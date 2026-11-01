using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiClaude;

/// <summary>
/// Sends claude:// links (Google and SSO sign-in coming back from the browser) to the right account.
///
/// Windows hands a claude:// link to a new Claude process started without a data folder. That
/// process lives for a second or two, then passes the link to the default account's window and
/// exits. While another account's window was the last Claude in front, MultiClaude watches for
/// that short-lived process, stops it before it passes the link on, and gives the link to that
/// account instead. If it ever misses one, the link reaches the default account, as without
/// MultiClaude. Nothing is registered or changed in Windows for this.
/// </summary>
internal sealed class LinkRouter
{
    static readonly Regex LinkArg = new(@"(claude://[^\s""]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly Action<string, string> deliver;  // (url, profile id), called on a worker thread
    readonly HashSet<int> seen = new();
    readonly Dictionary<string, DateTime> delivered = new();
    Thread? thread;

    /// <summary>The account that should get the next link, or null to leave links alone.</summary>
    public volatile Profile? Target;

    public LinkRouter(Action<string, string> deliver) => this.deliver = deliver;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "MultiClaude links", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Run()
    {
        while (true)
        {
            var target = Target;
            if (target == null) { seen.Clear(); Thread.Sleep(500); continue; }
            try { Scan(target); } catch (Exception ex) { Log.Write("link watch: " + ex.Message); }
            Thread.Sleep(120);
        }
    }

    void Scan(Profile target)
    {
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            int pid = p.Id;
            alive.Add(pid);
            if (seen.Add(pid)) Inspect(p, target);
            p.Dispose();
        }
        seen.IntersectWith(alive);
    }

    void Inspect(Process p, Profile target)
    {
        var (exe, cmd, _) = Native.QueryProcess(p.Id);
        if (exe == null || cmd == null || !ClaudeApp.IsDesktopExe(exe)) return;
        // Only Windows' hand-off process: a plain Claude start with a link and no data folder.
        if (cmd.Contains("--type=") || cmd.Contains("--user-data-dir") || cmd.Contains("claude-desktop.exe", StringComparison.OrdinalIgnoreCase)) return;
        var m = LinkArg.Match(cmd);
        if (!m.Success) return;
        string url = m.Groups[1].Value;
        lock (delivered)
        {
            foreach (var old in delivered.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > 15).Select(kv => kv.Key).ToList()) delivered.Remove(old);
            if (delivered.ContainsKey(url)) return;   // one we started ourselves
        }

        try { p.Kill(entireProcessTree: true); }
        catch { return; }   // already gone: it passed the link on before we got to it
        Log.Write($"link -> {target.Name}: {Redact(url)}");
        deliver(url, target.Id);
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
