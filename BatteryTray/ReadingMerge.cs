using System.Text;

namespace BatteryTray;

/// <summary>
/// Plusieurs sources peuvent voir le même appareil (ex. un casque Bluetooth vu par le service Bluetooth ET par le
/// balayage des périphériques de Windows). On garde la lecture de la source la plus spécifique, listée en premier,
/// et on écarte les doublons. Deux appareils de même nom lus par la MÊME source (deux souris identiques) restent distincts.
/// Logique pure, testée.
/// </summary>
public static class ReadingMerge
{
    /// <summary>Source d'une lecture = début de son identifiant (« logi », « razer », « ble », « bt », « pnp », « win »…).</summary>
    public static string SourceOf(string id)
    {
        int dash = id.IndexOf('-');
        return dash > 0 ? id[..dash] : id;
    }

    /// <summary>Nom réduit à ses lettres et chiffres, en minuscules : « Logi MX-Keys » et « logi mx keys » sont identiques.</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    static bool SameDevice(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        // « g502lightspeed » et « g502lightspeedwirelessgamingmouse » : l'un contient l'autre (au moins 6 caractères,
        // pour qu'un nom très court comme « mouse » ne fusionne pas des appareils différents)
        string shorter = a.Length <= b.Length ? a : b, longer = a.Length <= b.Length ? b : a;
        return shorter.Length >= 6 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nettoie une lecture venue d'un périphérique ou d'un pilote, en qui on n'a pas à avoir confiance : identifiant absent (rejetée),
    /// pourcentage hors 0–100 (valeur « inconnue » renvoyée par certains appareils, ex. 255 : devient « pas de niveau », jamais
    /// inventé), nom absent, trop long ou contenant des caractères de contrôle ou d'inversion de texte. Logique pure, testée.
    /// </summary>
    /// <summary>Caractères d'inversion du sens du texte (U+202A–202E, U+2066–2069) : servent à tromper l'affichage.</summary>
    static bool IsBidiControl(char c) => c is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069;

    public static BatteryReading? Sanitize(BatteryReading r)
    {
        if (r == null || string.IsNullOrWhiteSpace(r.Id)) return null;
        var name = new string((r.Name ?? "").Where(c => !char.IsControl(c) && !IsBidiControl(c)).ToArray()).Trim();
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (name.Length == 0) name = "?";
        int? percent = r.Percent is int p && p is >= 0 and <= 100 ? p : null;
        var parts = r.Parts?.Where(x => x != null).Select(x => x with { Percent = Math.Clamp(x.Percent, 0, 100) }).ToList();
        return r with { Name = name, Percent = percent, Parts = parts };
    }

    public static List<BatteryReading> Deduplicate(IEnumerable<BatteryReading> readingsInPriorityOrder)
    {
        var kept = new List<(string Name, string Source, BatteryReading Reading)>();
        foreach (var raw in readingsInPriorityOrder)
        {
            if (Sanitize(raw) is not { } r) continue;
            string name = Normalize(r.Name), source = SourceOf(r.Id);
            bool duplicate = kept.Any(k => k.Source != source && SameDevice(k.Name, name));
            if (!duplicate) kept.Add((name, source, r));
        }
        return kept.Select(k => k.Reading).ToList();
    }
}
