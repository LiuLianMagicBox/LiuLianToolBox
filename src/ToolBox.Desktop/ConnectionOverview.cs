using System.Drawing.Drawing2D;

namespace LiuLian.ToolBox.Desktop;

internal sealed class ConnectionOverview : Panel
{
    private readonly Palette colors;
    private readonly Label[] roles, names, details;
    private readonly StatusChip[] statuses;
    private Bitmap? diagram;
    private bool paired, localOnline, peerOnline;

    public ConnectionOverview(Palette colors, Catalog text, Font small, Font deviceName)
    {
        this.colors = colors;
        DoubleBuffered = true; Dock = DockStyle.Top;
        SetStyle(ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Grouping; AccessibleName = text["ConnectionOverview"];
        roles = [LabelFor(text["LocalPC"], small), LabelFor(text["PeerPC"], small)];
        names = [LabelFor("", deviceName), LabelFor("", deviceName)];
        details = [LabelFor("", small), LabelFor("", small)];
        statuses = [new(colors, small), new(colors, small)];
        Controls.AddRange([roles[0], names[0], statuses[0], details[0], roles[1], names[1], statuses[1], details[1]]);
    }
    private static Label LabelFor(string value, Font font) => new()
    {
        Text = value, Font = font, AutoEllipsis = true, UseMnemonic = false,
        TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent, TabStop = false
    };

    public void UpdateDevices(string localName, string? peerName, string localStatus, string peerStatus,
        string localVoice, string peerVoice, bool connected, bool linkedPair, bool peerConnected)
    {
        var diagramChanged = (localOnline, paired, peerOnline) != (connected, linkedPair, peerConnected);
        var extra = localVoice != "" || peerVoice != "";
        var layoutChanged = statuses[0].Text != localStatus || statuses[1].Text != peerStatus || details[0].Visible != extra;
        localOnline = connected; paired = linkedPair; peerOnline = peerConnected;
        UpdateColumn(0, localName, localStatus, localVoice, connected);
        UpdateColumn(1, peerName ?? "—", peerStatus, peerVoice, peerConnected);
        if (details[0].Visible != extra) { details[0].Visible = details[1].Visible = extra; }
        var h = ContentHeight(extra);
        if (Height != h) Height = h;
        else if (layoutChanged) PerformLayout();
        if (diagramChanged) RefreshDiagram();
    }

    private void UpdateColumn(int i, string name, string status, string voice, bool online)
    {
        roles[i].ForeColor = details[i].ForeColor = colors.HeroMuted;
        names[i].ForeColor = colors.HeroInk;
        names[i].Text = name;
        details[i].Text = voice;
        statuses[i].SetStatus(status, online);
    }
    private int D(int n) => LogicalToDeviceUnits(n);
    private int ContentHeight(bool voice) => D(88) + roles[0].Font.Height + names[0].Font.Height + D(48) +
        (voice ? details[0].Font.Height + D(8) : 0);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (statuses is null) return;
        var half = ClientSize.Width / 2;
        for (int i = 0; i < 2; i++)
        {
            var x = half * i + D(12); var width = Math.Max(1, half - D(24));
            var y = D(80);
            roles[i].SetBounds(x, y, width, roles[i].Font.Height + D(4));
            y = roles[i].Bottom + D(2);
            names[i].SetBounds(x, y, width, names[i].Font.Height + D(4));
            y = names[i].Bottom + D(5);
            var chipWidth = Math.Min(width, statuses[i].GetPreferredSize(Size.Empty).Width);
            statuses[i].SetBounds(x + (width - chipWidth) / 2, y, chipWidth, statuses[i].Font.Height + D(7));
            details[i].SetBounds(x, statuses[i].Bottom + D(4), width, details[i].Font.Height + D(4));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        // Transparent child labels also request the panel background. Reuse the drawing across their repaints.
        if (diagram is null)
        {
            diagram = new Bitmap(Width, Height);
            diagram.SetResolution(DeviceDpi, DeviceDpi);
            using var buffer = Graphics.FromImage(diagram);
            DrawDiagram(buffer);
        }
        e.Graphics.DrawImageUnscaled(diagram, 0, 0);
    }

    internal void RefreshDiagram()
    {
        diagram?.Dispose(); diagram = null;
        Invalidate(true);
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshDiagram(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshDiagram(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { diagram?.Dispose(); diagram = null; }
        base.Dispose(disposing);
    }

    private void DrawDiagram(Graphics g)
    {
        g.Clear(colors.Canvas); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Palette.Round(new RectangleF(.5f, .5f, Width - 1f, Height - 1f), D(14));
        using var fill = new LinearGradientBrush(ClientRectangle, colors.HeroLeft, colors.HeroRight, 12f);
        g.FillPath(fill, shape);
        var clip = g.Save(); g.SetClip(shape);
        if (!colors.Contrast)
        {
            using var dot = new SolidBrush(Color.FromArgb(40, colors.HeroMuted));
            for (int y = D(8); y < Height; y += D(16))
                for (int x = D(10); x < Width; x += D(16))
                    g.FillEllipse(dot, x, y, D(1), D(1));
        }
        var left = Width / 4f; var right = Width * .75f; var mid = Width / 2f; var cy = D(45);
        var connected = localOnline && peerOnline && paired;
        var trace = connected ? colors.Mint : colors.HeroMuted;
        using var cable = new Pen(Color.FromArgb(colors.Contrast || connected ? 255 : 150, trace), D(2));
        if (!connected) cable.DashPattern = [2, 3];
        if (right - left > D(92))
        {
            g.DrawLine(cable, left + D(42), cy, mid - D(18), cy);
            g.DrawLine(cable, mid + D(18), cy, right - D(42), cy);
        }
        using var linkFill = new SolidBrush(colors.Contrast ? colors.HeroLeft : Palette.Blend(colors.HeroLeft, colors.HeroRight, .6f));
        using var outline = new Pen(Color.FromArgb(colors.Contrast ? 255 : 130, trace));
        g.FillEllipse(linkFill, mid - D(16), cy - D(16), D(32), D(32));
        g.DrawEllipse(outline, mid - D(16), cy - D(16), D(32), D(32));
        Glyphs.Draw(g, paired ? Glyph.Link : Glyph.Unlink, new(mid - D(9), cy - D(9), D(18), D(18)), trace);
        DrawDevice(g, left, cy, false, localOnline);
        DrawDevice(g, right, cy, true, peerOnline);
        g.Restore(clip);
        if (colors.Contrast) { using var pen = new Pen(colors.Ink); g.DrawPath(pen, shape); }
    }

    private void DrawDevice(Graphics g, float x, float y, bool laptop, bool online)
    {
        var saved = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
        var ink = online ? colors.Mint : colors.HeroMuted;
        using var stroke = new Pen(ink, 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shade = new SolidBrush(colors.Contrast ? colors.HeroLeft : Palette.Blend(colors.HeroLeft, ink, .10f));
        var rect = laptop ? new RectangleF(-25, -17, 50, 30) : new RectangleF(-29, -20, 58, 35);
        using var screen = Palette.Round(rect, 3);
        g.FillPath(shade, screen); g.DrawPath(stroke, screen);
        if (laptop)
        {
            g.DrawPolygon(stroke, [new Point(-25, 14), new(25, 14), new(32, 20), new(-32, 20)]);
            g.DrawLine(stroke, -5, 16.5f, 5, 16.5f);
        }
        else
        {
            g.DrawLine(stroke, -28, 9, 28, 9);
            g.DrawLine(stroke, -3, 15, -4.5f, 21); g.DrawLine(stroke, 3, 15, 4.5f, 21);
            g.DrawLine(stroke, -11, 22, 11, 22);
        }
        if (online)
            g.DrawLines(stroke, [new Point(-9, -2), new(-4, -2), new(-1, -7), new(3, 2), new(6, -2), new(10, -2)]);
        else
        {
            using var glint = new Pen(ink, 1.2f);
            g.DrawLine(glint, rect.Left + 5, rect.Top + 7, rect.Left + 11, rect.Top + 7);
            g.DrawLine(glint, rect.Left + 5, rect.Top + 11, rect.Left + 17, rect.Top + 11);
        }
        g.Restore(saved);
    }

    private sealed class StatusChip : Label
    {
        private readonly Palette colors;
        private bool online;
        public StatusChip(Palette colors, Font font)
        {
            this.colors = colors; Font = font; BackColor = Color.Transparent; UseMnemonic = false;
        }
        public void SetStatus(string status, bool isOnline)
        {
            Text = status;
            if (online != isOnline) { online = isOnline; Invalidate(); }
        }
        public override Size GetPreferredSize(Size proposedSize) =>
            new(TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + LogicalToDeviceUnits(29), Font.Height + LogicalToDeviceUnits(7));
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Palette.Round(new(0, 0, Width - 1, Height - 1), Height / 2f);
            var ink = online ? colors.Mint : colors.HeroInk;
            var fill = colors.Contrast ? colors.Surface : Palette.Blend(colors.HeroLeft, online ? colors.Mint : colors.HeroMuted, .17f);
            using var brush = new SolidBrush(fill); g.FillPath(brush, shape);
            var d = LogicalToDeviceUnits(6); using var dot = new SolidBrush(ink);
            g.FillEllipse(dot, LogicalToDeviceUnits(9), (Height - d) / 2f, d, d);
            var start = LogicalToDeviceUnits(20);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(start, 0, Width - start, Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
