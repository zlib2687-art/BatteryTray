using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// Gère une icône de zone de notification par appareil : un pictogramme (casque, souris,
/// clavier, manette, webcam) avec le pourcentage dessous. Le nom, l'état de charge ou l'autonomie
/// et l'heure de lecture sont dans l'infobulle ; un clic gauche ouvre le flyout ; un clic droit
/// ouvre le menu (actualiser, langue, notifications, quitter).
/// Le pictogramme suit le thème de la barre des tâches ; seul le chiffre passe en orange puis
/// en rouge quand la batterie baisse.
/// </summary>
public sealed class TrayManager : IDisposable, IToastSink
{
    sealed class Entry { public NotifyIcon Tray = null!; public Icon? Icon; }

    readonly Dictionary<string, Entry> _icons = new();
    readonly ContextMenuStrip _menu = new();
    readonly AppSettings _settings;
    readonly Action _onRefresh, _onExit, _onSettingsChanged, _onOpenSettings;
    readonly Action<string> _onLanguage;

    public event Action? IconClicked;
    /// <summary>Double-clic gauche sur une icône (ouvre les paramètres).</summary>
    public event Action? IconDoubleClicked;
    readonly ClickGate _clicks = new();

    // Seuils de couleur (repris des réglages au démarrage et à chaque modification)
    public static int OrangeBelow { get; set; } = 40;
    public static int RedBelow { get; set; } = 15;

    readonly Action? _onTestNotification;

    public TrayManager(AppSettings settings, Action onRefresh, Action onExit, Action<string> onLanguage,
        Action onSettingsChanged, Action onOpenSettings, Action? onTestNotification = null)
    {
        _onTestNotification = onTestNotification;
        _settings = settings;
        _onRefresh = onRefresh;
        _onExit = onExit;
        _onLanguage = onLanguage;
        _onSettingsChanged = onSettingsChanged;
        _onOpenSettings = onOpenSettings;
        BuildMenu();
    }

    /// <summary>(Re)construit le menu du clic droit dans la langue courante.</summary>
    public void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.RightToLeft = Loc.IsRtl ? RightToLeft.Yes : RightToLeft.No;

        _menu.Items.Add(Loc.T("menu.refresh"), null, (_, _) => _onRefresh());
        _menu.Items.Add(Loc.T("menu.settings"), null, (_, _) => _onOpenSettings());
        _menu.Items.Add(new ToolStripSeparator());

        var lang = new ToolStripMenuItem(Loc.T("menu.language"));
        foreach (var (code, native) in Loc.Languages)
        {
            var item = new ToolStripMenuItem(native) { Checked = code == Loc.Current };
            string c = code;
            item.Click += (_, _) => _onLanguage(c);
            lang.DropDownItems.Add(item);
        }
        _menu.Items.Add(lang);

        var low = new ToolStripMenuItem(Loc.T("menu.notifyLow")) { CheckOnClick = true, Checked = _settings.NotifyLow };
        low.CheckedChanged += (_, _) => { _settings.NotifyLow = low.Checked; _onSettingsChanged(); };
        var full = new ToolStripMenuItem(Loc.T("menu.notifyFull")) { CheckOnClick = true, Checked = _settings.NotifyFull };
        full.CheckedChanged += (_, _) => { _settings.NotifyFull = full.Checked; _onSettingsChanged(); };
        _menu.Items.Add(low);
        _menu.Items.Add(full);
        if (_onTestNotification != null)
            _menu.Items.Add(Loc.T("menu.testNotif"), null, (_, _) => _onTestNotification());

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Loc.T("menu.exit"), null, (_, _) => _onExit());
    }

    Entry EnsureEntry(string id)
    {
        if (!_icons.TryGetValue(id, out var e))
        {
            e = new Entry { Tray = new NotifyIcon { ContextMenuStrip = _menu } };
            // clic simple : flyout ; double-clic : paramètres (le second relâchement du bouton ne doit pas rouvrir le flyout)
            e.Tray.MouseUp += (_, a) => { if (a.Button == MouseButtons.Left && _clicks.AllowSingle(DateTime.UtcNow)) IconClicked?.Invoke(); };
            e.Tray.MouseDoubleClick += (_, a) =>
            {
                if (a.Button != MouseButtons.Left) return;
                _clicks.MarkDouble(DateTime.UtcNow);
                IconDoubleClicked?.Invoke();
            };
            _icons[id] = e;
        }
        return e;
    }

    /// <summary>
    /// Crée à l'avance (invisibles) les icônes des appareils déjà connus, dans un ordre fixe. Windows retient quelles
    /// icônes sont épinglées d'après l'ORDRE DE CRÉATION des icônes d'un programme : sans cela, masquer un appareil
    /// ou le voir arriver en retard décalait les autres et en renvoyait dans le menu ^.
    /// </summary>
    public void Reserve(IEnumerable<string> idsInStableOrder)
    {
        foreach (string id in idsInStableOrder) EnsureEntry(id);
    }

    public void Update(IReadOnlyList<BatteryReading> readings)
    {
        // un appareil absent n'a plus d'icône visible, mais l'objet est gardé : s'il revient, il retrouve la MÊME identité
        // (et donc son épinglage) au lieu d'en recevoir une nouvelle
        var present = readings.Select(r => r.Id).ToHashSet();
        foreach (var (id, entry) in _icons.Where(kv => !present.Contains(kv.Key)).ToList())
        {
            entry.Tray.Visible = false;
            entry.Tray.Icon = null;
            entry.Icon?.Dispose();
            entry.Icon = null;
        }

        bool light = Theme.TaskbarIsLight();
        foreach (var r in readings.OrderBy(r => r.Id))
        {
            var e = EnsureEntry(r.Id);
            var old = e.Icon;
            e.Icon = Draw(r.Kind, r.Percent, light, r.Offline, r.IsPlugged);
            e.Tray.Icon = e.Icon;
            e.Tray.Text = Tooltip(r);
            e.Tray.Visible = true;
            old?.Dispose();
        }
    }

    /// <summary>Notification Windows rattachée à l'icône de l'appareil concerné.</summary>
    public void Notify(string deviceId, string title, string text, bool warning)
    {
        if (!_icons.TryGetValue(deviceId, out var e)) return;
        e.Tray.ShowBalloonTip(8000, Cut(title, 63), Cut(text, 250), warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>Notification de test rattachée à la première icône visible. Retourne faux s'il n'y en a aucune.</summary>
    public bool NotifyAny(string title, string text, bool warning)
    {
        foreach (var e in _icons.Values)
        {
            if (!e.Tray.Visible) continue;
            e.Tray.ShowBalloonTip(8000, Cut(title, 63), Cut(text, 250), warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            return true;
        }
        return false;
    }

    static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    static string Tooltip(BatteryReading r)
    {
        string level = r.Percent is int ? r.PercentText : Loc.T("state.unavailable");
        var parts = new List<string> { r.Name, level };
        if (r.PercentEstimated) parts.Add(Loc.T("state.estimated"));
        if (r.Offline) parts.Add(Loc.T("state.offline"));
        else
        {
            string charge = Loc.ChargeText(r.Charge);
            if (charge.Length > 0)
            {
                parts.Add(charge);
                if (Loc.ChargeEtaText(r) is { } eta) parts.Add(eta);
            }
            else
            {
                if (Loc.PartsText(r) is { } batteries) parts.Add(batteries);
                if (Loc.AutonomyText(r) is { } a) parts.Add(a);
            }
            if (r.Detail != null) parts.Add(r.Detail);
        }
        parts.Add(Loc.T("time.read", r.At.ToString("HH:mm")));
        return Cut(string.Join(" · ", parts), 127); // limite Windows pour l'infobulle
    }

    /// <summary>Aperçu (PNG) des icônes en grand et à la taille réelle de la barre, sur fond sombre et clair.</summary>
    public void SavePreview(string path)
    {
        var samples = new (DeviceKind, int?)[]
        {
            (DeviceKind.Headset, 75), (DeviceKind.Mouse, 100), (DeviceKind.Keyboard, 62),
            (DeviceKind.Gamepad, 30), (DeviceKind.Webcam, 12), (DeviceKind.Microphone, 88), (DeviceKind.Mouse, 64),
        };
        using var sheet = new Bitmap(samples.Length * 150, 320);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int row = 0; row < 2; row++)
        {
            bool light = row == 1;
            using var bg = new SolidBrush(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
            g.FillRectangle(bg, 0, row * 160, sheet.Width, 160);
            for (int i = 0; i < samples.Length; i++)
            {
                // le dernier exemple est en charge (vert)
                using var icon = Render(samples[i].Item1, samples[i].Item2, light, charging: i == samples.Length - 1);
                g.DrawImage(icon, i * 150 + 10, row * 160 + 6, 100, 100);   // grand
                g.DrawImage(icon, i * 150 + 112, row * 160 + 20, 24, 24);   // taille réelle ~150 %
                g.DrawImage(icon, i * 150 + 112, row * 160 + 60, 16, 16);   // taille réelle 100 %
            }
        }
        sheet.Save(path);
    }

    static Icon Draw(DeviceKind kind, int? percent, bool lightTaskbar, bool offline = false, bool charging = false)
    {
        using var bmp = Render(kind, percent, lightTaskbar, offline, charging);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    static Bitmap Render(DeviceKind kind, int? percent, bool lightTaskbar, bool offline = false, bool charging = false)
    {
        const int S = 64; // dessiné en grand, Windows réduit à la taille de la barre
        var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color fg = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.White;
            if (offline) fg = Color.FromArgb(125, 125, 125); // hors ligne : tout en gris, dernière valeur connue
            Glyphs.Draw(g, kind, fg, 0, 0, 1f);

            // en charge : chiffre vert (prioritaire sur orange/rouge : l'appareil se recharge)
            Color textColor = offline ? fg : charging ? Theme.ChargingColor(lightTaskbar) : Theme.LevelColor(percent, fg);
            string text = percent is int v ? v.ToString() : "?";
            float size = text.Length >= 3 ? 27f : 34f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var br = new SolidBrush(textColor);
            g.DrawString(text, font, br, new RectangleF(0, 34, S, 30), fmt);
        }
        return bmp;
    }

    static void Remove(Entry e)
    {
        e.Tray.Visible = false;
        e.Tray.Dispose();
        e.Icon?.Dispose();
    }

    public void Dispose()
    {
        foreach (var e in _icons.Values) Remove(e);
        _icons.Clear();
        _menu.Dispose();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

/// <summary>Thème Windows (clair/sombre) et couleurs de niveau partagées entre l'icône et le flyout.</summary>
static class Theme
{
    public static bool TaskbarIsLight() => ReadLight("SystemUsesLightTheme");
    public static bool AppsAreLight() => ReadLight("AppsUseLightTheme");

    static bool ReadLight(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Vert de charge : plus clair sur fond sombre, plus foncé sur fond clair (lisibilité).</summary>
    public static Color ChargingColor(bool lightBackground) =>
        lightBackground ? Color.FromArgb(22, 148, 70) : Color.FromArgb(76, 204, 112);

    /// <summary>Couleur du chiffre : couleur du thème, orange puis rouge quand la batterie baisse.</summary>
    public static Color LevelColor(int? percent, Color normal) => percent switch
    {
        int p when p <= TrayManager.RedBelow => Color.FromArgb(255, 99, 99),
        int p when p < TrayManager.OrangeBelow => Color.FromArgb(255, 170, 40),
        _ => normal,
    };
}
