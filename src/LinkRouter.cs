using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiClaude;

/// <summary>
/// Sends claude:// links to the right account: Google and SSO sign-in coming back from the browser,
/// and clicked notifications, which open a link to the session they're about.
///
/// Windows hands a claude:// link to a new Claude process started without a data folder. That
/// process lives for a second or two, then passes the link to the default account's window and
/// exits. While another account is open, MultiClaude watches for that short-lived process, stops it
/// before it passes the link on, and gives the link to the right account instead: the one whose data
/// folder holds the session the link names, or else the account last in front (for sign-in). If it
/// ever misses one, the link reaches the default account, as without MultiClaude. Nothing is
/// registered or changed in Windows for this.
/// </summary>
internal sealed class LinkRouter
{
    static readonly Regex LinkArg = new(@"(claude://[^\s""]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SessionId = new(@"\blocal_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly Action<string, string> deliver;  // (url, profile id), called on a worker thread
    readonly HashSet<int> seen = new();
    readonly Dictionary<string, DateTime> delivered = new();
    Thread? thread;

    /// <summary>The account that gets links that don't name a session (sign-in), or null to leave them alone.</summary>
    public volatile Profile? Target;

    /// <summary>The open accounts with their data folders, the default one included. Watching runs while one isn't the default.</summary>
    public volatile (Profile Profile, string DataDir)[] Open = Array.Empty<(Profile, string)>();

    public LinkRouter(Action<string, string> deliver) => this.deliver = deliver;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "MultiClaude links", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Run()
    {
        var refreshed = DateTime.MinValue;
        while (true)
        {
            if (Target == null && !Open.Any(o => !o.Profile.IsDefault)) { seen.Clear(); notifications = null; Thread.Sleep(500); continue; }
            try
            {
                if ((DateTime.Now - refreshed).TotalSeconds >= 1)
                {
                    notifications = NotificationHistory.Read() ?? notifications;
                    refreshed = DateTime.Now;
                }
                Scan();
            }
            catch (Exception ex) { Log.Write("link watch: " + ex.Message); }
            Thread.Sleep(120);
        }
    }

    /// <summary>Claude's notifications as of the last check, to spot the one a click removes.</summary>
    Dictionary<long, string?>? notifications;

    void Scan()
    {
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            int pid = p.Id;
            alive.Add(pid);
            if (seen.Add(pid)) Inspect(p);
            p.Dispose();
        }
        seen.IntersectWith(alive);
    }

    /// <summary>
    /// Who should get a link: the account holding the session it names, else the sign-in target.
    /// Null means leave it alone (it's the default account's, or nobody's in particular).
    /// </summary>
    Profile? Resolve(string url, out bool bySession)
    {
        bySession = true;
        var m = SessionId.Match(url);
        if (m.Success && OwnerOf(m.Value) is Profile owner) return owner.IsDefault ? null : owner;
        bySession = false;
        return Target;
    }

    /// <summary>The open account whose data folder holds a session (local_…), or null.</summary>
    Profile? OwnerOf(string sessionId)
    {
        string file = sessionId + ".json";
        foreach (var (profile, dir) in Open)
        {
            string sessions = Path.Combine(dir, "claude-code-sessions");
            if (!Directory.Exists(sessions)) continue;
            foreach (string account in Directory.EnumerateDirectories(sessions))
                foreach (string org in Directory.EnumerateDirectories(account))
                    if (File.Exists(Path.Combine(org, file)))
                        return profile;
        }
        return null;
    }

    void Inspect(Process p)
    {
        var (exe, cmd, parent) = Native.QueryProcess(p.Id);
        if (exe == null || cmd == null || !ClaudeApp.IsDesktopExe(exe)) return;
        // Only Windows' hand-off process: a plain Claude start with no data folder.
        if (cmd.Contains("--type=") || cmd.Contains("--user-data-dir") || cmd.Contains("claude-desktop.exe", StringComparison.OrdinalIgnoreCase)) return;
        var m = LinkArg.Match(cmd);
        if (!m.Success) { InspectActivation(p, parent); return; }
        string url = m.Groups[1].Value;
        lock (delivered)
        {
            foreach (var old in delivered.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > 15).Select(kv => kv.Key).ToList()) delivered.Remove(old);
            if (delivered.ContainsKey(url)) return;   // one we started ourselves
        }
        if (Resolve(url, out bool bySession) is not Profile target) return;

        try { p.Kill(entireProcessTree: true); }
        catch { return; }   // already gone: it passed the link on before we got to it
        Log.Write($"link -> {target.Name} ({(bySession ? "its session" : "last in front")}): {Redact(url)}");
        deliver(url, target.Id);
    }

    /// <summary>
    /// A plain Claude started by Windows (svchost) with no arguments: a clicked notification, or Claude
    /// opened from Start. It's stopped, then the notification that just left the history says which:
    /// with a session, that session opens in the account that owns it; otherwise Claude starts as asked.
    /// </summary>
    void InspectActivation(Process p, int parentPid)
    {
        var before = notifications;
        if (before == null || before.Count == 0) return;
        var (parentExe, _, _) = Native.QueryProcess(parentPid);
        if (parentExe == null || !Path.GetFileName(parentExe).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) return;

        try { p.Kill(entireProcessTree: true); }
        catch { return; }

        // Windows takes the clicked notification out of the history around the time it starts Claude.
        string? session = null;
        for (int i = 0; i < 15 && session == null; i++)
        {
            var now = NotificationHistory.Read();
            if (now != null)
            {
                var gone = before.Where(kv => !now.ContainsKey(kv.Key) && kv.Value != null).Select(kv => kv.Value!).Distinct().ToList();
                if (gone.Count == 1) session = gone[0];
                else if (gone.Count > 1) break;   // several cleared at once: can't tell which was clicked
            }
            if (session == null) Thread.Sleep(100);
        }

        if (session != null && OwnerOf(session) is Profile owner)
        {
            string url = "claude://claude.ai/epitaxy/" + session;
            Log.Write($"notification -> {owner.Name}: {url}");
            deliver(url, owner.Id);
        }
        else
        {
            Log.Write("plain Claude start, no notification matched: starting the default account");
            ClaudeApp.Launch(null);
        }
    }

    /// <summary>
    /// Gives a link to an account: starting Claude on the account's folder hands its arguments to
    /// that account's open window (Claude runs one instance per folder), which handles the link.
    /// </summary>
    public void Deliver(string url, Profile profile)
    {
        if (!url.StartsWith("claude:", StringComparison.OrdinalIgnoreCase) || url.Contains('"')) return;
        lock (delivered) delivered[url] = DateTime.Now;
        ClaudeApp.Launch(profile.DataDir, url);
    }

    /// <summary>Sign-in links carry one-time codes; keep only the scheme and path in the log.</summary>
    static string Redact(string url)
    {
        int q = url.IndexOfAny(new[] { '?', '#' });
        return q < 0 ? url : url[..q] + "?…";
    }
}
