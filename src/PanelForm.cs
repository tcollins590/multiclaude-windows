using System.Drawing.Drawing2D;

namespace MultiClaude;

/// <summary>
/// The tray panel: one row per account, a switch to open or quit them all, New Profile, Keep Awake,
/// Settings. Left-click a row to open or focus it, hover it to quit it, right-click it for more.
/// </summary>
internal sealed class PanelForm : Form
{
    enum Kind { Switch, Profile, NewProfile, KeepAwake, Settings, Quit }

    sealed record Item(Kind Kind, Rectangle Bounds, Profile? Profile = null);

    readonly TrayApp app;
    Theme theme = Theme.Current();
    readonly List<Item> items = new();
    readonly List<int> dividers = new();
    Item? hover;
    bool hoverClose;
    readonly TextBox editor;
    Profile? editing;   // null while editing means "new profile"
    bool isEditing;
    int suppressHide;
    DateTime hiddenAt;
    Rectangle headerRect;

    Font titleFont = null!, rowFont = null!, rowBold = null!, smallFont = null!;

    public PanelForm(TrayApp app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Text = "MultiClaude";

        editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false };
        editor.KeyDown += Editor_KeyDown;
        // Clicking away keeps a rename but drops a half-typed new profile.
        editor.LostFocus += (_, _) => { if (isEditing) EndEdit(commit: editing != null); };
        Controls.Add(editor);
        MakeFonts();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: not in Alt-Tab
            cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
            return cp;
        }
    }

    float K => DeviceDpi / 96f;
    int S(float v) => (int)Math.Round(v * K);

    void MakeFonts()
    {
        titleFont = new Font("Segoe UI Semibold", 10.5f);
        rowFont = new Font("Segoe UI", 9.75f);
        rowBold = new Font("Segoe UI Semibold", 9.75f);
        smallFont = new Font("Segoe UI", 8.25f);
    }

    // ---- show / hide -----------------------------------------------------

    public bool RecentlyHidden => (DateTime.Now - hiddenAt).TotalMilliseconds < 350;

    public void ShowNear(Point anchor, bool startNewProfile = false)
    {
        theme = Theme.Current();
        BackColor = theme.Background;
        editor.BackColor = theme.Field;
        editor.ForeColor = theme.Text;
        Layout2();
        Place(anchor);
        Show();
        Native.StyleWindow(Handle, theme.Dark);
        Activate();
        if (startNewProfile) BeginEdit(null);
    }

    public void HidePanel()
    {
        if (isEditing) EndEdit(commit: false);
        Hide();
        hiddenAt = DateTime.Now;
    }

    /// <summary>Runs a modal dialog or menu without the panel hiding itself when it loses focus.</summary>
    public T KeepOpen<T>(Func<T> action)
    {
        suppressHide++;
        try { return action(); }
        finally { suppressHide--; }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (suppressHide == 0) HidePanel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && !isEditing) HidePanel();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Layout2();
    }

    void Place(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var wa = screen.WorkingArea; var b = screen.Bounds;
        int m = S(10);
        int x, y;
        if (wa.Bottom < b.Bottom) { x = anchor.X - Width / 2; y = wa.Bottom - Height - m; }       // taskbar at the bottom
        else if (wa.Top > b.Top) { x = anchor.X - Width / 2; y = wa.Top + m; }                     // top
        else if (wa.Left > b.Left) { x = wa.Left + m; y = anchor.Y - Height / 2; }                  // left
        else { x = wa.Right - Width - m; y = anchor.Y - Height / 2; }                               // right or hidden
        x = Math.Clamp(x, wa.Left + m, wa.Right - Width - m);
        y = Math.Clamp(y, wa.Top + m, wa.Bottom - Height - m);
        Location = new Point(x, y);
    }

    /// <summary>Re-lays out after the profile list or status changed, keeping the panel's bottom edge in place.</summary>
    public void Refresh2()
    {
        if (!Visible) return;
        int bottom = Bottom;
        Layout2();
        if (Height != bottom - Top) Top = bottom - Height;
        if (isEditing) PositionEditor();
        Invalidate();
    }

    // ---- layout ----------------------------------------------------------

    void Layout2()
    {
        items.Clear(); dividers.Clear();
        int w = S(300), pad = S(6), row = S(36), y = S(8);

        headerRect = new Rectangle(0, y, w, S(48));
        items.Add(new Item(Kind.Switch, new Rectangle(w - S(16) - S(36), y + S(14), S(36), S(20))));
        y += S(52);
        dividers.Add(y); y += S(6);

        foreach (var p in app.Config.Profiles)
        {
            items.Add(new Item(Kind.Profile, new Rectangle(pad, y, w - 2 * pad, row), p));
            y += row;
        }
        items.Add(new Item(Kind.NewProfile, new Rectangle(pad, y, w - 2 * pad, row)));
        y += row + S(6);
        dividers.Add(y); y += S(6);

        int small = S(32);
        items.Add(new Item(Kind.KeepAwake, new Rectangle(pad, y, w - 2 * pad, small))); y += small;
        items.Add(new Item(Kind.Settings, new Rectangle(pad, y, w - 2 * pad, small))); y += small;
        items.Add(new Item(Kind.Quit, new Rectangle(pad, y, w - 2 * pad, small))); y += small;
        y += S(6);

        ClientSize = new Size(w, y);
    }

    Rectangle CloseRect(Rectangle row) => new(row.Right - S(30), row.Top + (row.Height - S(22)) / 2, S(22), S(22));

    // ---- painting --------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Background);

        // header
        int open = app.Config.Profiles.Count(app.IsRunning);
        TextRenderer.DrawText(g, "MultiClaude", titleFont, new Point(S(16), headerRect.Top + S(6)), theme.Text);
        string sub = open == 0 ? $"{Count(app.Config.Profiles.Count, "account")}, none open"
                   : $"{open} of {Count(app.Config.Profiles.Count, "account")} open";
        if (app.KeepAwakeActive) sub += "  ·  awake";
        TextRenderer.DrawText(g, sub, smallFont, new Point(S(16), headerRect.Top + S(28)), theme.Muted);

        using (var pen = new Pen(theme.Divider, 1))
            foreach (int d in dividers) g.DrawLine(pen, S(12), d, ClientSize.Width - S(12), d);

        foreach (var it in items)
        {
            if (it.Kind != Kind.Switch && hover == it && !(isEditing && editing == it.Profile && (it.Profile != null || it.Kind == Kind.NewProfile)))
                FillRounded(g, it.Bounds, S(6), theme.Hover);

            switch (it.Kind)
            {
                case Kind.Switch: DrawSwitch(g, it.Bounds, open > 0); break;
                case Kind.Profile: DrawProfile(g, it); break;
                case Kind.NewProfile:
                    if (!(isEditing && editing == null))
                        DrawLabel(g, it.Bounds, "+", "New Profile", theme.Muted);
                    break;
                case Kind.KeepAwake:
                    DrawLabel(g, it.Bounds, "☀", "Keep Awake", theme.Text, app.KeepAwakeStatus);
                    break;
                case Kind.Settings: DrawLabel(g, it.Bounds, "⚙", "Settings", theme.Text, "›"); break;
                case Kind.Quit: DrawLabel(g, it.Bounds, "⏻", "Quit MultiClaude", theme.Text); break;
            }
        }
    }

    static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    void DrawProfile(Graphics g, Item it)
    {
        var p = it.Profile!;
        var r = it.Bounds;
        bool running = app.IsRunning(p);
        bool front = app.FrontProfileId == p.Id;
        var color = Palette.Parse(p.Color);

        int d = S(12);
        var dot = new Rectangle(r.Left + S(12), r.Top + (r.Height - d) / 2, d, d);
        if (running)
        {
            using var b = new SolidBrush(color);
            g.FillEllipse(b, dot);
        }
        else
        {
            using var pen = new Pen(color, Math.Max(1.5f, K * 1.6f));
            var inner = dot; inner.Inflate(-1, -1);
            g.DrawEllipse(pen, inner);
        }

        if (isEditing && editing == p) return;

        int textLeft = r.Left + S(34);
        var font = running ? rowBold : rowFont;
        var size = TextRenderer.MeasureText(g, p.Name, font, Size.Empty, TextFormatFlags.NoPadding);
        int maxName = r.Width - S(34) - S(90);
        var nameRect = new Rectangle(textLeft, r.Top, Math.Min(size.Width + S(2), maxName), r.Height);
        TextRenderer.DrawText(g, p.Name, font, nameRect, theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        string? tag = p.IsDefault && !p.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "default" : null;
        if (front) tag = tag == null ? "in front" : tag + " · in front";
        if (tag != null)
            TextRenderer.DrawText(g, tag, smallFont, new Rectangle(nameRect.Right + S(8), r.Top, S(120), r.Height), theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (hover == it && running)
        {
            var cr = CloseRect(r);
            if (hoverClose) FillRounded(g, cr, cr.Width / 2, theme.Divider);
            using var pen = new Pen(theme.Text, Math.Max(1.2f, K * 1.3f));
            int q = S(6);
            g.DrawLine(pen, cr.Left + q, cr.Top + q, cr.Right - q, cr.Bottom - q);
            g.DrawLine(pen, cr.Right - q, cr.Top + q, cr.Left + q, cr.Bottom - q);
        }
        else
        {
            int index = app.Config.Profiles.IndexOf(p);
            if (app.Config.Settings.ProfileHotkeys && index < 9)
                TextRenderer.DrawText(g, $"Ctrl+Alt+Shift+{index + 1}", smallFont,
                    new Rectangle(r.Right - S(130), r.Top, S(118), r.Height), theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
    }

    void DrawLabel(Graphics g, Rectangle r, string glyph, string text, Color color, string? right = null)
    {
        TextRenderer.DrawText(g, glyph, rowFont, new Rectangle(r.Left + S(6), r.Top, S(24), r.Height), theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, text, rowFont, new Rectangle(r.Left + S(34), r.Top, r.Width - S(34), r.Height), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        if (right != null)
            TextRenderer.DrawText(g, right, smallFont, new Rectangle(r.Left, r.Top, r.Width - S(12), r.Height), theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    void DrawSwitch(Graphics g, Rectangle r, bool on)
    {
        var track = on ? theme.Accent : (theme.Dark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(200, 200, 200));
        if (hover?.Kind == Kind.Switch) track = ControlPaint.Light(track, 0.15f);
        FillRounded(g, r, r.Height / 2, track);
        int k = r.Height - S(6);
        var knob = new Rectangle(on ? r.Right - k - S(3) : r.Left + S(3), r.Top + S(3), k, k);
        using var b = new SolidBrush(Color.White);
        g.FillEllipse(b, knob);
    }

    static void FillRounded(Graphics g, Rectangle r, int radius, Color c)
    {
        using var path = Icons.RoundedRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    // ---- mouse -----------------------------------------------------------

    Item? HitTest(Point pt) => items.FirstOrDefault(i => i.Bounds.Contains(pt));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        bool hc = h?.Kind == Kind.Profile && CloseRect(h.Bounds).Contains(e.Location);
        if (h != hover || hc != hoverClose)
        {
            hover = h; hoverClose = hc;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != null) { hover = null; hoverClose = false; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var it = HitTest(e.Location);
        if (it == null) return;
        if (isEditing && !(it.Kind == Kind.Profile && it.Profile == editing)) EndEdit(commit: true);

        if (e.Button == MouseButtons.Right)
        {
            if (it.Kind == Kind.Profile) ShowMenu(app.BuildProfileMenu(it.Profile!), e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        switch (it.Kind)
        {
            case Kind.Switch:
                if (app.Config.Profiles.Any(app.IsRunning)) app.QuitAll(); else app.OpenAll();
                break;
            case Kind.Profile:
                var p = it.Profile!;
                if (app.IsRunning(p) && CloseRect(it.Bounds).Contains(e.Location)) app.QuitProfile(p);
                else { HidePanel(); app.OpenProfile(p); }
                break;
            case Kind.NewProfile: BeginEdit(null); break;
            case Kind.KeepAwake: ShowMenu(app.BuildKeepAwakeMenu(), new Point(it.Bounds.Left + S(30), it.Bounds.Bottom)); break;
            case Kind.Settings: ShowMenu(app.BuildSettingsMenu(), new Point(it.Bounds.Left + S(30), it.Bounds.Bottom)); break;
            case Kind.Quit: app.ExitApp(); break;
        }
    }

    void ShowMenu(ContextMenuStrip menu, Point at)
    {
        menu.Renderer = new ThemedRenderer(theme);
        menu.Closed += (_, _) => BeginInvoke(() => { if (Visible && !isEditing) Activate(); });
        menu.Show(this, at);
    }

    // ---- inline editing (new profile, rename) ---------------------------

    public void BeginEdit(Profile? profile)
    {
        if (!Visible) return;
        editing = profile;
        isEditing = true;
        editor.Font = rowFont;
        editor.Text = profile?.Name ?? "";
        PositionEditor();
        editor.Visible = true;
        editor.Focus();
        editor.SelectAll();
        Invalidate();
    }

    void PositionEditor()
    {
        var it = editing == null
            ? items.First(i => i.Kind == Kind.NewProfile)
            : items.FirstOrDefault(i => i.Profile == editing);
        if (it == null) { EndEdit(false); return; }
        int h = editor.PreferredHeight;
        editor.SetBounds(it.Bounds.Left + S(32), it.Bounds.Top + (it.Bounds.Height - h) / 2, it.Bounds.Width - S(44), h);
        editor.PlaceholderText = editing == null ? "Name, then Enter" : "";
    }

    void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(commit: true); }
        else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(commit: false); }
    }

    void EndEdit(bool commit)
    {
        if (!isEditing) return;
        isEditing = false;
        string name = editor.Text.Trim();
        var target = editing;
        editor.Visible = false;
        editing = null;
        ActiveControl = null;
        if (commit && name.Length > 0)
        {
            if (target == null)
            {
                var p = app.CreateProfile(name);
                HidePanel();
                app.OpenProfile(p);
                return;
            }
            if (name != target.Name) app.RenameProfile(target, name);
        }
        Refresh2();
        Invalidate();
    }
}
