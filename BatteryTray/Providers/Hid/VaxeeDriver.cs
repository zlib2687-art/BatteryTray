// Récepteurs VAXEE 4K et 8K et câble USB ZYGEN NP-01S V2 (VID 3057 : récepteur 1002, récepteur 8K 2002, câble 1011).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/vaxee.cpp, porté en C# — voir NOTICE.md.
// Référence du protocole : stuffz/mouse-battery-monitor (docs/VAXEE.md). Seules les lectures de batterie (0B) et de charge (10) sont envoyées ; jamais d'écriture de configuration.
//   SetFeature [0E][A5][cmd][01 = LECTURE (02 = écriture)][01]… puis GetFeature 0E : [0E][A5][cmd][..][01][valeur] ; batterie = valeur ≤ 20 par pas de 5 %.
namespace BatteryTray.Providers.Hid;

public static class VaxeeDriver
{
    const ushort Vid = 0x3057, Receiver = 0x1002, Receiver8K = 0x2002, Cable = 0x1011;

    public static bool Supported(ushort pid) => pid is Receiver or Receiver8K or Cable;

    public static byte[] BuildQuery(byte command) { var r = new byte[64]; r[0] = 0x0E; r[1] = 0xA5; r[2] = command; r[3] = 0x01; r[4] = 0x01; return r; }

    /// <summary>Valeur d'une réponse à <paramref name="command"/>, ou null.</summary>
    public static byte? ParseReply(byte[] r, byte command) => r.Length < 6 || r[0] != 0x0E || r[1] != 0xA5 || r[2] != command || r[4] != 1 ? null : r[5];

    public static string ModelName(ushort pid) => pid == Cable ? "ZYGEN NP-01S V2 Wireless" : pid == Receiver8K ? "VAXEE 8K Wireless" : "VAXEE wireless mouse";

    static byte? Query(HidDev d, byte command)
    {
        if (!d.SetFeature(BuildQuery(command))) return null;
        Thread.Sleep(100);
        return d.GetFeature(0x0E, out var r) ? ParseReply(r, command) : null;
    }

    // Termine aussitôt le délai de grâce de l'interface quand l'appareil USB physique disparaît (y compris en déplaçant le câble du récepteur à la souris).
    static readonly Dictionary<string, DeviceStatus> LastGood = new();

    public static void Poll(PollContext ctx)
    {
        foreach (var key in LastGood.Keys.ToList())
        {
            if (ctx.Colls.Any(c => c.Path == key)) continue;
            var gone = LastGood[key];
            gone.Connected = false; gone.Percent = -1; gone.Charging = false;
            ctx.Out.Add(gone);
            LastGood.Remove(key);
        }
        foreach (var (_, pid) in ctx.Products(Vid))
        {
            if (!Supported(pid)) continue;
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFF05 && c.Usage == 1 && c.FeatLen == 64))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                byte? battery = null;
                for (int attempt = 0; attempt < 2 && battery == null; attempt++)
                    if (Query(d, 0x0B) is { } b && b <= 20) battery = b;
                if (battery == null) continue; // l'interface garde la dernière lecture estompée pendant le délai de grâce
                var s = new DeviceStatus { Key = ctx.StableKey("vaxee:" + HidUtil.Hex4(pid)) };
                // Une entrée « disparue » ajoutée plus haut pour la même souris sur son ancien port est remplacée, pas dupliquée.
                ctx.Out.RemoveAll(o => o.Key == s.Key && !o.Connected);
                s.Name = ModelName(pid); s.Kind = DeviceKind.Mouse; s.Via = pid == Cable ? "USB cable" : "2.4 GHz receiver"; s.Percent = battery.Value * 5;
                if (Query(d, 0x10) is { } chg) s.Charging = chg != 0;
                LastGood[c.Path] = s;
                ctx.Add(s, Vid, pid);
                break;
            }
        }
    }
}
