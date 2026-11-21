// Liaison avec le téléphone de Microsoft Phone Link : Phone Link stocke dans le dossier de son paquet une carte adaptative localisée (StartMenuCompanion.json) contenant l'état de connexion du téléphone et sa batterie.
// Prend en charge les messages de connexion et les infobulles de batterie en anglais et en japonais. Lit les champs localisés dans la disposition JSON de la carte ; rejette les lectures malformées ou ambiguës.
// Le fichier est un détail d'implémentation non documenté de Phone Link. Lecture seule, sans accès à un appareil ni au réseau.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/phonelink.cpp et phonelink_protocol.h, porté en C# — voir NOTICE.md.
using System.Text.Json;

namespace BatteryTray.Providers.Hid;

public static class PhoneLinkDriver
{
    const long StaleSec = 3 * 60 * 60; // une carte non réécrite depuis ce temps est affichée estompée, comme dernière valeur connue
    const int MaxBytes = 4 * 1024 * 1024;

    public sealed record Reading(string Name, int Percent, bool Charging);

    static readonly string[] Suffixes = { " - Connected to your PC", " - PC に接続しました" };
    const string JapaneseSuffix = " - PC に接続しました", JapaneseTooltip = "バッテリー 残量 ", EnglishTooltip = "Battery ";

    public static string? ConnectedPhoneName(string speak)
    {
        foreach (var suffix in Suffixes)
            if (speak.Length > suffix.Length && speak.EndsWith(suffix, StringComparison.Ordinal)) return speak[..^suffix.Length];
        return null;
    }

    /// <summary>Niveau d'une infobulle (« Battery 82% remaining[, charging] » ou « バッテリー 残量 82% ») ; -1 si le format n'est pas exactement celui-là.</summary>
    public static int BatteryFromTooltip(string tooltip, out bool charging)
    {
        charging = false;
        bool en = tooltip.StartsWith(EnglishTooltip, StringComparison.Ordinal), ja = tooltip.StartsWith(JapaneseTooltip, StringComparison.Ordinal);
        if (!en && !ja) return -1;
        int start = en ? EnglishTooltip.Length : JapaneseTooltip.Length, end = start;
        while (end < tooltip.Length && tooltip[end] is >= '0' and <= '9' && end - start < 3) end++;
        if (end == start || end >= tooltip.Length || tooltip[end] != '%') return -1;
        int level = int.Parse(tooltip.AsSpan(start, end - start));
        if (level > 100) return -1;
        string remainder = tooltip[(end + 1)..];
        if (en)
        {
            if (remainder != " remaining" && remainder != " remaining, charging") return -1;
            charging = remainder == " remaining, charging";
        }
        else if (remainder.Length != 0) return -1; // état de charge non établi pour les cartes japonaises
        return level;
    }

    static string? Field(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Une clé en double rend la carte ambiguë : rejetée, comme dans l'analyseur d'origine.
    static bool HasDuplicateKeys(JsonElement e, int depth = 0)
    {
        if (depth > 64) return true;
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) if (!seen.Add(p.Name) || HasDuplicateKeys(p.Value, depth + 1)) return true;
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var c in e.EnumerateArray()) if (HasDuplicateKeys(c, depth + 1)) return true;
        return false;
    }

    // On ne parcourt que les champs de disposition de la carte, jamais les actions, métadonnées arbitraires ni charges de notification.
    static void Battery(JsonElement value, bool japanese, ref int percent, ref bool charging, ref bool ambiguous)
    {
        if (value.ValueKind == JsonValueKind.Array) { foreach (var child in value.EnumerateArray()) Battery(child, japanese, ref percent, ref charging, ref ambiguous); return; }
        if (value.ValueKind != JsonValueKind.Object) return;
        string? type = Field(value, "type");
        if (type != null && type is not ("AdaptiveCard" or "Container" or "ColumnSet" or "Column" or "TextBlock" or "Image")) return;
        if (Field(value, "tooltip") is { } tooltip)
        {
            bool ja = tooltip.StartsWith(JapaneseTooltip, StringComparison.Ordinal);
            int n = ja == japanese ? BatteryFromTooltip(tooltip, out bool chg) : -1;
            chg = n >= 0 && ja == japanese && BatteryFromTooltip(tooltip, out bool c2) >= 0 && c2;
            string? text = Field(value, "text");
            if (n >= 0 && (text == null || text == n + "%"))
            {
                if (percent >= 0 && (percent != n || charging != chg)) ambiguous = true;
                percent = n; charging = chg;
            }
        }
        foreach (var field in new[] { "body", "items", "columns" })
            if (value.TryGetProperty(field, out var child)) Battery(child, japanese, ref percent, ref charging, ref ambiguous);
    }

    public static Reading? Parse(string json)
    {
        if (json.Length > MaxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateKeys(root)) return null;
            if (Field(root, "speak") is not { } speak || ConnectedPhoneName(speak) is not { Length: > 0 } name) return null;
            bool ja = speak.EndsWith(JapaneseSuffix, StringComparison.Ordinal);
            int percent = -1; bool charging = false, ambiguous = false;
            Battery(root, ja, ref percent, ref charging, ref ambiguous);
            return ambiguous || percent < 0 ? null : new Reading(name, percent, charging);
        }
        catch (JsonException) { return null; }
    }

    public static void Poll(PollContext ctx)
    {
        string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local)) return;
        string path = Path.Combine(local, "Packages", "Microsoft.YourPhone_8wekyb3d8bbwe", "LocalState", "StartMenu", "StartMenuCompanion.json");
        string json; DateTime written;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or >= MaxBytes) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            json = sr.ReadToEnd(); written = info.LastWriteTimeUtc;
        }
        catch { return; }
        if (Parse(json) is not { } r) return;
        long ageSec = (long)Math.Max(0, (DateTime.UtcNow - written).TotalSeconds);
        ctx.Out.Add(new DeviceStatus { Key = "phonelink", Name = r.Name, Kind = DeviceKind.Other, Via = "Phone Link", Percent = r.Percent, Charging = r.Charging, Connected = ageSec <= StaleSec });
    }
}
