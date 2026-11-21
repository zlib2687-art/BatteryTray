// Press Play ICARUS V2 Nearlink sur son récepteur 2,4 GHz (7768:6434).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/pressplay_icarus.cpp, porté en C# — voir NOTICE.md.
// Protocole observé sur le hub web de Press Play (hub.pressplayid.com) dans des captures USBPcap de cet appareil à 100 % et 90 % de batterie. Sur la collection vendeur FFB2:0001 (entrée / sortie de 64 octets),
// on envoie le rapport de sortie 06 05 suivi de zéros. Le récepteur répond par le rapport d'entrée [0] = 06, [1] = 05, [2] = 26 ; [13] est le pourcentage (0x64 à 100 %, 0x5A à 90 %).
// Les autres commandes de configuration observées ne sont volontairement pas envoyées.
namespace BatteryTray.Providers.Hid;

public static class PressPlayDriver
{
    const ushort Vid = 0x7768, Pid = 0x6434;
    const int ReportLength = 64, PercentOffset = 13;

    public static byte[] BuildRequest(int outLen) { var r = new byte[Math.Max(outLen, 2)]; r[0] = 0x06; r[1] = 0x05; return r; }

    public static int? ParseReply(byte[] r) => r.Length < PercentOffset + 1 || r[0] != 0x06 || r[1] != 0x05 || r[2] != 0x26 || r[PercentOffset] > 100 ? null : r[PercentOffset];

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Page == 0xFFB2 && c.Usage == 0x0001 && c.InLen == ReportLength && c.OutLen == ReportLength))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(BuildRequest(c.OutLen), 500)) continue;
            var r = d.Read(500);
            if (r == null || ParseReply(r) is not { } pct) continue;
            ctx.Add(new DeviceStatus { Key = "pressplay:icarus-v2", Name = "Press Play ICARUS V2 Nearlink", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = pct }, Vid, Pid);
            break;
        }
    }
}
