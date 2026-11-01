using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MultiClaude;

internal sealed class TrayApp : ApplicationContext
{
    public Config Config { get; }

    readonly NotifyIcon tray;
    readonly PanelForm panel;
    readonly System.Windows.Forms.Timer poll;
    readonly HotkeyWindow hotkeys;
    readonly SynchronizationContext ui;

    /// <summary>Running instance per profile id.</summary>
    Dictionary<string, ClaudeInstance> running = new();
    public string? FrontProfileId { get; private set; }

    // taskbar tagging: window handle -> AppUserModelID we gave it
    readonly Dictionary<IntPtr, string> tagged = new();

    // update recovery
    string? lastInstallDir;
    readonly Dictionary<string, DateTime> closedAt = new();
    readonly HashSet<string> quitByUs = new();

    // keep awake
    DateTime? awakeUntil;

    // sign-in links: the account whose window was in front last
    readonly Native.WinEventProc foregroundProc;
    readonly LinkRouter links;
    string? lastClaudeProfileId;
    DateTime lastClaudeAt;

    string trayKey = "";
    IntPtr trayHandle;

    public TrayApp(string? openOnStart)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Config = Config.Load();
        foregroundProc = (_, _, _, _, _, _, _) => Poll();
        links = new LinkRouter((url, id) => ui.Post(_ => DeliverLink(url, id), null));
        links.Start();

        var instances = ClaudeApp.Scan(out var defaultDir);
        if (defaultDir != null) Config.DefaultDataDir = defaultDir;
        Config.Import(instances.Where(i => i.DataDir != null).Select(i => i.DataDir!));
        Icons.SetLogo(ClaudeApp.LoadLogo(ClaudeApp.InstallDir(instances)));
        lastInstallDir = ClaudeApp.InstallDir(instances);

        panel = new PanelForm(this);
        panel.CreateControl();
        _ = panel.Handle;

        tray = new NotifyIcon { Text = "MultiClaude", Visible = true };
        tray.MouseUp += Tray_MouseUp;

        hotkeys = new HotkeyWindow(OnHotkey);
        ApplyHotkeys();

        poll = new System.Windows.Forms.Timer { Interval = 1500 };
        poll.Tick += (_, _) => Poll();
        Poll();
        poll.Start();
        Native.OnForegroundChange(foregroundProc);

        SyncShortcuts();
        Config.Save();

        if (!ClaudeApp.IsInstalled)
            Notify("Claude isn't installed", "Install Claude for Windows from claude.ai/download, then open your accounts from here.");
        else if (Config.IsNew)
            Notify("MultiClaude is in your tray", "Click its icon to open your Claude accounts. Drag it out of the ^ overflow to keep it in view.");

        foreach (var p in Config.Profiles.Where(p => p.OpenAtStartup && !IsRunning(p)))
            OpenProfile(p, focus: false);
        if (openOnStart != null && Config.Find(openOnStart) is Profile start) OpenProfile(start);
        else if (Config.IsNew) ShowPanel(DefaultAnchor());

        PipeServer.Start(msg => ui.Post(_ => HandleMessage(msg), null));
    }

    // ---- status ----------------------------------------------------------

    public bool IsRunning(Profile p) => running.ContainsKey(p.Id);

    Profile? ProfileFor(ClaudeInstance inst)
    {
        if (inst.DataDir == null) return Config.Profiles.First(p => p.IsDefault);
        if (Config.FindByDataDir(inst.DataDir) is Profile p) return p;
        if (Config.DefaultDataDir != null && Config.SamePath(inst.DataDir, Config.DefaultDataDir))
            return Config.Profiles.First(x => x.IsDefault);
        return null;
    }

    void Poll()
    {
        List<ClaudeInstance> instances;
        string? defaultDir;
        try { instances = ClaudeApp.Scan(out defaultDir); }
        catch { return; }

        bool changed = false;
        if (defaultDir != null && defaultDir != Config.DefaultDataDir) { Config.DefaultDataDir = defaultDir; Config.Save(); }

        var unknown = instances.Where(i => i.DataDir != null && ProfileFor(i) == null).Select(i => i.DataDir!).ToList();
        if (unknown.Count > 0 && Config.Import(unknown)) { changed = true; SyncShortcuts(); }

        var now = new Dictionary<string, ClaudeInstance>();
        foreach (var inst in instances)
            if (ProfileFor(inst) is Profile p && !now.ContainsKey(p.Id)) now[p.Id] = inst;

        foreach (var id in running.Keys.Where(id => !now.ContainsKey(id)))
        {
            if (quitByUs.Remove(id)) closedAt.Remove(id);
            else closedAt[id] = DateTime.Now;
        }
        if (!now.Keys.ToHashSet().SetEquals(running.Keys)) changed = true;
        running = now;

        ReopenAfterUpdate(instances);

        // the account in front
        string? front = null;
        int fgPid = Native.WindowProcessId(Native.GetForegroundWindow());
        foreach (var (id, inst) in running) if (inst.Pid == fgPid) front = id;
        if (front != FrontProfileId) { FrontProfileId = front; changed = true; }
        if (front != null) { lastClaudeProfileId = front; lastClaudeAt = DateTime.Now; }
        UpdateLinkTarget();

        if (Config.Settings.SeparateTaskbarButtons) TagWindows();
        UpdateKeepAwake();
        UpdateTrayIcon();
        if (changed) panel.Refresh2();
    }

    /// <summary>
    /// Claude restarts every instance when it updates itself, and comes back on the default account
    /// only. Profiles that closed shortly before a new Claude version appeared are reopened.
    /// </summary>
    void ReopenAfterUpdate(List<ClaudeInstance> instances)
    {
        string? dir = ClaudeApp.InstallDir(instances);
        if (dir == null) return;
        bool updated = lastInstallDir != null && !string.Equals(dir, lastInstallDir, StringComparison.OrdinalIgnoreCase);
        lastInstallDir = dir;
        if (!updated) return;

        Icons.SetLogo(ClaudeApp.LoadLogo(dir));
        if (!Config.Settings.ReopenAfterUpdate) return;
        var reopen = closedAt.Where(kv => (DateTime.Now - kv.Value).TotalMinutes < 10)
            .Select(kv => Config.Find(kv.Key)).OfType<Profile>().Where(p => !p.IsDefault && !IsRunning(p)).ToList();
        closedAt.Clear();
        if (reopen.Count == 0) return;
        Notify("Claude updated", "Reopening " + string.Join(", ", reopen.Select(p => p.Name)) + " on their own accounts.");
        Stagger(reopen, p => OpenProfile(p, focus: false));
    }

    // ---- taskbar identity ------------------------------------------------

    public static string AppIdFor(Profile p) => "MultiClaude.Profile." + p.Id;

    /// <summary>
    /// Every Claude window declares the same AppUserModelID, so all accounts would stack on one taskbar
    /// button. Each profile's windows get their own ID, icon and relaunch command instead.
    /// </summary>
    void TagWindows()
    {
        foreach (var gone in tagged.Keys.Where(h => !Native.IsWindowVisible(h) && Native.WindowProcessId(h) == 0).ToList())
            tagged.Remove(gone);

        string exe = Environment.ProcessPath ?? "";
        foreach (var (id, inst) in running)
        {
            var p = Config.Find(id);
            if (p == null || p.IsDefault) continue;
            string appId = AppIdFor(p);
            foreach (var hwnd in Native.AppWindows(inst.Pid))
            {
                if (!Native.IsWindowVisible(hwnd)) continue;
                if (tagged.TryGetValue(hwnd, out var have) && have == appId + p.Color) continue;
                bool ok = Native.SetWindowAppId(hwnd, appId, $"\"{exe}\" --open {p.Id}", "Claude " + p.Name, Icons.ProfileIconFile(p));
                if (ok)
                {
                    Native.SetWindowIcons(hwnd, Icons.ProfileIcon(p, false), Icons.ProfileIcon(p, true));
                    Native.RefreshTaskbarButton(hwnd);
                }
                tagged[hwnd] = appId + p.Color;
                if (!ok) Log.Write($"couldn't give {p.Name}'s window its own taskbar button");
            }
        }
    }

    // ---- open / quit -----------------------------------------------------

    public void OpenProfile(Profile p, bool focus = true)
    {
        try
        {
            if (focus && running.TryGetValue(p.Id, out var inst)) ClaudeApp.FocusWindows(inst.Pid);
            else if (p.DataDir != null) ClaudeApp.InheritDisplaySettings(ClaudeApp.DefaultDataDir(Config.DefaultDataDir), p.DataDir);
            ClaudeApp.Launch(p.DataDir);
            closedAt.Remove(p.Id);
            quitByUs.Remove(p.Id);
        }
        catch (Exception ex) { Error("Couldn't open " + p.Name, ex.Message); }
    }

    public void QuitProfile(Profile p)
    {
        if (!running.TryGetValue(p.Id, out var inst)) return;
        quitByUs.Add(p.Id);
        ClaudeApp.Quit(inst.Pid);
        running.Remove(p.Id);
        panel.Refresh2();
        UpdateTrayIcon();
    }

    public void OpenAll() => Stagger(Config.Profiles.Where(p => !IsRunning(p)).ToList(), p => OpenProfile(p, focus: false));

    public void QuitAll()
    {
        foreach (var p in Config.Profiles.Where(IsRunning).ToList()) QuitProfile(p);
    }

    /// <summary>Opens one account with every other one closed, so a browser sign-in link can only land there.</summary>
    void OpenAlone(Profile p)
    {
        foreach (var other in Config.Profiles.Where(o => o != p && IsRunning(o)).ToList()) QuitProfile(other);
        Delay(1200, () => OpenProfile(p));
        Notify("Signing in to " + p.Name, "Your other accounts are closed so the browser's sign-in link reaches this one. Reopen them from the tray when you're done.");
    }

    void Stagger(List<Profile> list, Action<Profile> action)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            Delay(i * 700, () => action(p));
        }
    }

    static void Delay(int ms, Action action)
    {
        if (ms <= 0) { action(); return; }
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); action(); };
        t.Start();
    }

    // ---- profile edits ---------------------------------------------------

    public Profile CreateProfile(string name)
    {
        var p = Config.Add(name);
        SyncShortcuts();
        return p;
    }

    public void RenameProfile(Profile p, string name)
    {
        string old = p.Name;
        p.Name = Config.UniqueName(name);
        if (p.Name.Equals(old, StringComparison.OrdinalIgnoreCase) && name.Equals(old, StringComparison.OrdinalIgnoreCase)) p.Name = name;
        Config.Save();
        tagged.Clear();
        SyncShortcuts();
        panel.Refresh2();
    }

    void SetColor(Profile p, string hex)
    {
        p.Color = hex;
        Config.Save();
        tagged.Clear();
        SyncShortcuts();
        panel.Refresh2();
        UpdateTrayIcon(force: true);
    }

    void Move(Profile p, int delta)
    {
        int i = Config.Profiles.IndexOf(p), j = i + delta;
        if (p.IsDefault || j < 1 || j >= Config.Profiles.Count) return;
        Config.Profiles.RemoveAt(i);
        Config.Profiles.Insert(j, p);
        Config.Save();
        ApplyHotkeys();
        panel.Refresh2();
    }

    void Remove(Profile p)
    {
        var answer = panel.KeepOpen(() => MessageBox.Show(
            $"Take {p.Name} off the list?\n\nIts data folder, with its sign-in and chats, stays on disk:\n{p.DataDir}",
            "MultiClaude", MessageBoxButtons.OKCancel, MessageBoxIcon.Question));
        if (answer != DialogResult.OK) return;
        if (p.DataDir != null) Config.Ignored.Add(p.DataDir);
        Config.Profiles.Remove(p);
        Config.Save();
        SyncShortcuts();
        foreach (var f in Directory.Exists(Config.IconDir) ? Directory.GetFiles(Config.IconDir, p.Id + "-*.ico") : Array.Empty<string>())
            try { File.Delete(f); } catch { }
        panel.Refresh2();
    }

    void Duplicate(Profile p)
    {
        string? source = p.IsDefault ? ClaudeApp.DefaultDataDir(Config.DefaultDataDir) : p.DataDir;
        if (source == null || !Directory.Exists(source)) { Error("Nothing to copy", "That account has no data folder yet."); return; }
        if (IsRunning(p)) { Error("Quit " + p.Name + " first", "Claude keeps its files locked while it runs. Quit it, then duplicate it."); return; }

        var copy = Config.Add(p.Name + " Copy");
        copy.Color = p.IsDefault ? Palette.NextColor(Config.Profiles.Select(x => x.Color)) : p.Color;
        Config.Save();
        panel.Refresh2();
        Notify("Copying " + p.Name, "Your new profile " + copy.Name + " will be ready in a moment.");
        Task.Run(() => CopyData(source, copy.DataDir!)).ContinueWith(t => ui.Post(_ =>
        {
            if (t.Exception != null) Error("Copy failed", t.Exception.InnerException?.Message ?? "");
            else Notify(copy.Name + " is ready", "It has " + p.Name + "'s sign-in and chats. Right-click it to rename it.");
            SyncShortcuts();
            panel.Refresh2();
        }, null));
    }

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "GraphiteDawnCache", "ShaderCache", "Crashpad", "logs", "vm_bundles", "claude-code" };

    static void CopyData(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(f);
            if (name.Equals("SingletonLock", StringComparison.OrdinalIgnoreCase) || name.Equals("lockfile", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Copy(f, Path.Combine(to, name), true); } catch (IOException) { }
        }
        foreach (string d in Directory.GetDirectories(from))
        {
            if (SkipDirs.Contains(Path.GetFileName(d))) continue;
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyData(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    void CopyMcpServers(Profile from, Profile to)
    {
        string? src = from.IsDefault ? ClaudeApp.DefaultDataDir(Config.DefaultDataDir) : from.DataDir;
        string? dst = to.IsDefault ? ClaudeApp.DefaultDataDir(Config.DefaultDataDir) : to.DataDir;
        if (src == null || dst == null) { Error("Can't find the data folder", "Open both accounts once first."); return; }
        try
        {
            string srcPath = ClaudeApp.DesktopConfigPath(src);
            if (!File.Exists(srcPath)) { Error("No MCP servers", from.Name + " has no claude_desktop_config.json yet."); return; }
            var servers = JsonNode.Parse(File.ReadAllText(srcPath))?["mcpServers"];
            if (servers == null) { Error("No MCP servers", from.Name + " has no MCP servers set up."); return; }

            string dstPath = ClaudeApp.DesktopConfigPath(dst);
            JsonObject target = new();
            if (File.Exists(dstPath))
            {
                File.Copy(dstPath, dstPath + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak", true);
                target = JsonNode.Parse(File.ReadAllText(dstPath)) as JsonObject ?? new JsonObject();
            }
            target["mcpServers"] = servers.DeepClone();
            Directory.CreateDirectory(dst);
            File.WriteAllText(dstPath, target.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Notify("MCP servers copied", $"{to.Name} now has {from.Name}'s MCP servers" + (IsRunning(to) ? ". Quit and reopen it to load them." : "."));
        }
        catch (Exception ex) { Error("Couldn't copy MCP servers", ex.Message); }
    }

    // ---- Claude Code -----------------------------------------------------

    string? ClaudeCodeDir(Profile p)
    {
        if (p.IsDefault) return null;
        if (p.ClaudeCodeDir == null)
        {
            p.ClaudeCodeDir = Path.Combine(Config.Home, ".claude-" + p.Slug.ToLowerInvariant());
            Config.Save();
        }
        return p.ClaudeCodeDir;
    }

    string ClaudeCodeCommand(Profile p)
    {
        string? dir = ClaudeCodeDir(p);
        return dir == null ? "claude" : $"$env:CLAUDE_CONFIG_DIR = '{dir.Replace("'", "''")}'; claude";
    }

    void OpenClaudeCode(Profile p)
    {
        string script = $"$Host.UI.RawUI.WindowTitle = 'Claude Code - {p.Name.Replace("'", "''")}'; {ClaudeCodeCommand(p)}";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string shell = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")) ? "pwsh.exe" : "powershell.exe";
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            var psi = File.Exists(wt)
                ? new ProcessStartInfo(wt, $"new-tab --title \"Claude Code - {p.Name}\" -d \"{Config.Home}\" {shell} -NoExit -EncodedCommand {encoded}")
                : new ProcessStartInfo(shell, $"-NoExit -EncodedCommand {encoded}");
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Config.Home;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Error("Couldn't open a terminal", ex.Message); }
    }

    // ---- menus -----------------------------------------------------------

    public ContextMenuStrip BuildProfileMenu(Profile p)
    {
        var m = new ContextMenuStrip();
        bool on = IsRunning(p);
        m.Items.Add(on ? "Bring Forward" : "Open", null, (_, _) => { panel.HidePanel(); OpenProfile(p); });
        if (on) m.Items.Add("Quit", null, (_, _) => QuitProfile(p));
        m.Items.Add("Open Alone to Sign In", null, (_, _) => { panel.HidePanel(); OpenAlone(p); }).ToolTipText =
            "Closes your other accounts first, so Google or SSO sign-in returns to this one.";
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Rename…", null, (_, _) => panel.BeginEdit(p));
        var color = new ToolStripMenuItem("Color");
        foreach (var (name, hex) in Palette.Presets)
        {
            var item = new ToolStripMenuItem(name, Icons.Swatch(hex, 16), (_, _) => SetColor(p, hex))
            { Checked = p.Color.Equals(hex, StringComparison.OrdinalIgnoreCase) };
            color.DropDownItems.Add(item);
        }
        color.DropDownItems.Add(new ToolStripSeparator());
        color.DropDownItems.Add("Custom…", null, (_, _) =>
        {
            using var dlg = new ColorDialog { Color = Palette.Parse(p.Color), FullOpen = true };
            if (panel.KeepOpen(() => dlg.ShowDialog(panel)) == DialogResult.OK) SetColor(p, Palette.ToHex(dlg.Color));
        });
        m.Items.Add(color);
        if (!p.IsDefault)
        {
            int i = Config.Profiles.IndexOf(p);
            m.Items.Add("Move Up", null, (_, _) => Move(p, -1)).Enabled = i > 1;
            m.Items.Add("Move Down", null, (_, _) => Move(p, 1)).Enabled = i < Config.Profiles.Count - 1;
        }
        m.Items.Add(new ToolStripMenuItem("Open When MultiClaude Starts", null, (_, _) => { p.OpenAtStartup = !p.OpenAtStartup; Config.Save(); })
        { Checked = p.OpenAtStartup });
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Open Claude Code in Terminal", null, (_, _) => { panel.HidePanel(); OpenClaudeCode(p); });
        m.Items.Add("Copy Claude Code Command", null, (_, _) => { Clipboard.SetText(ClaudeCodeCommand(p)); Notify("Copied", "Paste it in PowerShell to run Claude Code as " + p.Name + "."); });
        var mcp = new ToolStripMenuItem("Copy MCP Servers From");
        foreach (var other in Config.Profiles.Where(o => o != p))
            mcp.DropDownItems.Add(other.Name, Icons.Swatch(other.Color, 16), (_, _) => CopyMcpServers(other, p));
        mcp.Enabled = mcp.DropDownItems.Count > 0;
        m.Items.Add(mcp);
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Show Data Folder", null, (_, _) =>
        {
            string? dir = p.IsDefault ? ClaudeApp.DefaultDataDir(Config.DefaultDataDir) : p.DataDir;
            if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"")?.Dispose();
            else Error("No data folder yet", "Open this account once to create it.");
        });
        m.Items.Add("Duplicate as New Profile", null, (_, _) => Duplicate(p));
        if (!p.IsDefault) m.Items.Add("Remove from List…", null, (_, _) => Remove(p));
        return m;
    }

    public ContextMenuStrip BuildSettingsMenu()
    {
        var s = Config.Settings;
        var m = new ContextMenuStrip();
        ToolStripMenuItem Toggle(string text, bool value, Action<bool> set, string? tip = null)
        {
            var item = new ToolStripMenuItem(text) { Checked = value, ToolTipText = tip };
            item.Click += (_, _) => { set(!value); Config.Save(); };
            m.Items.Add(item);
            return item;
        }

        Toggle("Launch at Sign-In", Startup.Enabled, v => Startup.Enabled = v);
        Toggle("Separate Taskbar Button per Account", s.SeparateTaskbarButtons, v => { s.SeparateTaskbarButtons = v; tagged.Clear(); },
            "Each account's windows get their own colored taskbar button. Turning it off applies the next time an account opens.");
        Toggle("Start Menu Shortcut per Account", s.StartMenuShortcuts, v => { s.StartMenuShortcuts = v; Config.Save(); SyncShortcuts(); },
            "\"Claude <name>\" in the Start menu, ready to pin to the taskbar.");
        Toggle("Send Sign-In Links to the Right Account", s.RouteSignInLinks, v => { s.RouteSignInLinks = v; UpdateLinkTarget(); },
            "Google and SSO sign-in come back to the Claude window you started them from, instead of the default account.");
        Toggle("Reopen Accounts After Claude Updates", s.ReopenAfterUpdate, v => s.ReopenAfterUpdate = v);
        Toggle("Quit Accounts with MultiClaude", s.QuitProfilesOnExit, v => s.QuitProfilesOnExit = v);
        m.Items.Add(new ToolStripSeparator());
        Toggle("Panel Shortcut (Ctrl+Alt+Shift+M)", s.PanelHotkey, v => { s.PanelHotkey = v; ApplyHotkeys(); });
        Toggle("Account Shortcuts (Ctrl+Alt+Shift+1–9)", s.ProfileHotkeys, v => { s.ProfileHotkeys = v; ApplyHotkeys(); panel.Refresh2(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open Profiles Folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Config.ProfilesRoot);
            Process.Start("explorer.exe", $"\"{Config.ProfilesRoot}\"")?.Dispose();
        });
        m.Items.Add("About MultiClaude", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Program.HomePage) { UseShellExecute = true })?.Dispose());
        return m;
    }

    public ContextMenuStrip BuildKeepAwakeMenu()
    {
        var m = new ContextMenuStrip();
        void Add(string text, TimeSpan? span)
        {
            m.Items.Add(text, null, (_, _) =>
            {
                awakeUntil = span == null ? null : span == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.Now + span;
                UpdateKeepAwake(); panel.Refresh2(); panel.Invalidate();
            });
        }
        if (KeepAwakeActive) { Add("Turn Off", null); m.Items.Add(new ToolStripSeparator()); }
        Add("For 30 Minutes", TimeSpan.FromMinutes(30));
        Add("For 1 Hour", TimeSpan.FromHours(1));
        Add("For 2 Hours", TimeSpan.FromHours(2));
        Add("For 4 Hours", TimeSpan.FromHours(4));
        Add("For 8 Hours", TimeSpan.FromHours(8));
        Add("Until Turned Off", TimeSpan.MaxValue);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Keep the Display On Too", null, (_, _) =>
        {
            Config.Settings.KeepDisplayOn = !Config.Settings.KeepDisplayOn; Config.Save(); UpdateKeepAwake();
        }) { Checked = Config.Settings.KeepDisplayOn });
        return m;
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var m = new ContextMenuStrip();
        foreach (var p in Config.Profiles)
        {
            var item = new ToolStripMenuItem(p.Name, Icons.Swatch(p.Color, 16), (_, _) => OpenProfile(p)) { Checked = IsRunning(p) };
            m.Items.Add(item);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("New Profile…", null, (_, _) => ShowPanel(DefaultAnchor(), startNewProfile: true));
        m.Items.Add(Config.Profiles.Any(IsRunning) ? "Quit All Accounts" : "Open All Accounts", null,
            (_, _) => { if (Config.Profiles.Any(IsRunning)) QuitAll(); else OpenAll(); });
        var settings = new ToolStripMenuItem("Settings");
        foreach (ToolStripItem i in BuildSettingsMenu().Items.Cast<ToolStripItem>().ToList()) settings.DropDownItems.Add(i);
        m.Items.Add(settings);
        var awake = new ToolStripMenuItem("Keep Awake" + (KeepAwakeActive ? " (" + KeepAwakeStatus + ")" : ""));
        foreach (ToolStripItem i in BuildKeepAwakeMenu().Items.Cast<ToolStripItem>().ToList()) awake.DropDownItems.Add(i);
        m.Items.Add(awake);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit MultiClaude", null, (_, _) => ExitApp());
        m.Renderer = new ThemedRenderer(Theme.Current());
        return m;
    }

    // ---- tray ------------------------------------------------------------

    void Tray_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (panel.Visible || panel.RecentlyHidden) panel.HidePanel();
            else ShowPanel(Cursor.Position);
        }
        else if (e.Button == MouseButtons.Right)
        {
            panel.HidePanel();
            var menu = BuildTrayMenu();
            menu.Closed += (_, _) => BeginDispose(menu);
            // NotifyIcon only positions its own ContextMenuStrip correctly; show it through the internal helper.
            tray.ContextMenuStrip = menu;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(tray, null);
        }
    }

    void BeginDispose(ContextMenuStrip menu) => Delay(200, () => { if (tray.ContextMenuStrip == menu) tray.ContextMenuStrip = null; menu.Dispose(); });

    void ShowPanel(Point anchor, bool startNewProfile = false)
    {
        Poll();
        panel.ShowNear(anchor, startNewProfile);
    }

    static Point DefaultAnchor()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        return new Point(wa.Right - 160, wa.Bottom - 10);
    }

    void UpdateTrayIcon(bool force = false)
    {
        int open = running.Count;
        var front = FrontProfileId != null ? Config.Find(FrontProfileId) : null;
        string color = front?.Color ?? Palette.ClaudeOrange;
        string key = $"{color}|{Math.Min(open, 2)}";
        if (key != trayKey || force)
        {
            trayKey = key;
            var icon = Icons.TrayIcon(Palette.Parse(color), open);
            var old = trayHandle;
            tray.Icon = icon;
            trayHandle = icon.Handle;
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        string text = open == 0 ? "MultiClaude: no accounts open" : $"MultiClaude: {open} open";
        if (front != null) text += $", {front.Name} in front";
        if (KeepAwakeActive) text += " · awake";
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    void Notify(string title, string text) => tray.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
    void Error(string title, string text) => tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning);

    // ---- keep awake ------------------------------------------------------

    public bool KeepAwakeActive => awakeUntil != null && awakeUntil > DateTime.Now;

    public string KeepAwakeStatus
    {
        get
        {
            if (!KeepAwakeActive) return "Off";
            if (awakeUntil == DateTime.MaxValue) return "On";
            var left = awakeUntil!.Value - DateTime.Now;
            return left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:D2}m left" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m left";
        }
    }

    void UpdateKeepAwake()
    {
        if (awakeUntil != null && !KeepAwakeActive) { awakeUntil = null; panel.Invalidate(); }
        uint flags = Native.ES_CONTINUOUS;
        if (KeepAwakeActive)
        {
            flags |= Native.ES_SYSTEM_REQUIRED;
            if (Config.Settings.KeepDisplayOn) flags |= Native.ES_DISPLAY_REQUIRED;
        }
        Native.SetThreadExecutionState(flags);
        if (KeepAwakeActive && panel.Visible) panel.Invalidate();
    }

    // ---- hotkeys ---------------------------------------------------------

    void ApplyHotkeys()
    {
        hotkeys.UnregisterAll();
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        if (Config.Settings.PanelHotkey) hotkeys.Register(1, mods, (uint)Keys.M);
        if (Config.Settings.ProfileHotkeys)
            for (int i = 0; i < 9; i++) hotkeys.Register(10 + i, mods, (uint)(Keys.D1 + i));
    }

    void OnHotkey(int id)
    {
        if (id == 1)
        {
            if (panel.Visible) panel.HidePanel(); else ShowPanel(DefaultAnchor());
        }
        else if (id >= 10 && id - 10 < Config.Profiles.Count)
            OpenProfile(Config.Profiles[id - 10]);
    }

    // ---- Start menu ------------------------------------------------------

    void SyncShortcuts()
    {
        try
        {
            string dir = Config.StartMenuDir;
            string exe = Environment.ProcessPath ?? "";
            if (!Config.Settings.StartMenuShortcuts)
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "Claude *.lnk")) File.Delete(f);
                return;
            }
            Directory.CreateDirectory(dir);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string self = Path.Combine(dir, "MultiClaude.lnk");
            Native.CreateShortcut(self, exe, "", exe, "Run several Claude accounts side by side", Program.AppId);
            foreach (var p in Config.Profiles.Where(p => !p.IsDefault))
            {
                string lnk = Path.Combine(dir, $"Claude {Config.Slugify(p.Name).Replace('-', ' ')}.lnk");
                wanted.Add(lnk);
                Native.CreateShortcut(lnk, exe, $"--open {p.Id}", Icons.ProfileIconFile(p), $"Claude, signed in as {p.Name}", AppIdFor(p));
            }
            foreach (string f in Directory.GetFiles(dir, "Claude *.lnk"))
                if (!wanted.Contains(f)) File.Delete(f);
        }
        catch (Exception ex) { Log.Write("shortcuts: " + ex); /* a convenience; the tray works without them */ }
    }

    // ---- messages and exit ----------------------------------------------

    /// <summary>
    /// The account that gets the next claude:// link: the one whose window was last in front, while
    /// it's open and it's not the default account (which gets links anyway). Null leaves links alone.
    /// </summary>
    void UpdateLinkTarget()
    {
        Profile? target = null;
        if (Config.Settings.RouteSignInLinks && lastClaudeProfileId != null &&
            (DateTime.Now - lastClaudeAt).TotalMinutes < 30 &&
            Config.Find(lastClaudeProfileId) is Profile p && !p.IsDefault && IsRunning(p))
            target = p;
        links.Target = target;
    }

    void DeliverLink(string url, string profileId)
    {
        if (Config.Find(profileId) is not Profile p) return;
        try { links.Deliver(url, p); }
        catch (Exception ex) { Error("Couldn't open the sign-in link", ex.Message); }
    }

    void HandleMessage(string msg)
    {
        if (msg.StartsWith("open ") && Config.Find(msg[5..].Trim()) is Profile p) OpenProfile(p);
        else if (msg == "show") ShowPanel(DefaultAnchor());
    }

    public void ExitApp()
    {
        poll.Stop();
        panel.HidePanel();
        if (Config.Settings.QuitProfilesOnExit)
            foreach (var p in Config.Profiles.Where(p => !p.IsDefault && IsRunning(p)).ToList()) QuitProfile(p);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        Config.Save();
        hotkeys.UnregisterAll();
        hotkeys.DestroyHandle();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

/// <summary>Turns "Launch at Sign-In" on and off through the per-user Run key.</summary>
internal static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("MultiClaude") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue("MultiClaude", $"\"{Environment.ProcessPath}\" --startup");
            else k.DeleteValue("MultiClaude", false);
        }
    }
}

internal sealed class HotkeyWindow : NativeWindow
{
    readonly Action<int> onHotkey;
    readonly List<int> ids = new();

    public HotkeyWindow(Action<int> onHotkey)
    {
        this.onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    public void Register(int id, uint mods, uint vk)
    {
        if (Native.RegisterHotKey(Handle, id, mods, vk)) ids.Add(id);
    }

    public void UnregisterAll()
    {
        foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
        ids.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) onHotkey(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}
