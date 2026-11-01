using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MultiClaude;

/// <summary>
/// Profile icons are Claude's own logo, read from the installed app and recolored, so no Claude
/// artwork ships with MultiClaude. Without the logo a plain drawn mark is used instead.
/// </summary>
internal static class Icons
{
    static Bitmap? logo;
    static readonly Dictionary<string, Icon> SmallCache = new();
    static readonly Dictionary<string, Icon> BigCache = new();

    public static void SetLogo(Bitmap? bmp) { logo?.Dispose(); logo = bmp; }

    // ---- profile icons ---------------------------------------------------

    public static Bitmap ProfileBitmap(string hex, int size)
    {
        var target = Palette.Parse(hex);
        using var src = logo != null ? Recolor(logo, target) : DrawMark(target, 256);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
        return bmp;
    }

    /// <summary>Writes a multi-size .ico for a profile and returns its path. Rewritten when the color changes.</summary>
    public static string ProfileIconFile(Profile p)
    {
        Directory.CreateDirectory(Config.IconDir);
        string path = Path.Combine(Config.IconDir, $"{p.Id}-{p.Color.TrimStart('#')}.ico");
        if (!File.Exists(path))
        {
            foreach (string old in Directory.GetFiles(Config.IconDir, p.Id + "-*.ico"))
                try { File.Delete(old); } catch { }
            var frames = new[] { 16, 20, 24, 32, 40, 48, 64, 256 }.Select(s => ProfileBitmap(p.Color, s)).ToList();
            WriteIco(path, frames);
            frames.ForEach(f => f.Dispose());
        }
        return path;
    }

    public static Icon ProfileIcon(Profile p, bool big)
    {
        var cache = big ? BigCache : SmallCache;
        if (cache.TryGetValue(p.Color, out var icon)) return icon;
        int size = big ? 32 : 16;
        size = (int)(size * (Screen.PrimaryScreen?.Bounds.Width > 0 ? DpiScale() : 1));
        using var bmp = ProfileBitmap(p.Color, size);
        icon = Icon.FromHandle(bmp.GetHicon());
        cache[p.Color] = icon;
        return icon;
    }

    static float DpiScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    /// <summary>Moves every colored pixel to the target hue, scaled so Claude's brand color lands exactly on it.</summary>
    static Bitmap Recolor(Bitmap source, Color target)
    {
        var brand = Palette.Parse(Palette.ClaudeOrange);
        RgbToHsv(brand, out _, out double bs, out double bv);
        RgbToHsv(target, out double th, out double ts, out double tv);

        var bmp = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(source, 0, 0, source.Width, source.Height);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * data.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a == 0) continue;
            var c = Color.FromArgb(px[i + 2], px[i + 1], px[i]);
            RgbToHsv(c, out _, out double s, out double v);
            if (s < 0.12) continue; // the white glyph and its soft edge stay as they are
            double ns = Math.Clamp(s * ts / Math.Max(bs, 0.01), 0, 1);
            double nv = Math.Clamp(v * tv / Math.Max(bv, 0.01), 0, 1);
            var n = HsvToRgb(th, ns, nv);
            px[i] = n.B; px[i + 1] = n.G; px[i + 2] = n.R;
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    /// <summary>A rounded tile with a twelve-spoke burst, used when Claude's logo can't be read.</summary>
    static Bitmap DrawMark(Color color, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = size * 0.22f;
        using (var path = RoundedRect(new RectangleF(0, 0, size, size), r))
        using (var fill = new SolidBrush(color))
            g.FillPath(fill, path);
        DrawBurst(g, size / 2f, size / 2f, size * 0.34f, size * 0.07f, Color.White);
        return bmp;
    }

    static void DrawBurst(Graphics g, float cx, float cy, float radius, float width, Color color)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6 + 0.13;
            float len = radius * (i % 2 == 0 ? 1f : 0.82f);
            g.DrawLine(pen, cx, cy, cx + (float)(Math.Cos(a) * len), cy + (float)(Math.Sin(a) * len));
        }
    }

    // ---- tray icon -------------------------------------------------------

    /// <summary>Two stacked tiles: "several Claudes". The front tile takes the active account's color.</summary>
    public static Icon TrayIcon(Color front, int openCount)
    {
        int size = Math.Max(16, (int)Math.Round(16 * DpiScale()));
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size;
            var back = new RectangleF(s * 0.30f, 0, s * 0.70f, s * 0.70f);
            var fore = new RectangleF(0, s * 0.30f, s * 0.70f, s * 0.70f);
            using (var path = RoundedRect(back, s * 0.16f))
            using (var b = new SolidBrush(Color.FromArgb(openCount > 1 ? 255 : 150, Lighten(front, 0.35f))))
                g.FillPath(b, path);
            using (var path = RoundedRect(fore, s * 0.16f))
            using (var b = new SolidBrush(front))
            using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0), Math.Max(1f, s / 24f)))
            {
                g.FillPath(b, path);
                g.DrawPath(edge, path);
            }
            DrawBurst(g, fore.X + fore.Width / 2, fore.Y + fore.Height / 2, fore.Width * 0.36f, Math.Max(1.2f, s * 0.07f), Color.White);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static Bitmap Swatch(string hex, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Palette.Parse(hex));
        g.FillEllipse(b, 1, 1, size - 2, size - 2);
        return bmp;
    }

    /// <summary>Writes the app's own icon; used once at build time (MultiClaude.exe --write-app-icon path).</summary>
    public static void WriteAppIcon(string path)
    {
        var frames = new[] { 16, 24, 32, 48, 64, 128, 256 }.Select(s =>
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var front = Palette.Parse(Palette.ClaudeOrange);
            var back = new RectangleF(s * 0.28f, s * 0.02f, s * 0.70f, s * 0.70f);
            var fore = new RectangleF(s * 0.02f, s * 0.28f, s * 0.70f, s * 0.70f);
            using (var p = RoundedRect(back, s * 0.16f)) using (var br = new SolidBrush(Palette.Parse("#3B82F6"))) g.FillPath(br, p);
            using (var p = RoundedRect(fore, s * 0.16f)) using (var br = new SolidBrush(front)) g.FillPath(br, p);
            DrawBurst(g, fore.X + fore.Width / 2, fore.Y + fore.Height / 2, fore.Width * 0.36f, Math.Max(1.2f, s * 0.06f), Color.White);
            return bmp;
        }).ToList();
        WriteIco(path, frames);
        frames.ForEach(f => f.Dispose());
    }

    // ---- helpers ---------------------------------------------------------

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Color Lighten(Color c, float amount) => Color.FromArgb(
        (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    /// <summary>A .ico whose frames are PNG-compressed, which Windows Vista and later read at every size.</summary>
    static void WriteIco(string path, IList<Bitmap> frames)
    {
        var pngs = frames.Select(f => { using var ms = new MemoryStream(); f.Save(ms, ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = frames[i].Width;
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
