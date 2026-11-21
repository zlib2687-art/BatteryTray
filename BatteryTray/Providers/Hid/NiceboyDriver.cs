// Souris sans fil Niceboy MW400 sur son « Wireless Receiver » 2,4 GHz (248A:FA02).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/niceboy.cpp, porté en C# — voir NOTICE.md.
// Protocole de l'application de configuration Niceboy MW400 (WillGetBatteryThread, slot RefreshBatteryStatus), de sa sortie de débogage « Threadread bat » et de réponses lues sur un MW400 :
//  * Collection : interface 0, collection vendeur du rapport 05 (usage 0001:0000, 33 octets en entrée et en sortie).
//  * Requête (interruption OUT) : [05][24][FA][48][00…], envoyée par l'application environ toutes les 27 s.
//  * Réponse (rapport d'entrée 05) : [05][xx][FA][48][lo][hi][charge][…] ; l'application n'accepte que [3] = 48. brut = lo | hi << 8, millivolts = brut × 990 / 240. lo, hi et charge à 0 : souris non liée.
//  * Pourcentage : table de l'application par paliers de 10 % (≥ 4200 mV 100 %, 4100 90 %, 4000 80 %, 3900 70 %, 3800 60 %, 3700 50 %, 3500 40 %, 3400 30 %, 3200 20 %, sinon 10 %).
//    Exemple : 05 49 FA 48 EA 03 00 = brut 1002 = 4133 mV = 90 %, pas en charge.
// L'identifiant fabricant 248A est partagé par d'autres récepteurs Telink (Machenike, Delux, Darmoshark, Ajazz) : seul cet identifiant de produit exact est retenu.
namespace BatteryTray.Providers.Hid;

public static class NiceboyDriver
{
    const ushort Vendor = 0x248A, Mw400Receiver = 0xFA02;
    const byte Report = 0x05;

    public static bool IsVendorCollection(HidColl c) => c.Iface == 0 && c.Page == 0x0001 && c.Usage == 0x0000 && c.InLen == 33 && c.OutLen == 33;

    static readonly int[] Mv = { 4200, 4100, 4000, 3900, 3800, 3700, 3500, 3400, 3200 };
    static readonly int[] Pct = { 100, 90, 80, 70, 60, 50, 40, 30, 20 };

    public static int PercentFromMillivolts(int mv)
    {
        for (int i = 0; i < Mv.Length; i++) if (mv >= Mv[i]) return Pct[i];
        return 10;
    }

    public static bool IsBatteryReply(byte[] r) => r.Length >= 7 && r[0] == Report && r[2] == 0xFA && r[3] == 0x48;

    /// <summary>Réponse : null si la souris est non liée (lo, hi et charge à 0), sinon (millivolts, pourcentage, en charge).</summary>
    public static (int Millivolts, int Percent, bool Charging)? Parse(byte[] r)
    {
        if ((r[4] | r[5] | r[6]) == 0) return null;
        int mv = (r[4] | (r[5] << 8)) * 990 / 240;
        return (mv, PercentFromMillivolts(mv), r[6] != 0);
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vendor, Mw400Receiver)) return;
        foreach (var c in ctx.Find(Vendor, Mw400Receiver, IsVendorCollection))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(Report, 0x24, 0xFA, 0x48)) continue;
            byte[]? r = null;
            long end = Environment.TickCount64 + 500; // une seule échéance absolue, les autres rapports 05 sont ignorés
            for (; ; )
            {
                long left = end - Environment.TickCount64;
                if (left <= 0) break;
                var x = d.Read((int)left);
                if (x == null) break;
                if (IsBatteryReply(x)) { r = x; break; }
            }
            if (r == null) continue;
            var s = new DeviceStatus { Key = "niceboy:" + HidUtil.Hex4(Mw400Receiver), Name = "Niceboy MW400", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
            if (Parse(r) is { } p) { s.Percent = p.Percent; s.Charging = p.Charging; }
            else s.Connected = false; // récepteur répond, souris en veille ou éteinte : le dernier niveau est conservé
            ctx.Add(s, Vendor, Mw400Receiver);
            break;
        }
    }
}
