using System.Globalization;

namespace BatteryTray;

/// <summary>
/// Écran de paramètres : langue, actualisation, démarrage avec Windows, notifications, seuils de couleur,
/// et pour chaque appareil : afficher/masquer, renommer, seuil d'alerte, autonomie annoncée, ordre
/// (glisser-déposer ou boutons). Les modifications ne sont appliquées qu'avec « OK ».
/// </summary>
sealed class SettingsForm : Form
{
    static readonly int[] PollChoices = { 1, 2, 5, 10 };

    readonly AppSettings _s;
    readonly Action _applied;

    readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _poll = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly CheckBox _startup = new() { AutoSize = true };
    readonly CheckBox _notifyLow = new() { AutoSize = true };
    readonly CheckBox _notifyFull = new() { AutoSize = true };
    readonly NumericUpDown _lowThreshold = new() { Minimum = 1, Maximum = 99, Width = 80 };
    readonly NumericUpDown _orange = new() { Minimum = 2, Maximum = 100, Width = 80 };
    readonly NumericUpDown _red = new() { Minimum = 1, Maximum = 99, Width = 80 };
    readonly DataGridView _grid = new();

    public SettingsForm(AppSettings settings, Action applied)
    {
        _s = settings;
        _applied = applied;

        Text = Loc.T("set.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 740);
        Font = new Font(Loc.FontFamily, 9f);
        if (Loc.IsRtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

        BuildLayout();
        LoadValues();
    }

    // ------------------------------------------------------------------ construction
    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // sinon la colonne s'élargit au-delà de la fenêtre
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // --- Général
        var general = Group(Loc.T("set.general"), out var gt);
        AddRow(gt, Loc.T("set.language"), _lang);
        AddRow(gt, Loc.T("set.poll"), _poll);
        AddRow(gt, Loc.T("set.size"), _size);
        AddRow(gt, "", _startup);
        _startup.Text = Loc.T("set.startup");
        root.Controls.Add(general, 0, 0);

        // --- Notifications + couleurs
        var notifs = Group(Loc.T("set.notifs"), out var nt);
        _notifyLow.Text = Loc.T("menu.notifyLow");
        _notifyFull.Text = Loc.T("menu.notifyFull");
        AddRow(nt, "", _notifyLow);
        AddRow(nt, "", _notifyFull);
        AddRow(nt, Loc.T("set.lowThreshold"), _lowThreshold);
        root.Controls.Add(notifs, 0, 1);

        var colors = Group(Loc.T("set.colors"), out var ct);
        AddRow(ct, Loc.T("set.orange"), _orange);
        AddRow(ct, Loc.T("set.red"), _red);
        root.Controls.Add(colors, 0, 2);

        // --- Appareils
        var devices = new GroupBox { Text = Loc.T("set.devices"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        var dt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        dt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dt.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dt.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        devices.Controls.Add(dt);

        ConfigureGrid();
        dt.Controls.Add(_grid, 0, 0);

        var moves = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(6, 0, 0, 0) };
        var up = new Button { Text = Loc.T("set.up"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var down = new Button { Text = Loc.T("set.down"), AutoSize = true, MinimumSize = new Size(90, 28) };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        moves.Controls.Add(up);
        moves.Controls.Add(down);
        dt.Controls.Add(moves, 1, 0);

        var hint = new Label { Text = Loc.T("set.hint"), Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
        dt.Controls.Add(hint, 0, 1);
        dt.SetColumnSpan(hint, 2);
        root.Controls.Add(devices, 0, 3);

        // --- Boutons
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var ok = new Button { Text = Loc.T("set.ok"), DialogResult = DialogResult.OK, MinimumSize = new Size(90, 30) };
        var cancel = new Button { Text = Loc.T("set.cancel"), DialogResult = DialogResult.Cancel, MinimumSize = new Size(90, 30) };
        ok.Click += (_, _) => { if (Commit()) Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 4);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static GroupBox Group(string title, out TableLayoutPanel table)
    {
        table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        var box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 8) };
        box.Controls.Add(table);
        return box;
    }

    static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, row);
            t.Controls.Add(c, 1, row);
        }
        else
        {
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 2);
        }
        c.Margin = new Padding(3, 4, 3, 4);
    }

    void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersWidth = 22;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AllowDrop = true;

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

        _startup.Checked = Installer.IsStartupEnabled();
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
