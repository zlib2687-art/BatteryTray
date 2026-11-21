using System.Drawing.Drawing2D;

namespace BatteryTray;

/// <summary>
/// Visionneuse de l'historique de la batterie (24 h, 7 jours, 30 jours) : courbe par appareil avec interruptions aux endroits
/// où il n'y a pas de mesure (rien n'est inventé entre deux mesures éloignées), dernière lecture, minimum, maximum, variation
/// et nombre de mesures. Style des Paramètres de Windows 11, thème clair ou sombre. L'historique d'un appareil peut être effacé.
/// </summary>
sealed class HistoryForm : Form
{
    readonly BatteryHistory _history;
    readonly SettingsTheme _t = SettingsTheme.Current();
    readonly ModernCombo _devices = new() { Width = 300 };
    readonly ModernButton[] _ranges = new ModernButton[3];
    readonly HistoryChart _chart;
    readonly Label[] _statValue = new Label[5];
    readonly Label[] _statCaption = new Label[5];
    readonly ModernButton _clear = new(), _close = new();
    readonly Label _deviceLabel = new();
    int _range; // 0 = 24 h, 1 = 7 jours, 2 = 30 jours

    static readonly TimeSpan[] Spans = { TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30) };
    static readonly TimeSpan[] Gaps = { TimeSpan.FromHours(2.5), TimeSpan.FromHours(6), TimeSpan.FromHours(12) };

    public HistoryForm(BatteryHistory history, string? selectId = null)
    {
        _history = history;
        Text = Loc.T("hist.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(880, 620);
        BackColor = _t.Back; ForeColor = _t.Text;
        Font = new Font(Loc.FontFamily, 9.75f);
        if (Loc.IsRtl) RightToLeft = RightToLeft.Yes;
        DoubleBuffered = true;

        _chart = new HistoryChart(_t) { Location = new Point(24, 78), Size = new Size(832, 340) };
        Controls.Add(_chart);

        _deviceLabel.Text = Loc.T("hist.device");
        _deviceLabel.AutoSize = true; _deviceLabel.ForeColor = _t.SubText; _deviceLabel.BackColor = _t.Back;
        Controls.Add(_deviceLabel);

        _devices.Palette = _t; _devices.BackColor = _t.Back;
        Controls.Add(_devices);

        string[] keys = { "hist.24h", "hist.7d", "hist.30d" };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var b = new ModernButton { Palette = _t, Text = Loc.T(keys[i]), Width = 96, Height = 34, BackColor = _t.Back };
            b.Click += (_, _) => { _range = index; Reload(); };
            _ranges[i] = b;
            Controls.Add(b);
        }

        string[] captions = { "hist.latest", "hist.lowest", "hist.highest", "hist.change", "hist.samples" };
        for (int i = 0; i < 5; i++)
        {
            _statValue[i] = new Label { AutoSize = false, Size = new Size(156, 34), Font = new Font(Loc.FontFamily, 17f, FontStyle.Bold), ForeColor = _t.Text, BackColor = _t.Back, TextAlign = ContentAlignment.MiddleLeft };
            _statCaption[i] = new Label { AutoSize = false, Size = new Size(156, 22), Text = Loc.T(captions[i]), ForeColor = _t.SubText, BackColor = _t.Back, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(_statValue[i]); Controls.Add(_statCaption[i]);
        }

        _clear.Palette = _t; _clear.Text = Loc.T("hist.clear"); _clear.Size = new Size(330, 34); _clear.BackColor = _t.Back;
        _clear.Click += (_, _) => ClearSelected();
        _close.Palette = _t; _close.Primary = true; _close.Text = Loc.T("hist.close"); _close.Size = new Size(110, 34); _close.BackColor = _t.Back;
        _close.DialogResult = DialogResult.Cancel;
        _close.Click += (_, _) => Close();
        Controls.Add(_clear); Controls.Add(_close);
        CancelButton = _close;

        _devices.SelectedIndexChanged += (_, _) => Reload();
        FillDevices(selectId);
        LayoutAll();
        Reload();
    }

    /// <summary>Choisit la période affichée (0 = 24 h, 1 = 7 jours, 2 = 30 jours).</summary>
    public void SetRange(int range) { _range = Math.Clamp(range, 0, 2); Reload(); }

    record Item(string Id, string Name) { public override string ToString() => Name; }

    void FillDevices(string? selectId)
    {
        _devices.Items.Clear();
        foreach (var (id, name) in _history.Devices()) _devices.Items.Add(new Item(id, name));
        int at = selectId == null ? 0 : Math.Max(0, _devices.Items.FindIndex(i => ((Item)i).Id == selectId));
        _devices.SelectedIndex = _devices.Items.Count == 0 ? -1 : at;
    }

    // Les mises en page sont écrites de gauche à droite ; X() les retourne pour l'arabe.
    int X(int x, int width) => Loc.IsRtl ? ClientSize.Width - x - width : x;

    void LayoutAll()
    {
        const int top = 20;
        _deviceLabel.Location = new Point(X(24, _deviceLabel.Width), top + 8);
        _devices.Location = new Point(X(24 + _deviceLabel.Width + 12, _devices.Width), top);
        int rx = 880 - 24 - 3 * 96 - 2 * 8;
        for (int i = 0; i < 3; i++) _ranges[i].Location = new Point(X(rx + i * 104, 96), top);
        for (int i = 0; i < 5; i++)
        {
            _statValue[i].Location = new Point(X(24 + i * 168, 156), 436);
            _statCaption[i].Location = new Point(X(24 + i * 168, 156), 470);
        }
        _clear.Location = new Point(X(24, _clear.Width), 560);
        _close.Location = new Point(X(880 - 24 - 110, 110), 560);
        _chart.Location = new Point(24, 78);
    }

    void Reload()
    {
        foreach (var (b, i) in _ranges.Select((b, i) => (b, i))) b.Primary = i == _range;
        foreach (var b in _ranges) b.Invalidate();

        var id = (_devices.SelectedItem as Item)?.Id;
        var points = id == null ? Array.Empty<BatteryHistory.Point>() : _history.Points(id, Spans[_range], DateTime.Now);
        _chart.SetData(points, Spans[_range], Gaps[_range], _range == 0);

        var s = BatteryHistory.Summarize(points);
        _statValue[0].Text = s.Latest is { } l ? $"{l.P} %" : "—";
        _statValue[1].Text = s.Count > 0 ? $"{s.Lowest} %" : "—";
        _statValue[2].Text = s.Count > 0 ? $"{s.Highest} %" : "—";
        _statValue[3].Text = s.Count > 1 ? $"{(s.Change > 0 ? "+" : "")}{s.Change}" : "—";
        _statValue[4].Text = s.Count.ToString();
        _clear.Enabled = id != null;
    }

    void ClearSelected()
    {
        if (_devices.SelectedItem is not Item item) return;
        if (MessageBox.Show(Loc.T("hist.confirm", item.Name), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _history.Clear(item.Id);
        FillDevices(null);
        Reload();
    }

    /// <summary>Aperçu pour les tests visuels : dessine la fenêtre dans une image (comme SettingsForm.RenderPreview).</summary>
    public Bitmap RenderPreview()
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-5000, -5000);
        ShowInTaskbar = false;
        Show();
        for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(60); }
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }
}

/// <summary>Courbe du niveau : grille 0–100 %, tronçons séparés aux trous de mesure, points de charge en vert.</summary>
sealed class HistoryChart : Control
{
    readonly SettingsTheme _t;
    IReadOnlyList<BatteryHistory.Point> _points = Array.Empty<BatteryHistory.Point>();
    TimeSpan _span = TimeSpan.FromHours(24), _gap = TimeSpan.FromHours(2.5);
    bool _timeLabels;

    public HistoryChart(SettingsTheme theme)
    {
        _t = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = theme.Back;
    }

    public void SetData(IReadOnlyList<BatteryHistory.Point> points, TimeSpan span, TimeSpan gap, bool timeLabels)
    {
        _points = points; _span = span; _gap = gap; _timeLabels = timeLabels;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        bool rtl = Loc.IsRtl;

        var card = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gdi.Round(card, 8))
        {
            using var fill = new SolidBrush(_t.Card); g.FillPath(fill, path);
            using var border = new Pen(_t.CardBorder); g.DrawPath(border, path);
        }

        const int left = 56, right = 20, topPad = 22, bottomPad = 40;
        var area = new Rectangle(rtl ? right : left, topPad, Width - left - right, Height - topPad - bottomPad);

        using (var grid = new Pen(Color.FromArgb(70, _t.SubText), 1f))
        using (var subBrush = new SolidBrush(_t.SubText))
        {
            foreach (int level in new[] { 0, 25, 50, 75, 100 })
            {
                float y = area.Bottom - area.Height * level / 100f;
                g.DrawLine(grid, area.Left, y, area.Right, y);
                var labelRect = rtl ? new Rectangle(area.Right + 6, (int)y - 8, 44, 16) : new Rectangle(area.Left - 50, (int)y - 8, 44, 16);
                TextRenderer.DrawText(g, level + " %", Font, labelRect, _t.SubText,
                    (rtl ? TextFormatFlags.Left : TextFormatFlags.Right) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        if (_points.Count == 0)
        {
            TextRenderer.DrawText(g, Loc.T("hist.none"), Font, area, _t.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var end = DateTime.Now;
        var start = end - _span;
        float X(DateTime t)
        {
            double f = Math.Clamp((t - start).TotalSeconds / _span.TotalSeconds, 0, 1);
            return rtl ? area.Right - (float)f * area.Width : area.Left + (float)f * area.Width;
        }
        float Y(int p) => area.Bottom - area.Height * Math.Clamp(p, 0, 100) / 100f;

        using var line = new Pen(_t.Accent, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var segment in BatteryHistory.Segments(_points, _gap))
        {
            if (segment.Count >= 2) g.DrawLines(line, segment.Select(p => new PointF(X(p.T), Y(p.P))).ToArray());
        }
        // chaque mesure : petit point (vert si l'appareil était branché)
        if (_points.Count <= 400)
            foreach (var p in _points)
            {
                using var dot = new SolidBrush(p.Charging ? Theme.ChargingColor(_t.Light) : _t.Accent);
                g.FillEllipse(dot, X(p.T) - 3, Y(p.P) - 3, 6, 6);
            }

        string fmt = _timeLabels ? "HH:mm" : "dd/MM";
        var first = _timeLabels ? start : start.Date;
        TextRenderer.DrawText(g, start.ToString(fmt), Font, new Rectangle(rtl ? area.Right - 90 : area.Left, area.Bottom + 8, 90, 18), _t.SubText,
            (rtl ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, end.ToString(fmt), Font, new Rectangle(rtl ? area.Left : area.Right - 90, area.Bottom + 8, 90, 18), _t.SubText,
            (rtl ? TextFormatFlags.Left : TextFormatFlags.Right) | TextFormatFlags.NoPadding);
        _ = first;
    }
}
