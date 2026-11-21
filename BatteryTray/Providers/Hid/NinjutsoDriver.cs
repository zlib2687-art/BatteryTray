// Souris Ninjutso :
//  * Sora V2, sur son récepteur 1K (1915:AE1C) ou son câble USB (1915:AE11) : NinjaForce (createSorav2.readState()). Rapport de fonctionnalité 05, commande 15 : [9] batterie (0 = 100 %), [10] en charge,
//    [12] en ligne. Les indices comptent l'identifiant de rapport, comme le DataView renvoyé par WebHID. Sur câble, la même requête répond [12] = 0 (testé sur un Sora V2) : la souris y compte comme connectée,
//    comme la Sprime PM1. La règle « 0 = 100 % » s'applique aussi sur câble ; une batterie pleine sur câble n'a pas encore été capturée.
//  * Ten / Ten Air (récepteur 093A:EB01, câble 093A:E020) : NinjaForce (ninjaforce.co) et DrStopor/ninjutso-battery-tray. Rapport de fonctionnalité 06 (16 octets) sur FF01:0001 ;
//    commandes 0x13 (en ligne), 0x12 (batterie %), 0x11 (en charge), valeur à l'octet 8. Vérifié sur un Ninjutso Ten Air (093A:EB01 et 093A:E020).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/ninjutso.cpp, porté en C# — voir NOTICE.md.
namespace BatteryTray.Providers.Hid;

public static class NinjutsoDriver
{
    public static byte[] BuildTenQuery(byte cmd) { var q = new byte[16]; q[0] = 0x06; q[1] = cmd; q[4] = 1; return q; }

    /// <summary>Réponse Ten : valeur de l'octet 8 si l'écho (rapport 06, même commande) est correct, sinon null.</summary>
    public static byte? ParseTen(byte[] r, byte cmd) => r.Length == 16 && r[0] == 0x06 && r[1] == cmd ? r[8] : null;

    public static byte[] BuildSoraQuery() { var q = new byte[32]; q[0] = 0x05; q[1] = 0x15; q[4] = 1; return q; }

    /// <summary>Réponse Sora V2 : (connectée, pourcentage ou -1, en charge) ; null si invalide. Sur câble la souris est connectée même avec [12] = 0.</summary>
    public static (bool Connected, int Percent, bool Charging)? ParseSora(byte[] r, bool cable)
    {
        if (r.Length < 13 || r[0] != 0x05 || r[1] != 0x15 || r[9] > 100) return null;
        bool connected = cable || r[12] != 0;
        return (connected, connected ? (r[9] == 0 ? 100 : r[9]) : -1, connected && r[10] != 0);
    }

    static byte? QueryTen(HidDev d, byte cmd)
    {
        if (!d.SetFeature(BuildTenQuery(cmd))) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(30);
            if (d.GetFeature(0x06, out var r) && ParseTen(r, cmd) is { } v) return v;
        }
        return null;
    }

    static void PollTen(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x093A))
        {
            if (pid != 0xEB01 && pid != 0xE020) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF01 && c.Usage == 1 && c.FeatLen == 16))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                if (QueryTen(d, 0x13) != 1 || QueryTen(d, 0x12) is not { } pct || pct > 100 || QueryTen(d, 0x11) is not { } chg) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "ninjutso-ten:" + HidUtil.Hex4(pid), Name = c.Product.Length > 0 ? c.Product : "Ninjutso Ten / Ten Air", Kind = DeviceKind.Mouse,
                    Via = pid == 0xE020 ? "USB cable" : "2.4 GHz receiver", Percent = pct, Charging = chg != 0,
                }, vid, pid);
                break;
            }
        }
    }

    public static void Poll(PollContext ctx)
    {
        const ushort vid = 0x1915, receiverPid = 0xAE1C, cablePid = 0xAE11;
        foreach (ushort pid in new[] { receiverPid, cablePid })
        {
            if (ctx.IsClaimed(vid, pid)) continue;
            bool cable = pid == cablePid;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFFA0 && c.Usage == 1 && c.FeatLen >= 32))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                (bool Connected, int Percent, bool Charging)? res = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(90);
                    if (!d.SetFeature(BuildSoraQuery())) break;
                    Thread.Sleep(15);
                    if (!d.GetFeature(0x05, out var r) || ParseSora(r, cable) is not { } p) continue;
                    res = p;
                    if (p.Connected) break;
                }
                if (res == null) continue;
                var s = new DeviceStatus
                {
                    Key = "ninjutso:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "Ninjutso Sora V2", Kind = DeviceKind.Mouse,
                    Via = cable ? "USB cable" : "2.4 GHz receiver", Connected = res.Value.Connected, Percent = res.Value.Percent, Charging = res.Value.Charging,
                };
                ctx.Add(s, vid, pid);
                break;
            }
        }
        PollTen(ctx);
    }
}
