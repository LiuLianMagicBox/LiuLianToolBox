namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm
{
    private enum Tone { Canvas, Side, SideTitle, SideMuted, Muted, Feedback }
    private readonly Palette colors = new();
    private readonly ToolTip tips = new();
    private readonly TableLayoutPanel shell = Table(), nav = Table(), fields = Table(), header = Table();
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private readonly Label brand = new(), sideName = new(), sideStatus = new(), heading = new(), feedback = new();
    private readonly PictureBox brandIcon = new() { SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
    private Bitmap? logoImage;
    private readonly List<(string Page, ToolButton Button)> menu = [];
    private TableLayoutPanel side = null!, body = null!, footer = null!, brandRow = null!;
    private IconTile footerIcon = null!;
    private Font smallFont = null!, boldFont = null!, cardFont = null!, largeFont = null!, nameFont = null!;
    private DateTimeOffset? lastAt;
    private string? feedbackPage;
    private bool shellBuilt, compact, shortWindow;
    private int D(int value) => LogicalToDeviceUnits(value);
    private Padding Pad(int x, int y) => new(D(x), D(y), D(x), D(y));
    private Padding Gap(int bottom) => new(0, 0, 0, D(bottom));

    private static TableLayoutPanel Table() => new()
    {
        ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top, Margin = Padding.Empty, Padding = Padding.Empty, TabStop = false
    };
    private static void Columns(TableLayoutPanel t, params ColumnStyle[] styles)
    {
        t.ColumnCount = styles.Length;
        t.ColumnStyles.Clear();
        foreach (var style in styles) t.ColumnStyles.Add(style);
    }
    private static void AddRow(TableLayoutPanel t, Control c)
    {
        var row = t.RowCount++;
        t.RowStyles.Add(new(SizeType.AutoSize));
        t.Controls.Add(c, 0, row);
    }
    private static void Clear(Control parent)
    {
        foreach (var child in parent.Controls.Cast<Control>().ToArray()) child.Dispose();
        if (parent is TableLayoutPanel t) { t.RowStyles.Clear(); t.RowCount = 0; }
    }

    private void BuildShell()
    {
        colors.Select(settings.Appearance);
        smallFont = new Font(Font.FontFamily, 9.25f);
        boldFont = new Font(Font, FontStyle.Bold);
        cardFont = new Font(Font.FontFamily, 12.5f, FontStyle.Bold);
        nameFont = new Font(Font.FontFamily, 16, FontStyle.Bold);
        largeFont = new Font(Font.FontFamily, 23, FontStyle.Bold);
        SuspendLayout();
        shell.Dock = DockStyle.Fill; shell.AutoSize = false;
        Columns(shell, new(SizeType.Absolute, D(230)), new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowCount = 1;
        side = Table(); side.Dock = DockStyle.Fill; side.AutoSize = false; side.Tag = Tone.Side;
        Columns(side, new ColumnStyle(SizeType.Percent, 100));
        side.RowCount = 3;
        side.RowStyles.Add(new(SizeType.Absolute, D(85)));
        side.RowStyles.Add(new(SizeType.Percent, 100));
        side.RowStyles.Add(new(SizeType.Absolute, D(82)));
        brandRow = Table(); brandRow.Dock = DockStyle.Fill; brandRow.AutoSize = false; brandRow.Tag = Tone.Side;
        Columns(brandRow, new(SizeType.Absolute, D(40)), new(SizeType.Percent, 100));
        brandRow.RowStyles.Add(new(SizeType.Percent, 100));
        using (var logo = new Icon(appIcon, 64, 64)) brandIcon.Image = logoImage = logo.ToBitmap();
        tips.SetToolTip(brandIcon, "LiuLianToolBox");
        brandIcon.Size = new(D(36), D(36)); brandIcon.Anchor = AnchorStyles.None; brandIcon.Margin = Padding.Empty;
        brand.Text = "LiuLianToolBox"; brand.Font = cardFont; brand.UseMnemonic = false; brand.Tag = Tone.SideTitle;
        brand.TextAlign = ContentAlignment.MiddleLeft; brand.Dock = DockStyle.Fill; brand.Margin = new(D(8), 0, 0, 0);
        brandRow.Controls.Add(brandIcon, 0, 0); brandRow.Controls.Add(brand, 1, 0);
        var navScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Tag = Tone.Side };
        nav.Tag = Tone.Side; Columns(nav, new ColumnStyle(SizeType.Percent, 100)); navScroll.Controls.Add(nav);
        footer = Table(); footer.AutoSize = false; footer.Dock = DockStyle.Fill; footer.Tag = Tone.Side;
        Columns(footer, new(SizeType.Absolute, D(40)), new(SizeType.Percent, 100));
        footer.RowCount = 2; footer.RowStyles.Add(new(SizeType.Percent, 52)); footer.RowStyles.Add(new(SizeType.Percent, 48));
        footer.Paint += (_, e) => { using var p = new Pen(colors.SideLine); e.Graphics.DrawLine(p, D(14), 0, footer.Width - D(14), 0); };
        footerIcon = new IconTile(colors, Glyph.Monitor) { OnSidebar = true, Anchor = AnchorStyles.None, Size = new(D(35), D(35)), TabStop = false };
        footer.Controls.Add(footerIcon, 0, 0); footer.SetRowSpan(footerIcon, 2);
        foreach (var label in new[] { sideName, sideStatus })
        {
            label.Dock = DockStyle.Fill; label.AutoEllipsis = true; label.UseMnemonic = false; label.Margin = new(D(8), 0, 0, 0);
        }
        sideName.TextAlign = ContentAlignment.BottomLeft; sideName.Font = boldFont; sideName.Tag = Tone.SideTitle;
        sideStatus.TextAlign = ContentAlignment.MiddleLeft; sideStatus.Font = smallFont; sideStatus.Tag = Tone.SideMuted;
        footer.Controls.Add(sideName, 1, 0); footer.Controls.Add(sideStatus, 1, 1);
        side.Controls.Add(brandRow, 0, 0); side.Controls.Add(navScroll, 0, 1); side.Controls.Add(footer, 0, 2);

        body = Table(); body.Dock = DockStyle.Fill; body.AutoSize = false; body.Tag = Tone.Canvas;
        Columns(body, new ColumnStyle(SizeType.Percent, 100));
        body.RowCount = 3; body.RowStyles.Add(new(SizeType.AutoSize)); body.RowStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.AutoSize));
        header.Tag = Tone.Canvas;
        Columns(header, new(SizeType.Percent, 100), new(SizeType.AutoSize));
        heading.Dock = DockStyle.Fill; heading.AutoSize = true; heading.UseMnemonic = false; heading.Font = largeFont;
        heading.Margin = Padding.Empty; heading.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(heading, 0, 0);
        scroll.Tag = fields.Tag = Tone.Canvas; Columns(fields, new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(fields);
        feedback.AutoSize = true; feedback.Dock = DockStyle.Fill; feedback.UseMnemonic = false; feedback.Tag = Tone.Feedback; feedback.Visible = false;
        body.Controls.Add(header, 0, 0); body.Controls.Add(scroll, 0, 1); body.Controls.Add(feedback, 0, 2);
        shell.Controls.Add(side, 0, 0); shell.Controls.Add(body, 1, 0); Controls.Add(shell);
        shellBuilt = true;
        ResizeShell(); Render(); ResumeLayout();
    }

    private void ResizeShell()
    {
        if (!shellBuilt) return;
        compact = ClientSize.Width < D(920);
        shortWindow = ClientSize.Height < D(620);
        shell.ColumnStyles[0].Width = D(compact ? 72 : 234);
        side.RowStyles[0].Height = D(shortWindow ? 60 : 85);
        side.RowStyles[2].Height = D(shortWindow ? 55 : 82);
        brandRow.Padding = new Padding(D(compact ? 16 : 19), 0, D(compact ? 0 : 8), 0);
        footer.Padding = new Padding(D(compact ? 16 : 19), D(shortWindow ? 4 : 13), D(compact ? 0 : 8), D(shortWindow ? 4 : 12));
        brand.Visible = sideName.Visible = sideStatus.Visible = !compact;
        nav.Padding = new Padding(D(compact ? 6 : 12), D(4), D(compact ? 6 : 12), 0);
        foreach (var (_, button) in menu) SizeMenuButton(button);
        var pad = D(compact ? 20 : 32);
        header.Padding = new Padding(pad, D(compact ? 19 : 26), pad, D(18));
        fields.Padding = new Padding(pad, 0, pad, D(20));
        feedback.Margin = new Padding(pad, D(4), pad, D(12));
    }
    private void SizeMenuButton(ToolButton button)
    {
        button.Compact = compact;
        button.Height = D(shortWindow ? 36 : 43);
        button.Margin = Gap(shortWindow ? 2 : 4);
        button.Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ResizeShell(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ResizeShell(); }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); SyncAppearance(); }
    protected override void OnSystemColorsChanged(EventArgs e) { base.OnSystemColorsChanged(e); SyncAppearance(); }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x001A && shellBuilt && !IsDisposed) SyncAppearance(); // WM_SETTINGCHANGE
    }
    private void SyncAppearance()
    {
        if (shellBuilt && colors.Select(settings.Appearance))
        {
            ApplyColors(shell);
            if (page == "Home") UpdateHome();
            colors.TitleBar(Handle);
        }
    }

    private static Glyph PageGlyph(string key) => key switch
    {
        "Home" => Glyph.Home, "Current" => Glyph.Monitor, "Remote" => Glyph.Pair, "Bot" => Glyph.Bot,
        "Idle" => Glyph.Moon, "Mailbox" => Glyph.Mail, "Plugins" => Glyph.Plug, _ => Glyph.Info
    };
    private void BuildNavigation()
    {
        nav.SuspendLayout(); Clear(nav); menu.Clear();
        foreach (var key in Catalog.Menu(state))
        {
            if (key == "Plugins")
            {
                var rule = new Panel { Height = D(1), Dock = DockStyle.Top, Margin = new Padding(D(12), D(8), D(12), D(9)), TabStop = false };
                rule.Paint += (_, e) => e.Graphics.Clear(colors.SideLine);
                AddRow(nav, rule);
            }
            var item = new ToolButton(colors, text[key], PageGlyph(key), ButtonKind.Navigation)
            {
                Dock = DockStyle.Top, SelectedPage = page == key,
                AccessibleDescription = page == key ? text["SelectedPage"] : null
            };
            SizeMenuButton(item);
            tips.SetToolTip(item, text[key]);
            item.Click += (_, _) => Navigate(key);
            menu.Add((key, item)); AddRow(nav, item);
        }
        nav.ResumeLayout(); ApplyColors(nav);
    }
    private void Navigate(string key)
    {
        if (key == page || !Catalog.Menu(state).Contains(key)) return;
        page = key; feedbackPage = null; Render();
    }
    private void Render()
    {
        if (!shellBuilt) return;
        if (!Catalog.Menu(state).Contains(page)) page = "Home";
        shell.SuspendLayout(); fields.SuspendLayout();
        if (!menu.Select(m => m.Page).SequenceEqual(Catalog.Menu(state)) || menu.Any(m => m.Button.Text != text[m.Page]))
            BuildNavigation();
        foreach (var item in menu)
        {
            item.Button.SelectedPage = item.Page == page;
            item.Button.AccessibleDescription = item.Page == page ? text["SelectedPage"] : null;
            item.Button.Invalidate();
        }
        var previousAction = header.GetControlFromPosition(1, 0);
        previousAction?.Dispose();
        Clear(fields); defaultDevices = presenterChoice = null; shareSwitch = null;
        heading.Text = text[page];
        switch (page)
        {
            case "Home": HomePage(); break;
            case "Current": CurrentPage(); break;
            case "Remote": RemotePage(); break;
            case "Bot": BotPage(); break;
            case "Idle": IdlePage(); break;
            case "Mailbox": MailPage(); break;
            case "Plugins": MessageCard(Glyph.Plug, text["PluginWip"]); break;
            case "Info": MessageCard(Glyph.Info, text.Format("Version", Application.ProductVersion.Split('+')[0])); break;
        }
        UpdateSidebar(); UpdateFeedback(); ApplyColors(shell);
        colors.TitleBar(Handle);
        fields.ResumeLayout(); shell.ResumeLayout(); scroll.AutoScrollPosition = Point.Empty;
    }

    private string ConnectionStatus => connectionKey switch
    {
        "Online" => text["Online"], "NotLinked" or "Unauthorized" => text["UnlinkedStatus"], _ => text["OfflineStatus"]
    };
    private void UpdateSidebar()
    {
        sideName.Text = settings.Name; sideStatus.Text = ConnectionStatus;
        var label = settings.Name + " · " + sideStatus.Text;
        if (footerIcon.AccessibleName != label)
        {
            footerIcon.AccessibleName = label;
            tips.SetToolTip(footerIcon, label);
        }
    }
    private Color ResultColor => lastKey switch
    {
        "Saved" or "Copied" or "Launched" or "MoveRequested" => colors.Success,
        "Denied" or "Failed" or "LaunchFailed" or "InvalidInput" or "InvalidCode" or "BotUnavailable"
            or "Unauthorized" or "SettingsError" or "AppMissing" or "Offline" or "Uncertain" or "InvalidUrl" or "CopyFailed" => colors.Warning,
        _ => colors.Ink
    };
    private void UpdateFeedback()
    {
        var result = text.Format(lastKey, lastArgs);
        feedback.Text = result; feedback.ForeColor = ResultColor;
        feedback.Visible = page != "Home" && feedbackPage == page && lastAt.HasValue;
        if (page == "Home" && lastLabel is { IsDisposed: false })
        {
            lastLabel.Text = result; lastLabel.ForeColor = lastAt.HasValue ? ResultColor : colors.Muted;
            resultTime!.Text = lastAt?.ToLocalTime().ToString("t") ?? "";
        }
    }
    private void ApplyColors(Control c)
    {
        if (c is ConnectionOverview diagram) { diagram.RefreshDiagram(); return; }
        if (c is Label label)
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = label.Tag switch
            {
                Tone.SideTitle => colors.SideText, Tone.SideMuted => colors.SideMuted, Tone.Muted => colors.Muted,
                Tone.Feedback => lastAt.HasValue ? ResultColor : colors.Muted, _ => colors.Ink
            };
        }
        else if (c is TextBoxBase or ComboBox or UpDownBase or ListBox)
        { c.BackColor = colors.Field; c.ForeColor = colors.Ink; c.Invalidate(); return; }
        else
        {
            c.BackColor = c.Tag switch
            {
                Tone.Side => colors.Sidebar, Tone.Canvas => colors.Canvas,
                _ when c is SectionCard or SwitchBox => colors.Surface,
                _ => c.Parent?.BackColor ?? colors.Canvas
            };
            c.ForeColor = colors.Ink;
        }
        foreach (Control child in c.Controls) ApplyColors(child);
        c.Invalidate();
    }

    private SectionCard Card(string key, Glyph glyph)
    {
        var card = new SectionCard(colors) { Margin = Gap(13), Padding = new Padding(D(20), D(16), D(20), D(16)) };
        var titleRow = Table(); Columns(titleRow, new(SizeType.Absolute, D(43)), new(SizeType.Percent, 100));
        var icon = new IconTile(colors, glyph) { Size = new(D(32), D(32)), Margin = Padding.Empty, TabStop = false };
        titleRow.Controls.Add(icon, 0, 0);
        var title = new Label { Text = text[key], Font = cardFont, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false };
        titleRow.Controls.Add(title, 1, 0); titleRow.Margin = Gap(10);
        AddRow(card, titleRow); AddRow(fields, card); card.AccessibleName = text[key]; return card;
    }
    private void MessageCard(Glyph glyph, string value)
    {
        var card = new SectionCard(colors) { Padding = Pad(22, 22), Margin = Gap(12) };
        Columns(card, new(SizeType.Absolute, D(48)), new(SizeType.Percent, 100));
        card.Controls.Add(new IconTile(colors, glyph) { Size = new(D(36), D(36)), Margin = Padding.Empty }, 0, 0);
        card.Controls.Add(new Label { Text = value, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false }, 1, 0);
        AddRow(fields, card);
    }
    private Label Note(TableLayoutPanel parent, string value, bool muted = true)
    {
        var label = new Label { Text = value, UseMnemonic = false, AutoSize = true, Dock = DockStyle.Top, Tag = muted ? Tone.Muted : null, Margin = Gap(6) };
        AddRow(parent, label); return label;
    }
    private void FieldLabel(TableLayoutPanel parent, string key)
    {
        var label = Note(parent, text[key], false);
        label.Margin = new Padding(0, D(4), 0, D(4));
    }
    private TextBox Input(TableLayoutPanel parent, string key, string value = "", int max = 250)
    {
        FieldLabel(parent, key);
        var input = new TextBox { Text = value, MaxLength = max, Dock = DockStyle.Top, AccessibleName = text[key], BorderStyle = BorderStyle.FixedSingle, Margin = Gap(8) };
        AddRow(parent, input); return input;
    }
    private ComboBox Combo(TableLayoutPanel parent, string key, bool showLabel = true)
    {
        if (showLabel) FieldLabel(parent, key);
        var box = new ToolCombo(colors) { Dock = DockStyle.Top, Margin = Gap(8), AccessibleName = text[key] };
        AddRow(parent, box); return box;
    }
    private SwitchBox Check(TableLayoutPanel parent, string key, bool value)
    {
        var control = new SwitchBox(colors, text[key], value) { Margin = Gap(6) }; AddRow(parent, control); return control;
    }
    private NumericUpDown Number(TableLayoutPanel parent, string key, int min, int max, int value)
    {
        FieldLabel(parent, key);
        var number = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = D(132), AccessibleName = text[key], BorderStyle = BorderStyle.FixedSingle, Margin = Gap(8) };
        AddRow(parent, number); return number;
    }
    private (TableLayoutPanel Left, TableLayoutPanel Right) FieldPair(TableLayoutPanel parent)
    {
        var row = Table(); var left = Table(); var right = Table();
        Columns(row, new(SizeType.Percent, 50), new(SizeType.Percent, 50));
        left.Margin = new Padding(0, 0, D(9), 0); right.Margin = new Padding(D(9), 0, 0, 0);
        row.Controls.Add(left, 0, 0); row.Controls.Add(right, 1, 0); AddRow(parent, row);
        return (left, right);
    }
    private FlowLayoutPanel Actions(TableLayoutPanel parent)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, TabStop = false };
        AddRow(parent, row); return row;
    }
    private ToolButton ButtonFor(Control parent, string key, Glyph glyph, Func<Task> work, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ToolButton(colors, text[key], glyph, kind) { BusyText = text["Working"], Margin = new Padding(0, D(3), D(7), D(3)) };
        b.Click += async (_, _) => await Do(b, work);
        if (parent == header) { b.Anchor = AnchorStyles.Right; b.Margin = Padding.Empty; header.Controls.Add(b, 1, 0); }
        else parent.Controls.Add(b);
        return b;
    }
    private void SaveAction(Func<Task> save) => ButtonFor(header, "Save", Glyph.Check, save, ButtonKind.Primary);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        tips.Dispose(); logoImage?.Dispose(); appIcon.Dispose(); desktopFont.Dispose();
        smallFont?.Dispose(); boldFont?.Dispose(); cardFont?.Dispose(); nameFont?.Dispose(); largeFont?.Dispose();
    }
}
