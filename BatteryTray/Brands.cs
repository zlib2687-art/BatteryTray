using System.Text.RegularExpressions;

namespace BatteryTray;

/// <summary>
/// Reconnaissance de la marque d'un appareil d'après son nom, et couleur associée (réglage « Couleurs de marque »).
/// Les noms de modèles qui ne contiennent pas le nom de la marque (« G502 », « Arctis »…) sont reconnus par leur forme.
/// Chaque marque a une teinte pour barre/fond sombre et une plus foncée pour fond clair (lisibilité). Logique pure, testée.
/// </summary>
public static class Brands
{
    public sealed record Brand(string Key, string Display, Color OnDark, Color OnLight);

    static Brand B(string key, string display, int r, int g, int b) =>
        new(key, display, Color.FromArgb(r, g, b), Color.FromArgb((int)(r * 0.62), (int)(g * 0.62), (int)(b * 0.62)));

    // L'ordre compte : « Xbox Wireless Controller » doit être reconnu avant le « Wireless Controller » générique (nom Windows des manettes PlayStation).
    static readonly (Regex Pattern, Brand Brand)[] Rules =
    {
        (new(@"\brazer\b|deathadder|basilisk|viper|barracuda|blackshark|kraken", RegexOptions.IgnoreCase), B("razer", "Razer", 68, 214, 44)),
        (new(@"\blogitech\b|\bg\d{3}\b|\bg pro\b|\bpro x\b|\bmx (master|anywhere|keys|ergo|vertical)\b|\bg(535|733|935|435)\b|lightspeed|streamcam|\bg hub\b", RegexOptions.IgnoreCase), B("logitech", "Logitech", 0, 184, 252)),
        (new(@"steelseries|arctis|aerox|rival \d|prime wireless", RegexOptions.IgnoreCase), B("steelseries", "SteelSeries", 255, 112, 40)),
        (new(@"corsair|virtuoso|\bhs\d{2}\b|void (pro|elite|rgb)|dark core|sabre|scimitar|slipstream", RegexOptions.IgnoreCase), B("corsair", "Corsair", 255, 210, 0)),
        (new(@"hyperx|cloud (ii|iii|alpha|flight|stinger|core)|pulsefire", RegexOptions.IgnoreCase), B("hyperx", "HyperX", 228, 38, 60)),
        (new(@"\brog\b|asus|zowie|keris|harpe|gladius|chakram", RegexOptions.IgnoreCase), B("asus", "ASUS ROG", 255, 40, 60)),
        (new(@"xbox", RegexOptions.IgnoreCase), B("xbox", "Xbox", 16, 190, 16)),
        (new(@"playstation|dualsense|dualshock|wireless controller|\bps[345]\b", RegexOptions.IgnoreCase), B("playstation", "PlayStation", 0, 112, 209)),
        (new(@"samsung|galaxy buds", RegexOptions.IgnoreCase), B("samsung", "Samsung", 40, 90, 220)),
        (new(@"apple|airpods|magic (mouse|keyboard|trackpad)", RegexOptions.IgnoreCase), B("apple", "Apple", 162, 170, 173)),
        (new(@"jabra|evolve|elite \d", RegexOptions.IgnoreCase), B("jabra", "Jabra", 255, 205, 0)),
        (new(@"\bjbl\b", RegexOptions.IgnoreCase), B("jbl", "JBL", 255, 90, 20)),
        (new(@"cooler master|\bmm7\d\d\b", RegexOptions.IgnoreCase), B("coolermaster", "Cooler Master", 140, 90, 230)),
        (new(@"steam (controller|deck)", RegexOptions.IgnoreCase), B("steam", "Steam", 102, 192, 244)),
        (new(@"glorious|model [od]\b|model [od]-", RegexOptions.IgnoreCase), B("glorious", "Glorious", 255, 170, 0)),
        (new(@"keychron", RegexOptions.IgnoreCase), B("keychron", "Keychron", 255, 150, 0)),
    };

    /// <summary>Marque reconnue d'après le nom de l'appareil, ou null.</summary>
    public static Brand? Detect(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var (pattern, brand) in Rules)
            if (pattern.IsMatch(name)) return brand;
        return null;
    }

    /// <summary>Couleur de la marque pour un fond clair ou sombre, ou null si la marque n'est pas reconnue.</summary>
    public static Color? ColorFor(string? name, bool lightBackground) =>
        Detect(name) is { } b ? (lightBackground ? b.OnLight : b.OnDark) : null;
}
