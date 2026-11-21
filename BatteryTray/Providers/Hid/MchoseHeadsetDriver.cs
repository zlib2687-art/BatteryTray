// Casques sans fil MCHOSE au protocole « MC » (VID 3837). Prend en charge le MCHOSE V9 Pro 2 (récepteur 2,4 GHz 3837:6011) sur la collection vendeur FF07:0222 (identifiant de rapport 0x55, rapports d'entrée / sortie de 63 octets).
// Le MCHOSE R9 (3837:6013) utilise le même protocole dans M HUB mais n'est pas activé ici, faute de test.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose_headset.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote M HUB de MCHOSE (paquet customPage de cdn.mchose.com.cn : buildMCFrame, createReadBatteryCommand, parseBatteryResponse ; MC_HEADSET_REPORT_ID = 85), vérifié sur un V9 Pro 2 :
//   trame (après l'identifiant) : [0] AA début, [1] 01 version, [2] drapeaux (1 = somme XOR), [3] longueur (sous-commande + corps), [4..5] type de message (00 01 requête/réponse, 00 04 notification), [6] sous-commande,
//   [7..] corps, puis un octet de somme (0 quand les drapeaux valent 0). Lecture batterie : [55][AA 01 00 01 00 01 0B 00…] (lecture seule). La réponse, et la notification de batterie propre du casque (sous-commande 0x22),
//   portent corps[0] = 0 quand le casque est éteint ou endormi, sinon corps[1] = en charge (bit 7) | pourcentage (bits 0..6).
namespace BatteryTray.Providers.Hid;

public static class MchoseHeadsetDriver
{
    const ushort Vid = 0x3837, V9Pro2Pid = 0x6011;
    const byte ReportId = 0x55, Start = 0xAA, Version = 0x01, ReadBattery = 0x0B, NotifyBattery = 0x22;

    public enum Reply { None, Off, Level }

    public static byte[] BuildRequest() => new byte[] { ReportId, Start, Version, 0x00, 0x01, 0x00, 0x01, ReadBattery, 0x00 };

    /// <summary>Analyse un rapport d'entrée (r[0] = identifiant) : réponse batterie ou notification de batterie.</summary>
    public static (Reply Result, int Percent, bool Charging) ParseBattery(byte[] r)
    {
        var none = (Reply.None, -1, false);
        if (r.Length < 9 || r[0] != ReportId) return none;
        var f = r.AsSpan(1); int n = r.Length - 1;
        if (f[0] != Start || f[1] != Version || f[4] != 0x00) return none;
        bool reply = f[5] == 0x01 && f[6] == ReadBattery, notify = f[5] == 0x04 && f[6] == NotifyBattery;
        if (!reply && !notify) return none;
        int len = f[3]; // sous-commande + corps
        if (len < 3 || 6 + len >= n) return none;
        byte check = 0;
        if (f[2] == 1) { check = (byte)(f[2] ^ f[3] ^ f[4] ^ f[5]); for (int i = 0; i < len; i++) check ^= f[6 + i]; }
        if (f[6 + len] != check) return none;
        var body = f[7..];
        if (body[0] == 0) return (Reply.Off, -1, false);
        int pct = body[1] & 0x7F;
        return pct <= 100 ? (Reply.Level, pct, (body[1] & 0x80) != 0) : none;
    }

    static (Reply, int, bool) Query(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return (Reply.None, -1, false);
        d.Flush();
        if (!d.Write(BuildRequest())) return (Reply.None, -1, false);
        long end = Environment.TickCount64 + 1000;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            var got = ParseBattery(r);
            if (got.Result != Reply.None) return got;
        }
        return (Reply.None, -1, false);
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, V9Pro2Pid)) return;
        foreach (var c in ctx.Find(Vid, V9Pro2Pid, c => c.Page == 0xFF07 && c.Usage == 0x0222 && c.OutLen >= 64 && c.InLen >= 64))
        {
            var (got, pct, chg) = Query(c);
            if (got == Reply.None) continue;
            var s = new DeviceStatus { Key = "mchose:v9pro2", Name = c.Product.Length > 0 ? c.Product : "MCHOSE V9 Pro 2", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
            if (got == Reply.Off) s.Connected = false; // le récepteur a répondu, le casque est éteint ou endormi : on garde la dernière valeur
            else { s.Percent = pct; s.Charging = chg; }
            ctx.Add(s, Vid, V9Pro2Pid);
            break;
        }
    }
}
