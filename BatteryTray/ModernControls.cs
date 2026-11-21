using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>Couleurs de l'écran de paramètres, dans l'esprit des Paramètres de Windows 11 (clair ou sombre selon le système).</summary>
sealed record SettingsTheme(
    bool Light, Color Back, Color Footer, Color Card, Color CardBorder, Color Hover, Color Text, Color SubText,
    Color Accent, Color AccentText, Color Selection)
{
    /// <summary>Pour les aperçus : force le thème (true = clair, false = sombre) au lieu de suivre Windows.</summary>
    public static bool? Forced { get; set; }

    public static SettingsTheme Current() => Create(Forced ?? Theme.AppsAreLight());

    public static SettingsTheme Create(bool light) => light
        ? new SettingsTheme(true,
            Back: Color.FromArgb(243, 243, 243), Footer: Color.FromArgb(238, 238, 238),
            Card: Color.FromArgb(251, 251, 251), CardBorder: Color.FromArgb(229, 229, 229), Hover: Color.FromArgb(234, 234, 234),
            Text: Color.FromArgb(26, 26, 26), SubText: Color.FromArgb(96, 96, 96),
            Accent: Color.FromArgb(0, 95, 184), AccentText: Color.White, Selection: Color.FromArgb(214, 232, 248))
        : new SettingsTheme(false,
            Back: Color.FromArgb(32, 32, 32), Footer: Color.FromArgb(28, 28, 28),
            Card: Color.FromArgb(43, 43, 43), CardBorder: Color.FromArgb(58, 58, 58), Hover: Color.FromArgb(50, 50, 50),
            Text: Color.White, SubText: Color.FromArgb(197, 197, 197),
            Accent: Color.FromArgb(96, 205, 255), AccentText: Color.Black, Selection: Color.FromArgb(52, 74, 92));
}

static class Gdi
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Police d'icônes de Windows 11 (Segoe Fluent Icons), à défaut Segoe MDL2 Assets.</summary>
    public static Font IconFont(float size)
    {
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try { return new Font(new FontFamily(name), size, GraphicsUnit.Pixel); }
            catch (ArgumentException) { /* police absente : on essaie la suivante */ }
        }
        return new Font("Segoe UI Symbol", size, GraphicsUnit.Pixel);
    }

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? subIdList);

    public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}

/// <summary>Interrupteur à bascule (comme dans les Paramètres de Windows).</summary>
sealed class ToggleSwitch : CheckBox
{
    public SettingsTheme Palette { get; set; } = SettingsTheme.Current();

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 24);
        AutoSize = false;
        Cursor = Cursors.Hand;
        Text = "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 2, Width - 3, Height - 5);
        using (var path = Gdi.Round(track, track.Height / 2))
        {
            if (Checked) { using var b = new SolidBrush(Palette.Accent); g.FillPath(b, path); }
            else { using var pen = new Pen(Palette.SubText, 1.2f); g.DrawPath(pen, path); }
        }
        float d = Checked ? 14f : 12f;
        float x = Checked ? track.Right - d - 4 : track.X + 4;
        var knob = new RectangleF(x, track.Y + (track.Height - d) / 2, d, d);
        using (var kb = new SolidBrush(Checked ? Palette.AccentText : Palette.SubText)) g.FillEllipse(kb, knob);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Palette.Text, 1.5f);
            using var path = Gdi.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12);
            g.DrawPath(pen, path);
        }
    }
}

/// <summary>Carte arrondie : un réglage par carte, titre à gauche et contrôle à droite (inversé en arabe).</summary>
sealed class Card : Panel
{
    public SettingsTheme Palette { get; set; }
    public const int Gap = 8;
    public string Title { get; set; } = "";
    /// <summary>Contrôles placés à droite de la carte, de la droite vers la gauche.</summary>
    public List<Control> Trailing { get; } = new();
    readonly bool _rtl = Loc.IsRtl;

    public Card(SettingsTheme palette, int height = 56)
    {
        Palette = palette;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = height + Gap;
        BackColor = palette.Back;
    }

    public void AddTrailing(Control c)
    {
        Trailing.Add(c);
        Controls.Add(c);
        c.BackColor = Palette.Card;
        DoLayout();
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); DoLayout(); }

    void DoLayout()
    {
        int h = Height - Gap;
        int x = _rtl ? 16 : Width - 16;
        foreach (var c in Trailing)
        {
            if (_rtl) { c.Location = new Point(x, (h - c.Height) / 2); x += c.Width + 10; }
            else { x -= c.Width; c.Location = new Point(x, (h - c.Height) / 2); x -= 10; }
        }
        Invalidate();
    }

    public int TrailingEdge => Trailing.Count == 0 ? (_rtl ? Width : 0) : (_rtl ? Trailing.Max(t => t.Right) : Trailing.Min(t => t.Left));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - Gap - 1);
        using (var path = Gdi.Round(rect, 6))
        {
            using var fill = new SolidBrush(Palette.Card); g.FillPath(fill, path);
            using var pen = new Pen(Palette.CardBorder); g.DrawPath(pen, path);
        }
        if (Title.Length == 0) return;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int free = Math.Max(60, (_rtl ? Width - 16 - TrailingEdge : TrailingEdge - 16) - 24);
        var textRect = _rtl
            ? new Rectangle(Width - 16 - free, 0, free, Height - Gap)
            : new Rectangle(16, 0, free, Height - Gap);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | (_rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
        TextRenderer.DrawText(g, Title, Font, textRect, Palette.Text, flags);
    }
}

/// <summary>Bouton arrondi : « principal » (couleur d'accentuation) ou « secondaire » (comme une carte).</summary>
sealed class ModernButton : Button
{
    public SettingsTheme Palette { get; set; } = SettingsTheme.Current();
    public bool Primary { get; set; }
    bool _hot, _down;

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        Size = new Size(110, 34);
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, border, text;
        if (Primary)
        {
            fill = _down ? Blend(Palette.Accent, Palette.Back, 0.25f) : _hot ? Blend(Palette.Accent, Palette.Back, 0.1f) : Palette.Accent;
            border = fill; text = Palette.AccentText;
        }
        else
        {
            fill = _down ? Palette.Back : _hot ? Palette.Hover : Palette.Card;
            border = Palette.CardBorder; text = Palette.Text;
        }
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Gdi.Round(rect, 5))
        {
            using var b = new SolidBrush(fill); g.FillPath(b, path);
            using var pen = new Pen(border); g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Palette.Text, 1.5f) { DashStyle = DashStyle.Dot };
            using var path = Gdi.Round(new RectangleF(3, 3, Width - 7, Height - 7), 3);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (Loc.IsRtl ? TextFormatFlags.RightToLeft : 0));
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Entrée de la barre de navigation à gauche : icône + texte, trait d'accentuation quand elle est active.</summary>
sealed class NavItem : Control
{
    public SettingsTheme Palette { get; set; } = SettingsTheme.Current();
    public string Glyph { get; set; } = "";
    public bool Selected { get; set; }
    bool _hot;
    readonly Font _icons = Gdi.IconFont(16);

    public NavItem()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 40;
    }

    public void SetSelected(bool value) { Selected = value; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool rtl = Loc.IsRtl;
        var rect = new RectangleF(0, 2, Width - 1, Height - 5);
        if (Selected || _hot)
        {
            using var path = Gdi.Round(rect, 5);
            using var b = new SolidBrush(Selected ? Palette.Hover : Color.FromArgb(Palette.Hover.R, Palette.Hover.G, Palette.Hover.B));
            if (Selected || _hot) g.FillPath(b, path);
        }
        if (Selected)
        {
            float x = rtl ? Width - 5 : 1;
            using var path = Gdi.Round(new RectangleF(x, Height / 2f - 8, 3, 16), 1.5f);
            using var b = new SolidBrush(Palette.Accent);
            g.FillPath(b, path);
        }
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var icon = new Rectangle(rtl ? Width - 44 : 10, 0, 34, Height);
        TextRenderer.DrawText(g, Glyph, _icons, icon, Palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var label = rtl ? new Rectangle(8, 0, Width - 56, Height) : new Rectangle(46, 0, Width - 54, Height);
        TextRenderer.DrawText(g, Text, Font, label, Palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Palette.Text, 1.5f) { DashStyle = DashStyle.Dot };
            using var path = Gdi.Round(new RectangleF(2, 4, Width - 5, Height - 9), 4);
            g.DrawPath(pen, path);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _icons.Dispose(); base.Dispose(disposing); }
}
