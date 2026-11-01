using Microsoft.Win32;

namespace MultiClaude;

internal sealed class Theme
{
    public bool Dark { get; private init; }
    public Color Background { get; private init; }
    public Color Text { get; private init; }
    public Color Muted { get; private init; }
    public Color Hover { get; private init; }
    public Color Divider { get; private init; }
    public Color Field { get; private init; }
    public Color Accent { get; private init; }

    public static Theme Current()
    {
        bool dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { }

        return dark
            ? new Theme
            {
                Dark = true,
                Background = Color.FromArgb(32, 32, 32),
                Text = Color.FromArgb(240, 240, 240),
                Muted = Color.FromArgb(160, 160, 160),
                Hover = Color.FromArgb(52, 52, 52),
                Divider = Color.FromArgb(58, 58, 58),
                Field = Color.FromArgb(45, 45, 45),
                Accent = Palette.Parse(Palette.ClaudeOrange),
            }
            : new Theme
            {
                Dark = false,
                Background = Color.FromArgb(249, 249, 249),
                Text = Color.FromArgb(26, 26, 26),
                Muted = Color.FromArgb(110, 110, 110),
                Hover = Color.FromArgb(234, 234, 234),
                Divider = Color.FromArgb(225, 225, 225),
                Field = Color.White,
                Accent = Palette.Parse(Palette.ClaudeOrange),
            };
    }
}

/// <summary>Context menus that follow the light or dark theme.</summary>
internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    readonly Theme t;
    public ThemedRenderer(Theme theme) : base(new ThemedColors(theme)) { t = theme; RoundedEdges = true; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Text : t.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        r.Inflate(-1, -1);
        using var pen = new Pen(t.Text, 1.6f);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
            new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
        });
    }

    sealed class ThemedColors : ProfessionalColorTable
    {
        readonly Theme t;
        public ThemedColors(Theme theme) { t = theme; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => t.Background;
        public override Color ImageMarginGradientBegin => t.Background;
        public override Color ImageMarginGradientMiddle => t.Background;
        public override Color ImageMarginGradientEnd => t.Background;
        public override Color MenuBorder => t.Divider;
        public override Color MenuItemBorder => t.Hover;
        public override Color MenuItemSelected => t.Hover;
        public override Color MenuItemSelectedGradientBegin => t.Hover;
        public override Color MenuItemSelectedGradientEnd => t.Hover;
        public override Color SeparatorDark => t.Divider;
        public override Color SeparatorLight => t.Divider;
        public override Color CheckBackground => t.Hover;
        public override Color CheckSelectedBackground => t.Hover;
        public override Color CheckPressedBackground => t.Hover;
    }
}
