using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiuLian.ToolBox.Desktop;

internal sealed class Palette
{
    public bool Dark { get; private set; }
    public bool Contrast { get; private set; }

    public bool Select(string preference)
    {
        var contrast = SystemInformation.HighContrast;
        var dark = preference == "Dark" || (preference != "Light" && WindowsUsesDarkApps());
        var changed = (dark, contrast) != (Dark, Contrast);
        Dark = dark;
        Contrast = contrast;
        return changed;
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    public void TitleBar(nint handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        int value = Dark && !Contrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }

    private Color Pick(int light, int dark, Color contrast) => Contrast ? contrast :
        Color.FromArgb(unchecked((int)0xff000000) | (Dark ? dark : light));

    public Color Canvas => Pick(0xf5f5fa, 0x181924, SystemColors.Window);
    public Color Surface => Pick(0xffffff, 0x242533, SystemColors.Window);
    public Color Ink => Pick(0x252539, 0xf4f1fb, SystemColors.WindowText);
    public Color Muted => Pick(0x5d6176, 0xb2b1c6, SystemColors.WindowText);
    public Color Line => Pick(0xe0dfeb, 0x454456, SystemColors.WindowFrame);
    public Color Field => Pick(0xfafafd, 0x1d1e2b, SystemColors.Window);
    public Color Accent => Pick(0x5b49bd, 0xc1b5ff, SystemColors.Highlight);
    public Color OnAccent => Pick(0xffffff, 0x221949, SystemColors.HighlightText);
    public Color Soft => Pick(0xeeeaff, 0x353048, SystemColors.Control);
    public Color AccentInk => Pick(0x5844b0, 0xd0c5ff, SystemColors.ControlText);
    public Color Success => Pick(0x187052, 0x8cd9b9, SystemColors.WindowText);
    public Color Warning => Pick(0x94530e, 0xffc97b, SystemColors.WindowText);
    public Color Danger => Pick(0xae354b, 0xff9fb0, SystemColors.WindowText);
    public Color Sidebar => Pick(0x211f33, 0x151421, SystemColors.Window);
    public Color SideText => Pick(0xf6f3ff, 0xf6f3ff, SystemColors.WindowText);
    public Color SideMuted => Pick(0xc2bdd5, 0xc2bdd5, SystemColors.WindowText);
    public Color SideSelected => Pick(0x443c61, 0x3c3657, SystemColors.Highlight);
    public Color SideSelectedText => Pick(0xffffff, 0xffffff, SystemColors.HighlightText);
    public Color SideLine => Pick(0x413c53, 0x373347, SystemColors.WindowFrame);
    public Color HeroLeft => Pick(0x302b53, 0x292542, SystemColors.Window);
    public Color HeroRight => Pick(0x53427c, 0x41355d, SystemColors.Window);
    public Color HeroInk => Pick(0xffffff, 0xfcf9ff, SystemColors.WindowText);
    public Color HeroMuted => Pick(0xd6cee9, 0xd6cee9, SystemColors.WindowText);
    public Color Mint => Pick(0xadf0cf, 0xadf0cf, SystemColors.WindowText);

    internal static Color Blend(Color background, Color foreground, float amount) => Color.FromArgb(
        (int)(background.R + (foreground.R - background.R) * amount),
        (int)(background.G + (foreground.G - background.G) * amount),
        (int)(background.B + (foreground.B - background.B) * amount));

    internal static GraphicsPath Round(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter < 1) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum Glyph
{
    Home, Monitor, Pair, Bot, Moon, Mail, Plug, Info, Link, Play, Plus, Remove, Refresh,
    Check, History, Settings, Apps, Share, Sound, Bell, Copy, Arrow, Unlink
}

internal static class Glyphs
{
    public static void Draw(Graphics g, Glyph glyph, RectangleF box, Color color)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(box.Width / 24f, box.Height / 24f);
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float x, float y, float x2, float y2) => g.DrawLine(pen, x, y, x2, y2);
        void Box(float x, float y, float w, float h, float r)
        { using var path = Palette.Round(new(x, y, w, h), r); g.DrawPath(pen, path); }
        void Circle(float x, float y, float d) => g.DrawEllipse(pen, x, y, d, d);
        switch (glyph)
        {
            case Glyph.Home:
                g.DrawLines(pen, [new Point(3, 10), new(12, 3), new(21, 10)]);
                g.DrawLines(pen, [new Point(5, 10), new(5, 20), new(10, 20), new(10, 14), new(14, 14), new(14, 20), new(19, 20), new(19, 10)]);
                break;
            case Glyph.Monitor:
                Box(2.5f, 4, 19, 13, 2); Line(12, 17, 12, 20.5f); Line(7.5f, 21, 16.5f, 21); break;
            case Glyph.Pair:
                Box(2, 3.5f, 13, 9.5f, 1.5f); Box(9, 11, 13, 9.5f, 1.5f); break;
            case Glyph.Bot:
                Box(3, 7, 18, 13, 4); Line(12, 3, 12, 7); Line(9.5f, 3, 12, 3);
                Line(8, 12, 8, 14); Line(16, 12, 16, 14); Line(10.5f, 16.5f, 13.5f, 16.5f); break;
            case Glyph.Moon:
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(18.5f, 15.5f, 9, 18, 6, 9, 10, 3);
                    path.AddBezier(10, 3, -2, 6, 3, 24, 15, 20);
                    path.AddBezier(15, 20, 17, 19.5f, 19, 17, 19.5f, 15); path.CloseFigure(); g.DrawPath(pen, path);
                }
                break;
            case Glyph.Mail:
                Box(2.5f, 5, 19, 14, 2); g.DrawLines(pen, [new Point(4, 7), new(12, 13), new(20, 7)]); break;
            case Glyph.Plug:
                Line(8, 3, 8, 8); Line(16, 3, 16, 8); Line(5, 8, 19, 8);
                g.DrawArc(pen, 6, 5, 12, 12, 0, 180); Line(6, 8, 6, 11); Line(18, 8, 18, 11); Line(12, 17, 12, 22); break;
            case Glyph.Info: Circle(3, 3, 18); Line(12, 11.5f, 12, 16); Line(12, 7.5f, 12, 7.8f); break;
            case Glyph.Link:
            case Glyph.Unlink:
                g.TranslateTransform(12, 12); g.RotateTransform(-43);
                Box(-10, -3.5f, 8, 7, 3); Box(2, -3.5f, 8, 7, 3);
                if (glyph == Glyph.Link) Line(-5, 0, 5, 0); else { Line(0, -5, 0, -8); Line(0, 5, 0, 8); }
                break;
            case Glyph.Play: g.DrawPolygon(pen, [new Point(7, 4), new(20, 12), new(7, 20)]); break;
            case Glyph.Plus: Line(5, 12, 19, 12); Line(12, 5, 12, 19); break;
            case Glyph.Remove:
                Line(4, 7, 20, 7); Line(9, 4, 15, 4); Box(6.5f, 7, 11, 13.5f, 2); Line(10, 10.5f, 10, 17); Line(14, 10.5f, 14, 17); break;
            case Glyph.Refresh:
                g.DrawArc(pen, 4, 4, 16, 16, 30, 282); Line(18, 7, 21, 7); Line(21, 7, 21, 3); break;
            case Glyph.Check: g.DrawLines(pen, [new Point(4, 12), new(9, 17), new(20, 6)]); break;
            case Glyph.History:
                g.DrawArc(pen, 4.5f, 3.5f, 16, 17, 190, 322); Line(2.5f, 6, 3, 11); Line(3, 11, 8, 10); Line(12, 7, 12, 12); Line(12, 12, 16, 14); break;
            case Glyph.Settings:
                Line(4, 7, 9, 7); Circle(9, 4.5f, 5); Line(14, 7, 20, 7);
                Line(4, 17, 13, 17); Circle(13, 14.5f, 5); Line(18, 17, 20, 17); break;
            case Glyph.Apps: Box(3, 3, 7, 7, 1.5f); Box(14, 3, 7, 7, 1.5f); Box(3, 14, 7, 7, 1.5f); Box(14, 14, 7, 7, 1.5f); break;
            case Glyph.Share:
                Box(2, 5, 20, 15, 2); Line(12, 16, 12, 9); Line(12, 9, 8.5f, 12); Line(12, 9, 15.5f, 12); break;
            case Glyph.Sound:
                Line(4, 10, 4, 14); Line(8, 6, 8, 18); Line(12, 3, 12, 21); Line(16, 7, 16, 17); Line(20, 10, 20, 14); break;
            case Glyph.Bell:
                g.DrawArc(pen, 6, 4, 12, 12, 180, 180); Line(6, 10, 5, 16); Line(18, 10, 19, 16);
                Line(4.5f, 17, 19.5f, 17); g.DrawArc(pen, 9.5f, 17, 5, 4, 0, 180); break;
            case Glyph.Copy: Box(8, 8, 12, 13, 2); g.DrawLines(pen, [new Point(15, 5), new(15, 3), new(3, 3), new(3, 15), new(5, 15)]); break;
            case Glyph.Arrow: Line(4, 12, 20, 12); Line(14, 6, 20, 12); Line(14, 18, 20, 12); break;
        }
        g.Restore(saved);
    }
}
