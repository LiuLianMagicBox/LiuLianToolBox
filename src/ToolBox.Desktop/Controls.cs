using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LiuLian.ToolBox.Desktop;

internal enum ButtonKind { Navigation, Primary, Secondary, Danger }

internal sealed class ToolCombo : ComboBox
{
    private readonly Palette colors;
    private const TextFormatFlags TextFlags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    public ToolCombo(Palette colors)
    {
        this.colors = colors;
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        SizeItems();
    }
    private void SizeItems() => ItemHeight = Math.Max(LogicalToDeviceUnits(26), Font.Height + LogicalToDeviceUnits(7));
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); SizeItems(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); SizeItems(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? colors.Accent : colors.Field);
        e.Graphics.FillRectangle(fill, e.Bounds);
        if (e.Index >= 0 && e.Index < Items.Count)
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(8), 0),
                selected ? colors.OnAccent : colors.Ink, TextFlags);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // A native drop-down draws its closed border and arrow separately from the owner-drawn items.
        if (m.Msg == 0x000F && IsHandleCreated) // WM_PAINT
        {
            using var g = Graphics.FromHwnd(Handle);
            PaintField(g);
        }
        else if (m.Msg == 0x0318 && m.WParam != 0) // WM_PRINTCLIENT
        {
            using var g = Graphics.FromHdc(m.WParam);
            PaintField(g);
        }
    }
    private void PaintField(Graphics g)
    {
        if (Width < 2 || Height < 2) return;
        g.Clear(Parent?.BackColor ?? colors.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = Palette.Round(new RectangleF(.6f, .6f, Width - 1.2f, Height - 1.2f), LogicalToDeviceUnits(6));
        using var fill = new SolidBrush(colors.Field);
        using var border = new Pen(Focused ? colors.Accent : colors.Line, Focused ? LogicalToDeviceUnits(2) : 1);
        g.FillPath(fill, outline); g.DrawPath(border, outline);
        var ink = Enabled ? colors.Ink : colors.Muted;
        var textX = LogicalToDeviceUnits(10);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textX, 0, Math.Max(1, Width - textX - LogicalToDeviceUnits(30)), Height), ink, TextFlags);
        var x = Width - LogicalToDeviceUnits(16); var y = Height / 2; var d = LogicalToDeviceUnits(4);
        using var pen = new Pen(ink, Math.Max(1, DeviceDpi / 72f)) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new Point(x - d, y - d / 2), new(x, y + d / 2), new(x + d, y - d / 2)]);
    }
}

internal sealed class ToolButton : Button
{
    private readonly Palette colors;
    private readonly ButtonKind kind;
    private readonly Glyph glyph;
    private bool hovered, held, busy, available = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SelectedPage { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Compact { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string BusyText { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Available
    {
        set { available = value; Enabled = available && !busy; }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Busy
    {
        set { busy = value; Enabled = available && !busy; Invalidate(); }
    }

    public ToolButton(Palette colors, string caption, Glyph glyph, ButtonKind kind)
    {
        this.colors = colors; this.kind = kind; this.glyph = glyph;
        Text = AccessibleName = caption;
        UseMnemonic = false;
        AutoSize = kind != ButtonKind.Navigation;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var label = TextRenderer.MeasureText(Text, Font, Size.Empty, flags);
        var width = BusyText.Length == 0 ? label.Width : Math.Max(label.Width, TextRenderer.MeasureText(BusyText, Font, Size.Empty, flags).Width);
        return new(width + LogicalToDeviceUnits(53),
            Math.Max(label.Height + LogicalToDeviceUnits(16), LogicalToDeviceUnits(38)));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = held = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); held = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); held = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var nav = kind == ButtonKind.Navigation;
        var parentColor = Parent?.BackColor ?? colors.Surface;
        var fill = kind switch
        {
            ButtonKind.Navigation => SelectedPage ? colors.SideSelected : parentColor,
            ButtonKind.Primary => colors.Accent,
            _ => colors.Surface
        };
        var ink = kind switch
        {
            ButtonKind.Navigation => SelectedPage ? colors.SideSelectedText : colors.SideMuted,
            ButtonKind.Primary => colors.OnAccent,
            ButtonKind.Danger => colors.Danger,
            _ => colors.Ink
        };
        if (!Enabled)
        {
            fill = colors.Contrast ? SystemColors.Control : Palette.Blend(parentColor, colors.Muted, .10f);
            ink = colors.Contrast ? SystemColors.GrayText : colors.Muted;
        }
        else if (hovered && !colors.Contrast)
            fill = Palette.Blend(fill, ink, held ? .18f : .09f);

        g.Clear(parentColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Palette.Round(new RectangleF(.6f, .6f, Width - 1.2f, Height - 1.2f), LogicalToDeviceUnits(8));
        using var surface = new SolidBrush(fill);
        g.FillPath(surface, shape);
        if (kind is ButtonKind.Secondary or ButtonKind.Danger || colors.Contrast)
        {
            using var border = new Pen(colors.Contrast ? ink : colors.Line);
            g.DrawPath(border, shape);
        }
        var icon = LogicalToDeviceUnits(nav ? 20 : 17);
        var inset = LogicalToDeviceUnits(nav ? 15 : 13);
        if (nav && SelectedPage && !colors.Contrast)
        {
            using var marker = new SolidBrush(colors.Mint);
            g.FillRectangle(marker, LogicalToDeviceUnits(2), Height / 2 - LogicalToDeviceUnits(8), LogicalToDeviceUnits(3), LogicalToDeviceUnits(16));
        }
        var iconX = nav && Compact ? (Width - icon) / 2 : inset;
        Glyphs.Draw(g, glyph, new(iconX, (Height - icon) / 2f, icon, icon), ink);
        if (!nav || !Compact)
        {
            var start = iconX + icon + LogicalToDeviceUnits(8);
            var bounds = new Rectangle(start, 0, Math.Max(1, Width - start - inset + 1), Height);
            TextRenderer.DrawText(g, busy && BusyText != "" ? BusyText : Text, Font, bounds, ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(4)), ink, fill);
    }
}

internal sealed class SectionCard : TableLayoutPanel
{
    private readonly Palette colors;
    public SectionCard(Palette colors)
    {
        this.colors = colors;
        DoubleBuffered = true;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1; ColumnStyles.Add(new(SizeType.Percent, 100));
        Dock = DockStyle.Top;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        var g = e.Graphics;
        g.Clear(colors.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Round(new RectangleF(.55f, .55f, Width - 1.1f, Height - 1.1f), LogicalToDeviceUnits(13));
        using var brush = new SolidBrush(colors.Surface);
        using var border = new Pen(colors.Line);
        g.FillPath(brush, path); g.DrawPath(border, path);
    }
}

internal sealed class IconTile(Palette colors, Glyph glyph) : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool OnSidebar { get; init; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var edge = Math.Min(Width, Height) - 1;
        if (edge < 2) return;
        using var tile = Palette.Round(new RectangleF(0, 0, edge, edge), LogicalToDeviceUnits(8));
        using var brush = new SolidBrush(OnSidebar ? colors.SideSelected : colors.Soft);
        e.Graphics.FillPath(brush, tile);
        var size = edge * .57f;
        Glyphs.Draw(e.Graphics, glyph, new((edge - size) / 2, (edge - size) / 2, size, size),
            OnSidebar ? colors.SideSelectedText : colors.AccentInk);
    }
}

internal sealed class SwitchBox : CheckBox
{
    private readonly Palette colors;
    public SwitchBox(Palette colors, string caption, bool value)
    {
        this.colors = colors;
        Text = AccessibleName = caption;
        Checked = value;
        AutoSize = true; Dock = DockStyle.Top; Cursor = Cursors.Hand; UseMnemonic = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var inset = LogicalToDeviceUnits(54);
        var w = proposedSize.Width > inset ? proposedSize.Width - inset : int.MaxValue;
        var size = TextRenderer.MeasureText(Text, Font, new Size(w, int.MaxValue), Flags);
        return new(size.Width + inset, Math.Max(LogicalToDeviceUnits(32), size.Height + LogicalToDeviceUnits(8)));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(colors.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
        var h = LogicalToDeviceUnits(21); var w = LogicalToDeviceUnits(38);
        var y = (Height - h) / 2; var inset = LogicalToDeviceUnits(3);
        using var path = Palette.Round(new RectangleF(1, y, w, h), h / 2f);
        using var track = new SolidBrush(Checked ? colors.Accent : colors.Field);
        using var border = new Pen(Checked ? colors.Accent : colors.Muted);
        g.FillPath(track, path); g.DrawPath(border, path);
        using var knob = new SolidBrush(Checked ? colors.OnAccent : colors.Muted);
        var diameter = h - 2 * inset;
        g.FillEllipse(knob, Checked ? w - diameter - inset + 1 : inset + 1, y + inset, diameter, diameter);
        var start = LogicalToDeviceUnits(51);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(start, 0, Math.Max(1, Width - start - 2), Height), colors.Ink, Flags | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1), colors.Ink, colors.Surface);
    }
}
