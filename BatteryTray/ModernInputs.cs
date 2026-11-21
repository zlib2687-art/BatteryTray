using System.Drawing.Drawing2D;

namespace BatteryTray;

/// <summary>
/// Liste déroulante façon Windows 11 : champ arrondi avec chevron, fenêtre de choix aux coins arrondis, ligne survolée
/// éclaircie, choix actuel marqué d'un trait d'accentuation. Remplace le ComboBox standard (flèche et liste d'un autre âge).
/// </summary>
sealed class ModernCombo : Control
{
    public SettingsTheme Palette { get; set; } = SettingsTheme.Current();
    public List<object> Items { get; } = new();
    public event EventHandler? SelectedIndexChanged;

    int _index = -1;
    bool _hot;
    DateTime _closedAt = DateTime.MinValue;
    ComboPopup? _popup;

    public ModernCombo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(250, 34);
    }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            int v = Items.Count == 0 ? -1 : Math.Clamp(value, -1, Items.Count - 1);
            if (v == _index) return;
            _index = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem => _index >= 0 && _index < Items.Count ? Items[_index] : null;

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        // le clic qui ferme la liste la désactive d'abord : on ne la rouvre pas dans la foulée
        if (e.Button == MouseButtons.Left && (DateTime.UtcNow - _closedAt).TotalMilliseconds > 250) OpenPopup();
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down when !e.Alt: SelectedIndex = Math.Min(Items.Count - 1, _index + 1); e.Handled = true; break;
            case Keys.Up: SelectedIndex = Math.Max(0, _index - 1); e.Handled = true; break;
            case Keys.Enter or Keys.Space or Keys.F4: OpenPopup(); e.Handled = true; break;
            case Keys.Down when e.Alt: OpenPopup(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Focused) SelectedIndex = Math.Clamp(_index + (e.Delta > 0 ? -1 : 1), 0, Items.Count - 1);
    }

    void OpenPopup()
    {
        if (_popup != null || Items.Count == 0 || !IsHandleCreated) return;
        _popup = new ComboPopup(this);
        _popup.FormClosed += (_, _) => { _popup = null; _closedAt = DateTime.UtcNow; Invalidate(); };
        _popup.ShowBelow();
    }

    internal void Choose(int index) => SelectedIndex = index;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool rtl = Loc.IsRtl;
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Color fill = _popup != null ? Palette.Back : _hot ? Palette.Hover : Palette.Hover;
        if (_hot || _popup != null) fill = Mix(Palette.Hover, Palette.Text, Palette.Light ? 0.06f : 0.08f);
        using (var path = Gdi.Round(rect, 5))
        {
            using var b = new SolidBrush(fill); g.FillPath(b, path);
            using var pen = new Pen(Palette.CardBorder); g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Palette.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 8, Height - 2, Width - 9, Height - 2);
        }

        // chevron
        float cx = rtl ? 18 : Width - 18, cy = Height / 2f;
        using (var pen = new Pen(Palette.SubText, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(cx - 4.5f, cy - 2f), new PointF(cx, cy + 2.5f), new PointF(cx + 4.5f, cy - 2f) });

        var textRect = rtl ? new Rectangle(34, 0, Width - 46, Height) : new Rectangle(12, 0, Width - 46, Height);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font, textRect, Palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
    }

    internal static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Fenêtre de choix d'une <see cref="ModernCombo"/> : coins arrondis, ombre, fermeture dès qu'elle perd le focus.</summary>
sealed class ComboPopup : Form
{
    const int ItemH = 36, PadV = 6;
    readonly ModernCombo _combo;
    readonly SettingsTheme _t;
    int _hover;

    public ComboPopup(ModernCombo combo)
    {
        _combo = combo;
        _t = combo.Palette;
        _hover = combo.SelectedIndex;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = _t.Card;
        Font = combo.Font;
        if (Loc.IsRtl) RightToLeft = RightToLeft.Yes;
        Size = new Size(combo.Width, combo.Items.Count * ItemH + PadV * 2 + 2);
        Deactivate += (_, _) => Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW : pas dans Alt+Tab
            cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int v = _t.Light ? 0 : 1; Gdi.DwmSetWindowAttribute(Handle, 20, ref v, 4);
            v = 2; Gdi.DwmSetWindowAttribute(Handle, 33, ref v, 4); // coins arrondis Windows 11
        }
        catch { }
    }

    public void ShowBelow()
    {
        var screen = _combo.Parent!.RectangleToScreen(_combo.Bounds);
        var wa = Screen.FromRectangle(screen).WorkingArea;
        int y = screen.Bottom + 2;
        if (y + Height > wa.Bottom) y = screen.Top - Height - 2; // pas la place en dessous : au-dessus
        Location = new Point(Math.Clamp(screen.Left, wa.Left, Math.Max(wa.Left, wa.Right - Width)), y);
        Show(_combo.FindForm());
        Activate();
    }

    int IndexAt(int y) => y < PadV || y >= Height - PadV - 2 ? -1 : (y - PadV - 1) / ItemH;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i != _hover && i < _combo.Items.Count) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = IndexAt(e.Y);
        if (e.Button == MouseButtons.Left && i >= 0 && i < _combo.Items.Count) { _combo.Choose(i); Close(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int n = _combo.Items.Count;
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.Down: _hover = Math.Min(n - 1, _hover + 1); Invalidate(); break;
            case Keys.Up: _hover = Math.Max(0, _hover - 1); Invalidate(); break;
            case Keys.Enter or Keys.Space: if (_hover >= 0) _combo.Choose(_hover); Close(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool rtl = Loc.IsRtl;
        using (var border = new Pen(_t.CardBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        for (int i = 0; i < _combo.Items.Count; i++)
        {
            var row = new RectangleF(5, PadV + 1 + i * ItemH, Width - 11, ItemH - 2);
            bool selected = i == _combo.SelectedIndex;
            if (i == _hover || selected)
            {
                using var path = Gdi.Round(row, 5);
                using var b = new SolidBrush(i == _hover ? _t.Hover : ModernCombo.Mix(_t.Card, _t.Hover, 0.5f));
                g.FillPath(b, path);
            }
            if (selected)
            {
                float x = rtl ? row.Right - 5 : row.X + 1;
                using var path = Gdi.Round(new RectangleF(x, row.Y + row.Height / 2 - 8, 3, 16), 1.5f);
                using var b = new SolidBrush(_t.Accent);
                g.FillPath(b, path);
            }
            var text = new Rectangle((int)row.X + 12, (int)row.Y, (int)row.Width - 24, (int)row.Height);
            TextRenderer.DrawText(g, _combo.Items[i].ToString() ?? "", Font, text, _t.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
        }
    }
}

/// <summary>
/// Champ numérique façon Windows 11 : champ arrondi, valeur modifiable au clavier, petits chevrons haut/bas à droite,
/// molette et flèches du clavier. Même propriétés que NumericUpDown (Minimum, Maximum, Value).
/// </summary>
sealed class ModernNumeric : Control
{
    public SettingsTheme Palette { get; set; } = SettingsTheme.Current();
    public decimal Minimum { get; set; } = 0;
    public decimal Maximum { get; set; } = 100;
    public event EventHandler? ValueChanged;

    decimal _value;
    string? _typing;           // texte en cours de saisie (null = on affiche la valeur)
    int _hotPart;              // 1 = champ, 2 = chevron haut, 3 = chevron bas
    const int ButtonsW = 44;

    public ModernNumeric()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Size = new Size(110, 34);
        Cursor = Cursors.IBeam;
    }

    public decimal Value
    {
        get => _value;
        set
        {
            decimal v = Math.Clamp(Math.Round(value), Minimum, Maximum);
            _typing = null;
            if (v == _value) { Invalidate(); return; }
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    bool UpRect(Point p) => p.X >= (Loc.IsRtl ? 0 : Width - ButtonsW) && p.X < (Loc.IsRtl ? ButtonsW : Width) && p.Y < Height / 2;
    bool DownRect(Point p) => p.X >= (Loc.IsRtl ? 0 : Width - ButtonsW) && p.X < (Loc.IsRtl ? ButtonsW : Width) && p.Y >= Height / 2;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int part = UpRect(e.Location) ? 2 : DownRect(e.Location) ? 3 : 1;
        if (part != _hotPart) { _hotPart = part; Cursor = part == 1 ? Cursors.IBeam : Cursors.Hand; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hotPart = 0; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            if (UpRect(e.Location)) Step(+1);
            else if (DownRect(e.Location)) Step(-1);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { if (Focused) Step(e.Delta > 0 ? +1 : -1); base.OnMouseWheel(e); }

    void Step(int delta) { Commit(); Value = _value + delta; }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Step(+1); e.Handled = true; break;
            case Keys.Down: Step(-1); e.Handled = true; break;
            case Keys.Enter: Commit(); e.Handled = true; break;
            case Keys.Back: if (!string.IsNullOrEmpty(_typing ?? _value.ToString())) { _typing = (_typing ?? _value.ToString()); _typing = _typing[..^1]; Invalidate(); } e.Handled = true; break;
            case Keys.Escape: _typing = null; Invalidate(); break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (char.IsDigit(e.KeyChar)) { _typing = ((_typing ?? "") + e.KeyChar).TrimStart('0'); if (_typing.Length > 3) _typing = _typing[^3..]; Invalidate(); e.Handled = true; }
        base.OnKeyPress(e);
    }

    protected override void OnLostFocus(EventArgs e) { Commit(); Invalidate(); base.OnLostFocus(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }

    void Commit()
    {
        if (_typing == null) return;
        string t = _typing; _typing = null;
        if (decimal.TryParse(t, out var v)) Value = v; else Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool rtl = Loc.IsRtl;
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gdi.Round(rect, 5))
        {
            using var b = new SolidBrush(Focused ? Palette.Back : Palette.Hover); g.FillPath(b, path);
            using var pen = new Pen(Palette.CardBorder); g.DrawPath(pen, path);
        }
        if (Focused)
        {
            using var pen = new Pen(Palette.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 8, Height - 2, Width - 9, Height - 2);
        }

        // chevrons haut et bas, avec pastille au survol
        float bx = rtl ? 4 : Width - ButtonsW + 4;
        foreach (int part in new[] { 2, 3 })
        {
            var cell = new RectangleF(bx, part == 2 ? 3 : Height / 2f, ButtonsW - 8, Height / 2f - 3);
            if (_hotPart == part)
            {
                using var hp = Gdi.Round(cell, 4);
                using var hb = new SolidBrush(ModernCombo.Mix(Palette.Hover, Palette.Text, Palette.Light ? 0.1f : 0.12f));
                g.FillPath(hb, hp);
            }
            float cx = cell.X + cell.Width / 2, cy = cell.Y + cell.Height / 2;
            float d = part == 2 ? -1.8f : 1.8f;
            using var pen = new Pen(Palette.SubText, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - 4, cy - d), new PointF(cx, cy + d), new PointF(cx + 4, cy - d) });
        }

        string text = _typing ?? _value.ToString();
        var textRect = rtl ? new Rectangle(ButtonsW, 0, Width - ButtonsW - 10, Height) : new Rectangle(10, 0, Width - ButtonsW - 10, Height);
        TextRenderer.DrawText(g, text, Font, textRect, Palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
        if (Focused && _typing != null)
        {
            var size = TextRenderer.MeasureText(g, text, Font, new Size(200, Height), TextFormatFlags.NoPadding);
            int x = rtl ? Width - 10 - size.Width : 10 + size.Width;
            using var pen = new Pen(Palette.Text, 1f); g.DrawLine(pen, x, Height / 2 - 8, x, Height / 2 + 8);
        }
    }
}
