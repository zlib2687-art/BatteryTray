// Casques VGN au protocole « ZHOU » (VID 372E). Prend en charge le casque VGN OMNISOUND (ZHOUTING) sur son récepteur 2,4 GHz (372E:10BB), collection de contrôle 000C:0001
// (entrée 32 octets, sortie 64 octets, fonctionnalité 16 octets ; identifiant de rapport 0x55). La variante filaire et le Siren V2 ont la même forme de trame dans V HUB mais ne sont pas activés (non testés).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/vgn.cpp, porté en C# — voir NOTICE.md.
// Protocole du paquet V HUB 4.0.8 de VGN (HdServe.getBattery, branche pid 4283 de ReadHidBuffer), vérifié sur un récepteur ZHOUTING :
//   lecture batterie : rapport de sortie 0x55, charge [0] 0x65, [1] 0x01, reste à zéro (lecture seule). La réponse est un rapport d'entrée portant l'identifiant 0x55, puis 0x65, puis le pourcentage.
//   V HUB force « en charge = faux » pour ce récepteur : aucun bit de charge connu, la charge reste fausse.
namespace BatteryTray.Providers.Hid;

public static class VgnDriver
{
    const ushort Vid = 0x372E, ZhouWirelessPid = 0x10BB;
    const byte ReportId = 0x55, ReadBattery = 0x65;

    public static byte[] BuildRequest() => new byte[] { ReportId, ReadBattery, 0x01 };

    /// <summary>Rapport d'entrée → pourcentage. Numéroté ([55][65][%]) ou, à défaut, sans numéro ([65][%]) ; null si ce n'est pas la réponse ou si &gt; 100.</summary>
    public static int? ParseBattery(byte[] r)
    {
        int percent;
        if (r.Length >= 3 && r[0] == ReportId && r[1] == ReadBattery) percent = r[2];
        else if (r.Length >= 2 && r[0] == ReadBattery) percent = r[1];
        else return null;
        return percent <= 100 ? percent : null;
    }

    static int QueryZhou(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return -1;
        d.Flush();
        // Pas de canal d'interruption OUT : la requête part comme rapport de sortie HID et la réponse est le rapport d'entrée suivant.
        if (!d.Write(BuildRequest())) return -1;
        var r = d.Read(500);
        if (r != null && ParseBattery(r) is { } p) return p;
        // On écoute encore brièvement : la trame de batterie peut arriver derrière d'autres notifications.
        long end = Environment.TickCount64 + 300;
        while (end - Environment.TickCount64 > 0)
        {
            r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            if (ParseBattery(r) is { } q) return q;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, ZhouWirelessPid)) return;
        foreach (var c in ctx.Find(Vid, ZhouWirelessPid, c => c.Page == 0x000C && c.Usage == 0x0001 && c.OutLen >= 63 && c.InLen >= 3))
        {
            int pct = QueryZhou(c);
            if (pct < 0) continue;
            ctx.Add(new DeviceStatus
            {
                Key = "vgn:zhou", Name = c.Product.Length > 0 ? c.Product : "VGN OMNISOUND (ZHOUTING)", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver", Percent = HidUtil.Clamp100(pct),
            }, Vid, ZhouWirelessPid);
            break;
        }
    }
}
