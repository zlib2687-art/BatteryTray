using System.Globalization;

namespace BatteryTray;

/// <summary>
/// Écran de paramètres, dans l'esprit des Paramètres de Windows 11 : navigation à gauche (Général, Affichage,
/// Notifications, Appareils), un réglage par carte, interrupteurs, thème clair ou sombre selon le système.
/// Contenu : langue, actualisation, démarrage avec Windows, taille du flyout, couleurs du pourcentage, notifications,
/// et pour chaque appareil : afficher/masquer, renommer, seuil d'alerte, autonomie annoncée, ordre
/// (glisser-déposer ou boutons). Les modifications ne sont appliquées qu'avec « OK ».
/// </summary>
sealed class SettingsForm : Form
{
    static readonly int[] PollChoices = { 1, 2, 5, 10 };

    readonly AppSettings _s;
    readonly Action _applied;
    readonly SettingsTheme _t = SettingsTheme.Current();

    readonly ModernCombo _lang = new() { Width = 250 };
    readonly ModernCombo _poll = new() { Width = 250 };
    readonly ModernCombo _size = new() { Width = 250 };
    readonly ToggleSwitch _startup = new();
    readonly ToggleSwitch _notifyLow = new();
    readonly ToggleSwitch _notifyFull = new();
    readonly ToggleSwitch _brandColors = new();
    readonly ModernNumeric _lowThreshold = new() { Minimum = 1, Maximum = 99, Width = 120 };
    readonly ModernNumeric _orange = new() { Minimum = 2, Maximum = 100, Width = 120 };
    readonly ModernNumeric _red = new() { Minimum = 1, Maximum = 99, Width = 120 };
    readonly DataGridView _grid = new();

    readonly List<(NavItem Nav, Panel Page, string Title)> _pages = new();
    readonly Label _header = new();
    readonly Panel _content = new();
    readonly Panel _nav = new();
    readonly Panel _footer = new();
    ModernButton _ok = null!, _cancel = null!;

    public SettingsForm(AppSettings settings, Action applied)
    {
        _s = settings;
        _applied = applied;

        Text = Loc.T("set.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(980, 680);
        MinimumSize = new Size(820, 560);
        BackColor = _t.Back;
        ForeColor = _t.Text;
        Font = new Font(Loc.FontFamily, 9.75f);
        if (Loc.IsRtl) RightToLeft = RightToLeft.Yes;
        DoubleBuffered = true;

        BuildLayout();
        LoadValues();
        SelectPage(0);
    }

    // ------------------------------------------------------------------ fenêtre
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int v = _t.Light ? 0 : 1; Gdi.DwmSetWindowAttribute(Handle, 20, ref v, 4);          // barre de titre sombre
            v = Gdi.ColorRef(_t.Back); Gdi.DwmSetWindowAttribute(Handle, 35, ref v, 4);          // couleur de la barre de titre
            v = Gdi.ColorRef(_t.Text); Gdi.DwmSetWindowAttribute(Handle, 36, ref v, 4);          // couleur de son texte
        }
        catch { /* Windows plus ancien : barre de titre classique */ }
    }

    // ------------------------------------------------------------------ construction
    void BuildLayout()
    {
        bool rtl = Loc.IsRtl;

        // Dock : le dernier contrôle ajouté est placé en premier, le contrôle « Fill » doit donc être ajouté d'abord.
        _content.Dock = DockStyle.Fill;
        _content.BackColor = _t.Back;
        _content.Padding = new Padding(8, 18, 24, 0);
        Controls.Add(_content);

        _nav.Dock = rtl ? DockStyle.Right : DockStyle.Left;
        _nav.Width = 250;
        _nav.BackColor = _t.Back;
        Controls.Add(_nav);

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 68;
        _footer.BackColor = _t.Footer;
        Controls.Add(_footer);

        BuildNav();
        BuildFooter();

        _header.Dock = DockStyle.Top;
        _header.Height = 62;
        _header.Font = new Font(Loc.FontFamily, 20f, FontStyle.Bold);
        _header.ForeColor = _t.Text;
        _header.BackColor = _t.Back;
        _header.TextAlign = ContentAlignment.MiddleLeft; // en arabe, RightToLeft inverse déjà l'alignement
        _header.Padding = new Padding(rtl ? 0 : 16, 0, rtl ? 16 : 0, 0);

        // Général
        var general = NewPage();
        AddCards(general,
            Row(Loc.T("set.language"), _lang),
            Row(Loc.T("set.poll"), _poll),
            Row(Loc.T("set.startup"), _startup));
        AddPage("", Loc.T("set.general"), general);

        // Affichage
        var display = NewPage();
        AddCards(display,
            Row(Loc.T("set.size"), _size),
            Row(Loc.T("set.brandColors"), _brandColors),
            Section(Loc.T("set.colors")),
            Row(Loc.T("set.orange"), _orange),
            Row(Loc.T("set.red"), _red));
        AddPage("", Loc.T("set.display"), display);

        // Notifications
        var notifs = NewPage();
        AddCards(notifs,
            Row(Loc.T("menu.notifyLow"), _notifyLow),
            Row(Loc.T("menu.notifyFull"), _notifyFull),
            Row(Loc.T("set.lowThreshold"), _lowThreshold));
        AddPage("", Loc.T("set.notifs"), notifs);

        // Appareils
        var devices = NewPage();
        devices.AutoScroll = false;
        devices.Controls.Add(BuildDevicesCard());
        AddPage("", Loc.T("set.devices"), devices);

        _content.Controls.Add(_header); // en dernier : il se place tout en haut

        foreach (var c in new[] { _lang, _poll, _size }) c.Palette = _t;
        foreach (var n in new[] { _lowThreshold, _orange, _red }) n.Palette = _t;
    }

    void BuildNav()
    {
        bool rtl = Loc.IsRtl;
        var title = new Label
        {
            Text = "BatteryTray", AutoSize = false, Height = 44, Width = 226, Left = 12, Top = 14,
            Font = new Font(Loc.FontFamily, 12.5f, FontStyle.Bold), ForeColor = _t.Text, BackColor = _t.Back,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(rtl ? 0 : 8, 0, rtl ? 8 : 0, 0),
        };
        _nav.Controls.Add(title);
    }

    void AddPage(string glyph, string title, Panel page)
    {
        var item = new NavItem { Palette = _t, Glyph = glyph, Text = title, Width = 226, Left = 12, Top = 66 + _pages.Count * 44, BackColor = _t.Back };
        int index = _pages.Count;
        item.Click += (_, _) => SelectPage(index);
        _nav.Controls.Add(item);
        _content.Controls.Add(page);
        _pages.Add((item, page, title));
    }

    public void SelectPage(int index)
    {
        index = Math.Clamp(index, 0, _pages.Count - 1);
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Nav.SetSelected(i == index);
            _pages[i].Page.Visible = i == index;
        }
        _header.Text = _pages[index].Title;
    }

    Panel NewPage() => new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = _t.Back, Visible = false, Padding = new Padding(0, 0, 0, 8) };

    /// <summary>Ajoute les cartes dans l'ordre d'affichage (le premier ajouté à un Dock « Top » serait sinon tout en bas).</summary>
    static void AddCards(Panel page, params Control[] cards)
    {
        foreach (var c in cards.Reverse()) page.Controls.Add(c);
    }

    Card Row(string title, params Control[] trailing)
    {
        var card = new Card(_t) { Title = title, Font = Font };
        foreach (var c in trailing)
        {
            if (c is ToggleSwitch ts) ts.Palette = _t;
            card.AddTrailing(c);
        }
        return card;
    }

    Control Section(string text) => new Label
    {
        Text = text, Dock = DockStyle.Top, Height = 40, ForeColor = _t.Text, BackColor = _t.Back,
        Font = new Font(Loc.FontFamily, 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft,
        Padding = new Padding(4, 0, 4, 6),
    };

    Control BuildDevicesCard()
    {
        var card = new Card(_t, 100) { Dock = DockStyle.Fill, Font = Font, Padding = new Padding(2, 2, 2, Card.Gap + 2) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = _t.Card, Padding = new Padding(12, 10, 12, 6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        card.Controls.Add(table);

        ConfigureGrid();
        table.Controls.Add(_grid, 0, 0);

        // boutons sous la liste : la liste garde toute la largeur (les en-têtes de colonnes ne sont plus coupés)
        var moves = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, WrapContents = false, BackColor = _t.Card, Padding = new Padding(0, 8, 0, 0) };
        var up = new ModernButton { Palette = _t, Text = Loc.T("set.up"), Width = 120, Height = 34, BackColor = _t.Card, Margin = new Padding(0, 0, 8, 0) };
        var down = new ModernButton { Palette = _t, Text = Loc.T("set.down"), Width = 120, Height = 34, BackColor = _t.Card, Margin = new Padding(0) };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        moves.Controls.Add(up);
        moves.Controls.Add(down);
        table.Controls.Add(moves, 0, 1);

        var hint = new Label { Text = Loc.T("set.hint"), Dock = DockStyle.Fill, ForeColor = _t.SubText, BackColor = _t.Card, Padding = new Padding(2, 4, 2, 0) };
        table.Controls.Add(hint, 0, 2);
        return card;
    }

    void BuildFooter()
    {
        _ok = new ModernButton { Palette = _t, Primary = true, Text = Loc.T("set.ok"), DialogResult = DialogResult.OK, BackColor = _t.Footer };
        _cancel = new ModernButton { Palette = _t, Text = Loc.T("set.cancel"), DialogResult = DialogResult.Cancel, BackColor = _t.Footer };
        _ok.Click += (_, _) => { if (Commit()) Close(); };
        _cancel.Click += (_, _) => Close();
        _footer.Controls.Add(_cancel);
        _footer.Controls.Add(_ok);
        _footer.Resize += (_, _) => LayoutFooter();
        LayoutFooter();
        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    void LayoutFooter()
    {
        int y = (_footer.Height - _ok.Height) / 2;
        if (Loc.IsRtl) { _ok.Location = new Point(24, y); _cancel.Location = new Point(24 + _ok.Width + 10, y); }
        else { _ok.Location = new Point(_footer.Width - 24 - _ok.Width, y); _cancel.Location = new Point(_ok.Left - 10 - _cancel.Width, y); }
    }

    void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersWidth = 24;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AllowDrop = true;

        // apparence moderne : sans quadrillage, lignes aérées, couleurs du thème
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = _t.Card;
        _grid.GridColor = _t.CardBorder;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeight = 40;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.RowTemplate.Height = 40;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _t.Card, ForeColor = _t.SubText, SelectionBackColor = _t.Card, SelectionForeColor = _t.SubText,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0),
        };
        _grid.RowHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = _t.Card, SelectionBackColor = _t.Card };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = _t.Card, ForeColor = _t.Text, SelectionBackColor = _t.Selection, SelectionForeColor = _t.Text,
            Padding = new Padding(4, 0, 0, 0),
        };

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = Loc.T("set.col.show"), FillWeight = 15 }); // « Показать » se coupait à 11
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.col.device"), ReadOnly = true, FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.col.name"), FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.col.low"), FillWeight = 13 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.col.rated"), FillWeight = 24 });

        // glisser-déposer des lignes par leur marge
        _grid.MouseDown += (_, e) =>
        {
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.Type == DataGridViewHitTestType.RowHeader && hit.RowIndex >= 0)
                _grid.DoDragDrop(_grid.Rows[hit.RowIndex], DragDropEffects.Move);
        };
        _grid.DragOver += (_, e) => e.Effect = DragDropEffects.Move;
        _grid.DragDrop += (_, e) =>
        {
            var p = _grid.PointToClient(new Point(e.X, e.Y));
            int to = _grid.HitTest(p.X, p.Y).RowIndex;
            if (e.Data?.GetData(typeof(DataGridViewRow)) is DataGridViewRow row && to >= 0 && row.Index != to)
            {
                _grid.Rows.RemoveAt(row.Index);
                _grid.Rows.Insert(to, row);
                _grid.ClearSelection();
                _grid.Rows[to].Selected = true;
            }
        };
    }

    // ------------------------------------------------------------------ valeurs
    void LoadValues()
    {
        foreach (var (code, native) in Loc.Languages) _lang.Items.Add(new LangItem(code, native));
        _lang.SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == Loc.Current));

        foreach (int m in PollChoices) _poll.Items.Add(Loc.T("set.pollOpt", m));
        int pi = Array.IndexOf(PollChoices, _s.PollMinutes);
        _poll.SelectedIndex = pi >= 0 ? pi : 1;

        string[] sizeNames = { "set.size.s", "set.size.m", "set.size.l", "set.size.xl" };
        for (int i = 0; i < FlyoutForm.ScaleChoices.Length; i++)
            _size.Items.Add($"{Loc.T(sizeNames[i])} ({FlyoutForm.ScaleChoices[i]} %)");
        int si = Array.IndexOf(FlyoutForm.ScaleChoices, _s.FlyoutScale);
        _size.SelectedIndex = si >= 0 ? si : 1;

        _startup.Checked = _s.StartWithWindows || Installer.IsStartupEnabled();
        _brandColors.Checked = _s.BrandColors;
        _notifyLow.Checked = _s.NotifyLow;
        _notifyFull.Checked = _s.NotifyFull;
        _lowThreshold.Value = Math.Clamp(_s.LowThreshold, 1, 99);
        _orange.Value = Math.Clamp(_s.OrangeBelow, 2, 100);
        _red.Value = Math.Clamp(_s.RedBelow, 1, 99);

        // appareils connus, dans l'ordre choisi puis les autres
        var ids = _s.Order.Where(_s.Known.ContainsKey).Concat(_s.Known.Keys.Where(k => !_s.Order.Contains(k))).Distinct();
        foreach (string id in ids)
        {
            var known = _s.Known[id];
            var pref = _s.PrefFor(id);
            int i = _grid.Rows.Add(
                !pref.Hidden,
                known.Name,
                pref.Name ?? "",
                pref.LowThreshold?.ToString(CultureInfo.InvariantCulture) ?? "",
                pref.RatedHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? "");
            _grid.Rows[i].Tag = id;
        }
    }

    void MoveSelected(int delta)
    {
        if (_grid.CurrentRow is not { } row) return;
        int to = row.Index + delta;
        if (to < 0 || to >= _grid.Rows.Count) return;
        _grid.Rows.RemoveAt(row.Index);
        _grid.Rows.Insert(to, row);
        _grid.ClearSelection();
        _grid.Rows[to].Selected = true;
        _grid.CurrentCell = _grid.Rows[to].Cells[1];
    }

    /// <summary>Enregistre les valeurs saisies dans les réglages, puis prévient l'application.</summary>
    bool Commit()
    {
        _grid.EndEdit();

        _s.Language = ((LangItem)_lang.SelectedItem!).Code;
        _s.PollMinutes = PollChoices[Math.Max(0, _poll.SelectedIndex)];
        _s.FlyoutScale = FlyoutForm.ScaleChoices[Math.Max(0, _size.SelectedIndex)];
        _s.BrandColors = _brandColors.Checked;
        _s.NotifyLow = _notifyLow.Checked;
        _s.NotifyFull = _notifyFull.Checked;
        _s.LowThreshold = (int)_lowThreshold.Value;
        _s.OrangeBelow = (int)_orange.Value;
        _s.RedBelow = Math.Min((int)_red.Value, _s.OrangeBelow - 1); // le rouge reste sous l'orange

        _s.StartWithWindows = _startup.Checked;
        try { Installer.SetStartup(_startup.Checked); }
        catch (Exception e) { MessageBox.Show(e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        _s.Order = _grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Tag!).ToList();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            string id = (string)row.Tag!;
            var pref = BuildPref(_s.PrefFor(id), row.Cells[0].Value is true, row.Cells[2].Value as string,
                _s.Known[id].Name, row.Cells[3].Value as string, row.Cells[4].Value as string);
            if (pref == null) _s.Devices.Remove(id);
            else _s.Devices[id] = pref;
        }

        _s.Save();
        _applied();
        return true;
    }

    /// <summary>Aperçu pour les tests visuels : dessine la fenêtre dans une image.</summary>
    public Bitmap RenderPreview()
    {
        // la fenêtre doit avoir été affichée pour que tous les contrôles se dessinent : on l'affiche hors de l'écran
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-5000, -5000);
        ShowInTaskbar = false;
        Show();
        for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(60); }
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }

    /// <summary>
    /// Préférences d'un appareil d'après la ligne validée (null = rien de particulier, préférence supprimée).
    /// Un appareil masqué D'OFFICE faute de niveau reste « masqué automatiquement » tant que sa case n'a pas été touchée :
    /// avant, un simple clic sur OK le transformait en masquage choisi par l'utilisateur, et il ne réapparaissait plus
    /// jamais, même une fois son niveau disponible.
    /// </summary>
    internal static DevicePref? BuildPref(DevicePref old, bool show, string? typedName, string originalName, string? lowText, string? ratedText)
    {
        string name = (typedName ?? "").Trim();
        var pref = new DevicePref
        {
            Hidden = !show,
            AutoHidden = !show && old.Hidden && old.AutoHidden,
            Name = name.Length == 0 || name == originalName ? null : name,
            LowThreshold = ParseInt(lowText),
            RatedHours = ParseHours(ratedText),
        };
        return pref is { Hidden: false, Name: null, LowThreshold: null, RatedHours: null } ? null : pref;
    }

    static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 1, 99) : null;

    static double? ParseHours(string? text) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0
            ? Math.Min(v, 9999) : null;

    sealed record LangItem(string Code, string Native)
    {
        public override string ToString() => Native;
    }
}
