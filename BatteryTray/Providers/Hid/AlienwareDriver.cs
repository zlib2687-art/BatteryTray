// Casque Alienware AW920H tri-mode sur son récepteur 2,4 GHz (413C:A515).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/alienware.cpp, porté en C# — voir NOTICE.md.
// Protocole d'Alienware Command Center (méthodes GetRFBatteryStatus / IsChecksumOk du module AW920H) :
//  * Collection : interface 5, page FF13:0001 ; rapport de sortie 06 / d'entrée 07, 62 octets.
//  * Requête : SetOutputReport [06][C0][0A][00…].
//  * Réponse (GetInputReport 07, relue toutes les ~15 ms) : [07][C0][0A][09][00][liaison][%][en charge][critique][..][..][% critique][mode][somme]
//    [13] = XOR des octets 3..12 ; Command Center rejette aussi une réponse dont [6] vaut 0. [7] = 1 en charge. [8] = 1 si la batterie est
//    critique : le niveau est alors lu en [11]. [07][00][80] = « pas encore de réponse » (le casque éteint répond toujours 00 80).
//    Exemple : 07 C0 0A 09 00 01 5F 00 00 1C 10 5F 02 06 = 95 %, pas en charge.
//  * Environ une requête sur cent est perdue : on la renvoie. En mode Bluetooth (A514), Windows garde lui-même le niveau.
// Un casque qui ne répond pas est laissé de côté (le récepteur reste pris en charge pour que les pilotes génériques ne le reprennent pas).
namespace BatteryTray.Providers.Hid;

public static class AlienwareDriver
{
    const ushort Vendor = 0x413C, Aw920hDongle = 0xA515;

    public static byte[] BuildRequest() => new byte[] { 0x06, 0xC0, 0x0A };

    public static bool IsVendorCollection(HidColl c) => c.Iface == 5 && c.Page == 0xFF13 && c.Usage == 0x0001 && c.InLen == 62 && c.OutLen == 62;

    /// <summary>Décode une réponse comme Command Center ; null si ce n'en est pas une valide.</summary>
    public static (int Percent, bool Charging)? DecodeBattery(byte[] r)
    {
        if (r.Length < 14 || r[0] != 0x07 || r[1] != 0xC0 || r[2] != 0x0A || r[3] != 0x09) return null;
        byte x = 0;
        for (int i = 3; i <= 12; i++) x ^= r[i];
        if (x != r[13] || r[6] == 0) return null;
        int pct = r[8] == 1 ? r[11] : r[6];
        return pct > 100 ? null : (pct, r[7] == 1);
    }

    static (int Percent, bool Charging)? Query(HidDev d)
    {
        long deadline = Environment.TickCount64 + 600;
        for (int attempt = 0; attempt < 3 && Environment.TickCount64 < deadline; attempt++)
        {
            if (!d.SetOutputReport(BuildRequest())) return null;
            long attemptEnd = Math.Min(deadline, Environment.TickCount64 + 180); // une réponse normale prend 30-100 ms
            do
            {
                Thread.Sleep(15);
                if (d.GetInputReport(0x07, out var r) && DecodeBattery(r) is { } b) return b;
            } while (Environment.TickCount64 < attemptEnd);
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vendor, Aw920hDongle)) return;
        foreach (var c in ctx.Find(Vendor, Aw920hDongle, IsVendorCollection))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            var b = Query(d);
            if (b == null) { ctx.Claim(Vendor, Aw920hDongle); break; } // casque éteint, hors de portée ou requêtes perdues
            ctx.Add(new DeviceStatus
            {
                Key = "alienware:" + HidUtil.Hex4(Aw920hDongle), Name = "Alienware AW920H", Kind = DeviceKind.Headset,
                Via = "2.4 GHz dongle", Percent = b.Value.Percent, Charging = b.Value.Charging,
            }, Vendor, Aw920hDongle);
            break;
        }
    }
}
