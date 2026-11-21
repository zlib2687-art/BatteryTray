using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>
/// Fenêtre qui s'ouvre au clic sur une icône : un appareil par ligne (pictogramme, nom, pourcentage,
/// état de charge / temps de charge / autonomie estimée / batteries séparées, heure de lecture, barre de niveau).
/// Un clic sur une ligne déplie un mini-graphique du niveau depuis la dernière charge. Les appareils
/// déconnectés restent affichés en gris avec leur dernière valeur.
/// Dessinée en GDI+ pour rester légère. Coins arrondis et fond Acrylic via DWM (Windows 11) ; se ferme dès
/// qu'elle perd le focus. S'affiche de droite à gauche pour l'arabe. Sa taille se règle dans les paramètres
/// (<see cref="AppSettings.FlyoutScale"/>) : tout est dessiné dans un repère de 312 px de large puis agrandi.
/// </summary>
sealed class FlyoutForm : Form
{
    // repère de dessin (taille « 100 % »)
    const int RowH = 62, HeaderH = 42, Pad = 12, W = 312, ChartExtra = 66;
    int _hoverRow = -1;
    bool _hoverGear;

    public static readonly int[] ScaleChoices = { 85, 100, 125, 150 };

    IReadOnlyList<BatteryReading> _items = Array.Empty<BatteryReading>();
    string? _expandedId;
    DateTime _lastHide = DateTime.MinValue;
    DateTime _shownAt = DateTime.MinValue;
    readonly System.Windows.Forms.Timer _regainTimer = new();

    /// <summary>Fournit les mesures récentes d'un appareil (pour le mini-graphique).</summary>
    public Func<string, IReadOnlyList<AutonomyEstimator.Sample>>? History { get; set; }

    /// <summary>Taille de la fenêtre pour un nombre de lignes et un pourcentage d'agrandissement (logique pure, testée).</summary>
    public static Size SizeFor(int rows, int scalePercent, bool expanded = false)
    {
        double k = Math.Clamp(scalePercent, 70, 200) / 100.0;
        int designHeight = HeaderH + Math.Max(1, rows) * RowH + Pad + (expanded ? ChartExtra : 0);
        return new Size((int)Math.Round(W * k), (int)Math.Round(designHeight * k));
    }

    static float K => Math.Clamp(AppSettings.Current.FlyoutScale, 70, 200) / 100f;

    int ExpandedIndex => _expandedId == null ? -1 : _items.ToList().FindIndex(i => i.Id == _expandedId);

    /// <summary>Haut de la ligne i dans le repère de dessin (les lignes sous la ligne dépliée sont décalées).</summary>
    int RowTop(int i)
    {
        int e = ExpandedIndex;
        return HeaderH + i * RowH + (e >= 0 && i > e ? ChartExtra : 0);
    }

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        Size = SizeFor(1, AppSettings.Current.FlyoutScale);
        // Juste après le clic sur l'icône, la barre des tâches reprend brièvement le focus : on ignore
        // cette perte de focus initiale, puis on reprend le focus une fois la barre calmée.
        Deactivate += (_, _) =>
        {
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 500) return;
            HideFlyout();
        };
        _regainTimer.Interval = 550;
        _regainTimer.Tick += (_, _) =>
        {
            _regainTimer.Stop();
            if (Visible && ActiveForm != this) { SetForegroundWindow(Handle); Activate(); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HideFlyout(); };
    }

    public event Action? SettingsRequested;

    /// <summary>Zone cliquable de l'engrenage, dans le repère de dessin (côté fin de ligne : droite, ou gauche en arabe).</summary>
    static RectangleF GearRect()
    {
        var r = new RectangleF(W - Pad - 30, 6, 30, 30);
        return Loc.IsRtl ? new RectangleF(W - r.Right, r.Y, r.Width, r.Height) : r;
    }

    /// <summary>Index de la ligne sous le point (repère de dessin), ou -1.</summary>
    int RowAt(float designY)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            float top = RowTop(i);
            float height = RowH + (i == ExpandedIndex ? ChartExtra : 0);
            if (designY >= top && designY < top + height) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        float k = K;
        bool gear = GearRect().Contains(e.X / k, e.Y / k);
        int row = RowAt(e.Y / k);
        Cursor = gear || row >= 0 ? Cursors.Hand : Cursors.Default;
        if (gear != _hoverGear || row != _hoverRow) { _hoverGear = gear; _hoverRow = row; Invalidate(); } // survol : la ligne s'éclaircit
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverGear || _hoverRow >= 0) { _hoverGear = false; _hoverRow = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        float k = K;

        if (GearRect().Contains(e.X / k, e.Y / k))
        {
            HideFlyout();
            SettingsRequested?.Invoke();
            return;
        }

        int row = RowAt(e.Y / k);
        if (row >= 0)
        {
            string id = _items[row].Id;
            _expandedId = _expandedId == id ? null : id; // un seul graphique ouvert à la fois
            RefreshLayout();
        }
    }

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW : pas dans Alt+Tab
            return cp;
        }
    }

    public void Toggle(IReadOnlyList<BatteryReading> items)
    {
        if (Visible) { HideFlyout(); return; }
        // le clic sur l'icône provoque d'abord la perte de focus (fermeture) : on ignore la réouverture immédiate
        if ((DateTime.UtcNow - _lastHide).TotalMilliseconds < 300) return;
        SetItems(items);
        PositionNearTray();
        ApplyDwmStyle();
        _shownAt = DateTime.UtcNow;
        Show();
        SetForegroundWindow(Handle);
        Activate();
        _regainTimer.Start();
    }

    public void SetItems(IReadOnlyList<BatteryReading> items)
    {
        _items = items.ToList(); // l'ordre vient des réglages (DeviceView)
        if (_expandedId != null && !_items.Any(i => i.Id == _expandedId)) _expandedId = null;
        Size = SizeFor(_items.Count, AppSettings.Current.FlyoutScale, ExpandedIndex >= 0);
        if (Visible) { PositionNearTray(); }
        Invalidate();
    }

    /// <summary>Déplie le graphique d'un appareil (utilisé par l'aperçu en image).</summary>
    public void ExpandForPreview(string id)
    {
        _expandedId = id;
        RefreshLayout();
    }

    /// <summary>Reprend la taille choisie dans les paramètres (ou après le dépliage d'une ligne).</summary>
    public void RefreshLayout() => SetItems(_items);

    /// <summary>Ferme le flyout (par exemple quand un double-clic sur l'icône ouvre les paramètres).</summary>
    public void Dismiss() => HideFlyout();

    void HideFlyout()
    {
        if (!Visible) return;
        Hide();
        _lastHide = DateTime.UtcNow;
    }

    void PositionNearTray()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }

    // ---------- style Windows 11 : coins arrondis + Acrylic ----------
    void ApplyDwmStyle()
    {
        try
        {
            bool dark = !Theme.AppsAreLight();
            int v;
            v = dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref v, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            v = 2;            DwmSetWindowAttribute(Handle, 33, ref v, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = arrondi
            v = 3;            DwmSetWindowAttribute(Handle, 38, ref v, 4);   // DWMWA_SYSTEMBACKDROP_TYPE = Acrylic (fenêtre transitoire)
            var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref m);
        }
        catch { /* anciennes versions de Windows : fond uni */ }
    }

    // ---------- dessin ----------
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Noir = transparent sur le verre DWM (Acrylic)
        e.Graphics.Clear(Color.Black);
    }

    static Font MakeFont(float px, bool strong)
    {
        string family = Loc.FontFamily;
        return family == "Segoe UI"
            ? new Font(strong ? "Segoe UI Semibold" : "Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font(family, px, strong ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = K;
        g.ScaleTransform(k, k); // tout ce qui suit est écrit pour la taille « 100 % »
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = Math.Abs(k - 1f) < 0.01f ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;

        float designHeight = Height / k;
        bool rtl = Loc.IsRtl;
        // Les mises en page sont écrites de gauche à droite ; M() les retourne pour l'arabe.
        RectangleF M(RectangleF r) => rtl ? new RectangleF(W - r.Right, r.Y, r.Width, r.Height) : r;
        StringFormat Fmt(StringAlignment a) => new(rtl ? StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap : StringFormatFlags.NoWrap)
        {
            Alignment = a,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        bool light = Theme.AppsAreLight();
        Color fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
        Color sub = light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(190, 190, 190);
        Color accent = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        Color card = light ? Color.FromArgb(205, 255, 255, 255) : Color.FromArgb(125, 52, 52, 52);
        Color cardHover = light ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(165, 70, 70, 70);
        Color cardBorder = light ? Color.FromArgb(34, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255);
        Color dimmed = Color.FromArgb(125, 125, 125); // appareils hors ligne

        // voile de fond (teinte) pour que le texte reste lisible sur l'Acrylic
        using (var veil = new SolidBrush(light ? Color.FromArgb(170, 243, 243, 243) : Color.FromArgb(170, 28, 28, 28)))
            g.FillRectangle(veil, 0, 0, W, designHeight);

        using var title = MakeFont(15f, true);
        using var name = MakeFont(12.5f, true);
        using var small = MakeFont(10.5f, false);
        using var big = MakeFont(18f, true);
        using var fgBrush = new SolidBrush(fg);
        using var subBrush = new SolidBrush(sub);

        using (var near = Fmt(StringAlignment.Near))
            g.DrawString(Loc.T("app.title"), title, fgBrush, M(new RectangleF(Pad + 4, 10, W - 2 * Pad - 44, 22)), near);

        // engrenage (icône Windows 11) : ouvre les paramètres ; pastille arrondie au survol
        var gear = GearRect();
        if (_hoverGear)
            using (var hp = Glyphs.RoundRect(gear, 6)) using (var hb = new SolidBrush(cardHover)) g.FillPath(hb, hp);
        using (var icons = Gdi.IconFont(16))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            bool fluent = icons.Name.StartsWith("Segoe Fluent", StringComparison.Ordinal) || icons.Name.StartsWith("Segoe MDL2", StringComparison.Ordinal);
            if (fluent) g.DrawString("", icons, subBrush, new RectangleF(gear.X, gear.Y + 1, gear.Width, gear.Height), center);
            else using (var symbols = new Font("Segoe UI Symbol", 15f, FontStyle.Regular, GraphicsUnit.Pixel)) g.DrawString("⚙", symbols, subBrush, gear, center);
        }

        if (_items.Count == 0)
        {
            using var near = Fmt(StringAlignment.Near);
            near.FormatFlags &= ~StringFormatFlags.NoWrap;
            g.DrawString(Loc.T("flyout.empty"), small, subBrush, M(new RectangleF(Pad + 4, HeaderH + 8, W - 2 * Pad - 8, 30)), near);
            return;
        }

        const float glyphScale = 0.38f;
        int expanded = ExpandedIndex;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            int y = RowTop(i);
            bool open = i == expanded;

            // couleurs de la ligne : tout en gris quand l'appareil est hors ligne
            Color rowFg = it.Offline ? dimmed : fg;
            Color rowSub = it.Offline ? dimmed : sub;
            using var rowFgBrush = new SolidBrush(rowFg);

            // branché / en charge : pourcentage, barre et ligne d'état en vert
            bool plugged = it.IsPlugged && !it.Offline;
            Color green = Theme.ChargingColor(light);
            // couleur d'état : vert en charge, orange/rouge quand la batterie baisse, couleur d'accentuation sinon
            Color status = it.Offline ? dimmed : plugged ? green : Theme.LevelColor(it.Percent, accent);
            using var lineBrush = new SolidBrush(plugged ? green : rowSub);

            // carte arrondie avec fin liseré ; plus claire sous la souris
            var rect = M(new RectangleF(Pad, y, W - 2 * Pad, RowH - 8 + (open ? ChartExtra : 0)));
            using (var path = Glyphs.RoundRect(rect, 8))
            {
                using (var cb = new SolidBrush(i == _hoverRow ? cardHover : card)) g.FillPath(cb, path);
                using var border = new Pen(cardBorder, 1f);
                g.DrawPath(border, path);
            }

            // pastille de l'icône, teintée de la couleur d'état
            var tile = M(new RectangleF(Pad + 10, y + 9, 36, 36));
            using (var tp = Glyphs.RoundRect(tile, 9))
            using (var tb = new SolidBrush(Color.FromArgb(it.Offline ? 28 : 52, status)))
                g.FillPath(tb, tp);
            float gw = Glyphs.Width * glyphScale, gh = Glyphs.Height * glyphScale;
            Glyphs.Draw(g, it.Kind, rowFg, tile.X + (tile.Width - gw) / 2, tile.Y + (tile.Height - gh) / 2, glyphScale);

            // nom et ligne d'état
            using (var near = Fmt(StringAlignment.Near))
            {
                g.DrawString(it.Name, name, rowFgBrush, M(new RectangleF(Pad + 56, y + 7, 160, 18)), near);
                g.DrawString(SecondLine(it), small, lineBrush, M(new RectangleF(Pad + 56, y + 27, W - 2 * Pad - 66, 15)), near);
            }

            // pourcentage (« ~ » quand il est estimé)
            using var pb = new SolidBrush(it.Offline ? dimmed : plugged ? green : Theme.LevelColor(it.Percent, fg));
            using (var far = Fmt(StringAlignment.Far))
                g.DrawString(it.PercentText, big, pb, M(new RectangleF(W - Pad - 12 - 74, y + 5, 74, 26)), far);

            // barre de niveau arrondie
            if (it.Percent is int p)
            {
                var track = M(new RectangleF(Pad + 56, y + 44, W - 2 * Pad - 56 - 12, 4));
                using (var tp = Glyphs.RoundRect(track, 2)) using (var tb = new SolidBrush(Color.FromArgb(light ? 45 : 70, rowSub))) g.FillPath(tb, tp);
                float fill = Math.Max(4, track.Width * Math.Clamp(p, 0, 100) / 100f);
                var fillRect = new RectangleF(rtl ? track.Right - fill : track.X, track.Y, fill, track.Height);
                using (var fp = Glyphs.RoundRect(fillRect, 2)) using (var lb = new SolidBrush(status)) g.FillPath(lb, fp);
            }

            if (open) DrawChart(g, it, y, M, Fmt, small, sub, fg);
        }
    }

    /// <summary>Deuxième ligne : hors ligne, charge (+ temps restant), batteries séparées, autonomie…</summary>
    static string SecondLine(BatteryReading it)
    {
        string time = it.At.ToString("HH:mm");
        if (it.Offline) return $"{Loc.T("state.offline")} · {time}";

        if (it.IsCharging)
        {
            string eta = Loc.ChargeEtaText(it) is { } e ? " · " + e : $" · {time}";
            return Loc.ChargeText(it.Charge) + eta;
        }

        if (Loc.PartsText(it) is { } parts) return parts;

        string state = Loc.ChargeText(it.Charge);
        if (state.Length == 0) state = Loc.AutonomyText(it) ?? it.Detail ?? "";
        return state.Length > 0 ? $"{state} · {time}" : Loc.T("time.read", time);
    }

    /// <summary>Mini-graphique du niveau depuis la dernière charge (lignes à 0, 50 et 100 %).</summary>
    void DrawChart(Graphics g, BatteryReading it, int rowTop, Func<RectangleF, RectangleF> M, Func<StringAlignment, StringFormat> Fmt,
        Font small, Color sub, Color fg)
    {
        var samples = History?.Invoke(it.Id) ?? Array.Empty<AutonomyEstimator.Sample>();
        var area = M(new RectangleF(Pad + 8, rowTop + RowH - 2, W - 2 * (Pad + 8), ChartExtra - 28));

        using (var grid = new Pen(Color.FromArgb(70, sub), 1f))
        {
            g.DrawLine(grid, area.Left, area.Top, area.Right, area.Top);
            g.DrawLine(grid, area.Left, area.Top + area.Height / 2, area.Right, area.Top + area.Height / 2);
            g.DrawLine(grid, area.Left, area.Bottom, area.Right, area.Bottom);
        }

        using var subBrush = new SolidBrush(sub);
        if (samples.Count >= 2)
        {
            var (lo, hi) = ChartMath.Range(samples);
            using (var far = Fmt(StringAlignment.Far))
            {
                // repères de l'échelle (le haut et le bas du graphique)
                g.DrawString($"{hi} %", small, subBrush, new RectangleF(area.Right - 44, area.Top + 1, 42, 11), far);
                g.DrawString($"{lo} %", small, subBrush, new RectangleF(area.Right - 44, area.Bottom - 12, 42, 11), far);
            }
            var points = ChartMath.Map(samples, area, lo, hi);
            using var line = new Pen(it.Offline ? Color.FromArgb(125, 125, 125) : Theme.LevelColor(it.Percent, fg), 2f) { LineJoin = LineJoin.Round };
            g.DrawLines(line, points);

            using var left = Fmt(StringAlignment.Near);
            using var right = Fmt(StringAlignment.Far);
            var labels = new RectangleF(area.X, area.Bottom + 3, area.Width, 12);
            g.DrawString(samples[0].T.ToString("HH:mm"), small, subBrush, labels, left);
            g.DrawString(samples[^1].T.ToString("HH:mm"), small, subBrush, labels, right);
        }
        else
        {
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Loc.T("chart.none"), small, subBrush, area, center);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
}
