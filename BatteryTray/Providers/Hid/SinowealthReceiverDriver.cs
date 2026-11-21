// Récepteur Sinowealth (258A:002F) : souris sans fil (Redragon M690 Pro, M602-KS…).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/sinowealth_receiver.cpp, porté en C# — voir NOTICE.md.
// Référence du protocole : OpenMouse-Project/mouse-protocol, docs/redragon-m690-pro-testing.md. Courts rapports d'état de 8 octets (rapport de fonctionnalité 5), documentés pour le M690 Pro et
// vérifiés sur un M602-KS. L'identifiant USB seul est partagé par d'autres récepteurs : la forme de la collection est exigée.
//   SetFeature [05][cmd]… puis GetFeature 5 : [05][cmd][…] ; cmd 80 = liaison ([2] = 1 : liée) ; cmd 90 = batterie ([2] = 0x11, [3] = % ≤ 100).
namespace BatteryTray.Providers.Hid;

public static class SinowealthReceiverDriver
{
    const ushort Vid = 0x258A, Pid = 0x002F;

    public static byte[] BuildQuery(byte cmd) { var r = new byte[8]; r[0] = 5; r[1] = cmd; return r; }

    public static bool IsReply(byte[] r, byte cmd) => r.Length == 8 && r[0] == 5 && r[1] == cmd;
    public static bool IsLinked(byte[] link) => link.Length == 8 && link[2] == 1;
    public static int? ParseBattery(byte[] b) => b.Length == 8 && b[2] == 0x11 && b[3] <= 100 ? b[3] : null;

    static byte[]? Query(HidDev d, byte cmd)
    {
        if (!d.SetFeature(BuildQuery(cmd))) return null;
        Thread.Sleep(100);
        return d.GetFeature(5, out var r) && IsReply(r, cmd) ? r : null;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Iface == 1 && c.Page == 0xFF00 && c.Usage == 1 && c.FeatLen == 8 && c.InLen == 0 && c.OutLen == 0))
        {
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(c)) continue;
            if (Query(d, 0x80) is not { } link || !IsLinked(link) || Query(d, 0x90) is not { } bat || ParseBattery(bat) is not { } pct) continue;
            ctx.Add(new DeviceStatus { Key = "sinowealth:258A:002F", Name = "Sinowealth wireless mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct }, c.Vid, c.Pid);
            break;
        }
    }
}
