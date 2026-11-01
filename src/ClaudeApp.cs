using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MultiClaude;

/// <summary>A running Claude desktop instance: its main (browser) process and the data folder it uses.</summary>
internal sealed record ClaudeInstance(int Pid, string? DataDir, string Exe);

/// <summary>Finds, starts, focuses and quits the official Claude desktop app. Never modifies it.</summary>
internal static class ClaudeApp
{
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The Store (MSIX) build's execution alias. Starting it runs Claude inside its package.</summary>
    static string StoreAlias => Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "claude-desktop.exe");

    /// <summary>The older Squirrel installer's launcher.</summary>
    static string SquirrelLauncher => Path.Combine(LocalAppData, "AnthropicClaude", "claude.exe");

    public static string? Launcher =>
        File.Exists(StoreAlias) ? StoreAlias :
        File.Exists(SquirrelLauncher) ? SquirrelLauncher : null;

    public static bool IsInstalled => Launcher != null;

    static readonly Regex DataDirArg = new(@"--user-data-dir=(?:""([^""]+)""|(\S+))", RegexOptions.Compiled);

    public static string? ParseDataDir(string commandLine)
    {
        var m = DataDirArg.Match(commandLine);
        if (!m.Success) return null;
        return Config.NormalizePath(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    /// <summary>Claude Desktop's own executable, as opposed to the Claude Code CLI, which is also claude.exe.</summary>
    public static bool IsDesktopExe(string exe) =>
        exe.EndsWith("\\claude.exe", StringComparison.OrdinalIgnoreCase) &&
        (exe.Contains("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) ||
         exe.Contains("\\AnthropicClaude\\", StringComparison.OrdinalIgnoreCase));

    // ---- process scan ----------------------------------------------------

    sealed record ProcInfo(string? Exe, string? CommandLine, int Parent);
    static readonly Dictionary<int, ProcInfo> Cache = new();

    /// <summary>Running Claude instances. Command lines are read once per process id and cached.</summary>
    public static List<ClaudeInstance> Scan(out string? defaultDataDir)
    {
        var procs = Process.GetProcessesByName("claude");
        var alive = new HashSet<int>();
        foreach (var p in procs)
        {
            alive.Add(p.Id);
            if (!Cache.ContainsKey(p.Id))
            {
                var (exe, cmd, parent) = Native.QueryProcess(p.Id);
                Cache[p.Id] = new ProcInfo(exe, cmd, parent);
            }
            p.Dispose();
        }
        foreach (int gone in Cache.Keys.Where(k => !alive.Contains(k)).ToList()) Cache.Remove(gone);

        var result = new List<ClaudeInstance>();
        defaultDataDir = null;
        foreach (var (pid, info) in Cache)
        {
            if (info.Exe == null || info.CommandLine == null || !IsDesktopExe(info.Exe)) continue;
            if (info.CommandLine.Contains("--type=")) continue;
            string? dir = ParseDataDir(info.CommandLine);
            result.Add(new ClaudeInstance(pid, dir, info.Exe));

            // Claude passes its real data folder to its helpers, so the default account's
            // folder can be read from the children of the instance started without one.
            if (dir == null)
            {
                foreach (var child in Cache.Values)
                    if (child.Parent == pid && child.CommandLine != null && ParseDataDir(child.CommandLine) is string d)
                    { defaultDataDir = d; break; }
            }
        }
        return result;
    }

    // ---- launch ----------------------------------------------------------

    /// <summary>
    /// Starts Claude on a data folder. If that folder is already open, Claude's single-instance
    /// handling brings its window forward instead of opening a second copy.
    /// </summary>
    public static void Launch(string? dataDir, string? link = null)
    {
        string launcher = Launcher ?? throw new InvalidOperationException(
            "Claude for Windows is not installed. Get it from claude.ai/download.");
        if (dataDir != null) Directory.CreateDirectory(dataDir);

        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        var args = new List<string>();
        if (dataDir != null) args.Add($"--user-data-dir=\"{dataDir}\"");
        if (link != null) args.Add($"\"{link}\"");
        var psi = new ProcessStartInfo(launcher, string.Join(' ', args)) { UseShellExecute = false, WorkingDirectory = Config.Home };
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Brings an already visible window forward right away; Launch covers the hidden-to-tray case.</summary>
    public static void FocusWindows(int pid)
    {
        var win = Native.AppWindows(pid).FirstOrDefault(Native.IsWindowVisible);
        if (win == IntPtr.Zero) return;
        if (Native.IsIconic(win)) Native.ShowWindow(win, Native.SW_RESTORE);
        Native.SetForegroundWindow(win);
    }

    /// <summary>Quits one instance with all of its helpers.</summary>
    public static void Quit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    // ---- package info ----------------------------------------------------

    /// <summary>The app's install folder, for its version and logo.</summary>
    public static string? InstallDir(IEnumerable<ClaudeInstance> running)
    {
        var exe = running.Select(r => r.Exe).FirstOrDefault();
        if (exe != null)
        {
            string? dir = Path.GetDirectoryName(exe);
            // Store build: <package>\app\claude.exe
            if (dir != null && Path.GetFileName(dir).Equals("app", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);
            return dir;
        }
        return StorePackageDir();
    }

    static string? StorePackageDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (key == null) return null;
            string? best = null; Version bestVersion = new();
            foreach (string name in key.GetSubKeyNames())
            {
                if (!name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = name.Split('_');
                if (parts.Length < 2 || !Version.TryParse(parts[1], out var v) || v <= bestVersion) continue;
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("PackageRootFolder") is string root && Directory.Exists(root))
                { best = root; bestVersion = v; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Claude's own square logo, the base for each profile's recolored icon.</summary>
    public static Bitmap? LoadLogo(string? installDir)
    {
        installDir ??= StorePackageDir();
        if (installDir != null)
        {
            foreach (string name in new[] { "Square44x44Logo.targetsize-256.png", "Square150x150Logo.scale-200.png", "Square150x150Logo.png" })
            {
                string path = Path.Combine(installDir, "assets", name);
                try { if (File.Exists(path)) return new Bitmap(path); } catch { }
            }
        }
        try
        {
            string? exe = Launcher == SquirrelLauncher ? SquirrelLauncher : null;
            if (exe != null && Icon.ExtractAssociatedIcon(exe) is Icon ic) { using (ic) return new Icon(ic, 256, 256).ToBitmap(); }
        }
        catch { }
        return null;
    }

    // ---- data folders ----------------------------------------------------

    /// <summary>The default account's data folder: what a running Claude reported, else the usual places.</summary>
    public static string? DefaultDataDir(string? lastSeen)
    {
        if (lastSeen != null && Directory.Exists(lastSeen)) return lastSeen;
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string> { Path.Combine(roaming, "Claude") };
        string packages = Path.Combine(LocalAppData, "Packages");
        if (Directory.Exists(packages))
            foreach (string pkg in Directory.GetDirectories(packages, "Claude_*"))
                candidates.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude"));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    public static string DesktopConfigPath(string dataDir) => Path.Combine(dataDir, "claude_desktop_config.json");

    /// <summary>
    /// Settings that fix how Claude draws on this PC rather than describe an account. If the user turned
    /// off hardware acceleration in their default Claude (the usual fix for a laggy window), a profile
    /// with its own fresh settings file would lag again; carry the choice over until the profile sets its own.
    /// </summary>
    static readonly string[] DisplaySettings = { "isHardwareAccelerationDisabled" };

    public static void InheritDisplaySettings(string? defaultDir, string profileDir)
    {
        if (defaultDir == null || Config.SamePath(defaultDir, profileDir)) return;
        try
        {
            string srcPath = DesktopConfigPath(defaultDir);
            if (!File.Exists(srcPath) || JsonNode.Parse(File.ReadAllText(srcPath)) is not JsonObject src) return;

            string dstPath = DesktopConfigPath(profileDir);
            var dst = File.Exists(dstPath) ? JsonNode.Parse(File.ReadAllText(dstPath)) as JsonObject : new JsonObject();
            if (dst == null) return;   // unreadable: leave the profile's file alone

            bool changed = false;
            foreach (string key in DisplaySettings)
                if (src[key] is JsonNode value && !dst.ContainsKey(key)) { dst[key] = value.DeepClone(); changed = true; }
            if (!changed) return;

            Directory.CreateDirectory(profileDir);
            File.WriteAllText(dstPath, dst.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("display settings: " + ex.Message); }
    }
}
