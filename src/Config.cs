using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiClaude;

internal sealed class Profile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public string Color { get; set; } = Palette.ClaudeOrange;
    /// <summary>Claude's --user-data-dir for this account. Null for the default account.</summary>
    public string? DataDir { get; set; }
    public bool OpenAtStartup { get; set; }
    /// <summary>CLAUDE_CONFIG_DIR for Claude Code in a terminal, fixed on first use so renames keep the login.</summary>
    public string? ClaudeCodeDir { get; set; }

    [JsonIgnore] public bool IsDefault => DataDir == null;

    /// <summary>Folder-safe version of the name, used for data, Claude Code and icon paths.</summary>
    [JsonIgnore] public string Slug => Config.Slugify(Name);

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

internal sealed class Settings
{
    public bool SeparateTaskbarButtons { get; set; } = true;
    public bool StartMenuShortcuts { get; set; } = true;
    public bool ReopenAfterUpdate { get; set; } = true;
    public bool QuitProfilesOnExit { get; set; }
    public bool PanelHotkey { get; set; } = true;
    public bool ProfileHotkeys { get; set; }
    public bool KeepDisplayOn { get; set; }
    public bool RouteSignInLinks { get; set; } = true;
}

internal sealed class Config
{
    public List<Profile> Profiles { get; set; } = new();
    public Settings Settings { get; set; } = new();
    /// <summary>Data folders the user removed from the list, so import does not add them back.</summary>
    public List<string> Ignored { get; set; } = new();
    /// <summary>The default account's data folder, as last seen on a running Claude.</summary>
    public string? DefaultDataDir { get; set; }

    // ---- locations -------------------------------------------------------

    public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiClaude");
    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");
    public static string IconDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiClaude", "Icons");
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Profile data lives outside AppData on purpose: the Store build of Claude runs in a package that
    /// virtualizes writes under AppData, which would hide the folder and delete it on app reset.
    /// </summary>
    public static string ProfilesRoot => Path.Combine(Home, ".multiclaude", "profiles");

    public static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MultiClaude");

    // ---- load / save -----------------------------------------------------

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonIgnore] public bool IsNew { get; private set; }

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json);
                if (c != null) { c.EnsureDefault(); return c; }
            }
        }
        catch { /* a broken file starts over; the old one is kept below */ }

        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".bak", true);
        var fresh = new Config { IsNew = true };
        fresh.EnsureDefault();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, ConfigPath, true);
    }

    void EnsureDefault()
    {
        if (!Profiles.Any(p => p.IsDefault))
            Profiles.Insert(0, new Profile { Name = "Default", Color = Palette.ClaudeOrange, DataDir = null });
    }

    // ---- profiles --------------------------------------------------------

    public Profile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    public Profile? FindByDataDir(string dir) =>
        Profiles.FirstOrDefault(p => p.DataDir != null && SamePath(p.DataDir, dir));

    public Profile Add(string name)
    {
        name = UniqueName(name.Trim());
        string dir = Path.Combine(ProfilesRoot, "Claude-" + Slugify(name));
        for (int i = 2; Directory.Exists(dir) || FindByDataDir(dir) != null; i++)
            dir = Path.Combine(ProfilesRoot, $"Claude-{Slugify(name)}-{i}");
        Directory.CreateDirectory(dir);
        var p = new Profile { Name = name, DataDir = dir, Color = Palette.NextColor(Profiles.Select(x => x.Color)) };
        Profiles.Add(p);
        Save();
        return p;
    }

    public string UniqueName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Profile";
        string candidate = name;
        for (int i = 2; Profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    /// <summary>Picks up Claude-* folders in the profiles root, plus any folder a running Claude uses.</summary>
    public bool Import(IEnumerable<string> runningDataDirs)
    {
        var found = new List<string>();
        foreach (string root in new[] { ProfilesRoot, Path.Combine(Home, ".multi-claude", "data") })
        {
            if (!Directory.Exists(root)) continue;
            found.AddRange(Directory.GetDirectories(root, "Claude-*"));
        }
        found.AddRange(runningDataDirs);

        bool changed = false;
        foreach (string dir in found)
        {
            if (FindByDataDir(dir) != null) continue;
            if (Ignored.Any(i => SamePath(i, dir))) continue;
            if (DefaultDataDir != null && SamePath(DefaultDataDir, dir)) continue;
            string folder = Path.GetFileName(dir.TrimEnd('\\'));
            string name = folder.StartsWith("Claude-", StringComparison.OrdinalIgnoreCase) ? folder[7..] : folder;
            Profiles.Add(new Profile
            {
                Name = UniqueName(name.Replace('-', ' ')),
                DataDir = dir,
                Color = Palette.NextColor(Profiles.Select(x => x.Color)),
            });
            changed = true;
        }
        if (changed) Save();
        return changed;
    }

    // ---- helpers ---------------------------------------------------------

    public static string Slugify(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        string s = new string(chars);
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length == 0 ? "Profile" : s;
    }

    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\'); }
        catch { return path.Trim().Trim('"').TrimEnd('\\'); }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
}

internal static class Log
{
    static string PathName => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiClaude", "log.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            var info = new FileInfo(PathName);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}

internal static class Palette
{
    public const string ClaudeOrange = "#D97757";

    public static readonly (string Name, string Hex)[] Presets =
    {
        ("Claude", ClaudeOrange),
        ("Blue", "#3B82F6"),
        ("Green", "#22A06B"),
        ("Purple", "#8B5CF6"),
        ("Pink", "#EC4899"),
        ("Teal", "#14B8A6"),
        ("Red", "#E5484D"),
        ("Amber", "#F59E0B"),
        ("Gray", "#8A8F98"),
    };

    public static string NextColor(IEnumerable<string> used)
    {
        var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        foreach (var (_, hex) in Presets.Skip(1))
            if (!taken.Contains(hex)) return hex;
        return Presets[1 + taken.Count % (Presets.Length - 1)].Hex;
    }

    public static Color Parse(string hex)
    {
        try { return ColorTranslator.FromHtml(hex.StartsWith('#') ? hex : "#" + hex); }
        catch { return ColorTranslator.FromHtml(ClaudeOrange); }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
