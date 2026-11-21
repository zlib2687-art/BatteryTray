// Souris sans fil MCHOSE G3 V2 (VID 3837) : récepteur 2,4 GHz 3837:4245 et câble USB 3837:418C, sur la collection vendeur FF01:0010 (rapports d'entrée / sortie de 65 octets).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose_g3.cpp, porté en C# — voir NOTICE.md.
// Protocole de Jstdi001/MCHOSE-Monitor (MIT), vérifié sur un MCHOSE G3 V2 : rapport de sortie (identifiant 0, charge de 64 octets complétée de zéros) [00][55 30 A5 0B 2E 01 01 01 00 00…] ;
// réponse en entrée (identifiant 0 en tête retiré) : [0] = AA, [1] = 30, [8] = % (1..100), [9] = en charge (1 = en charge).
namespace BatteryTray.Providers.Hid;

public static class MchoseG3Driver
{
    const ushort Vid = 0x3837, WiredPid = 0x418C, WirelessPid = 0x4245;

    public static byte[] BuildRequest() => new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01, 0x00, 0x00 };

    /// <summary>Réponse : null si ce n'est pas la réponse ; (-1) si le niveau est hors 1..100 ; sinon (%, en charge).</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r)
    {
        if (r.Length < 10 || r[0] != 0xAA || r[1] != 0x30) return null;
        return r[8] is < 1 or > 100 ? (-1, false) : (r[8], r[9] == 1);
    }

    static (int Percent, bool Charging)? Query(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(BuildRequest())) return null;
        long end = Environment.TickCount64 + 1000;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            if (ParseReply(r) is { } p) return p.Percent < 0 ? null : p;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        bool found = false; // le filaire l'emporte sur le 2,4 GHz quand les deux sont connectés en même temps
        foreach (ushort pid in new[] { WiredPid, WirelessPid })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFF01 && c.Usage == 0x0010 && c.OutLen >= 65 && c.InLen >= 65))
            {
                if (found) { ctx.Claim(Vid, pid); break; }
                if (Query(c) is not { } p) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "mchose:g3v2", Name = c.Product.Length > 0 ? c.Product : "MCHOSE G3 V2", Kind = DeviceKind.Mouse,
                    Via = pid == WiredPid ? "USB cable" : "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging,
                }, Vid, pid);
                found = true;
                break;
            }
        }
    }
}
