// Souris ATK / VXE NearLink sur le « NK mouse NANO dongle » 373B:1216 (VXE R1 NK et apparentées), ATK Zero (câble 1154, récepteurs 124F / 1155 8K) et récepteur « 8K Nano Dongle » 373B:1178.
// À exécuter AVANT KysonaDriver, qui sinon prend le VID 373B : ces récepteurs n'ont pas de collection à rapport 08 de 17 octets du protocole Kysona.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/atk_bitmouse.cpp, porté en C# — voir NOTICE.md.
// Protocole BITMOUSE d'ATK sur la collection vendeur FF05:0001 : rapport de sortie 08 de 64 octets : [08][somme][72][02][00][01][cmd][01][00…], somme = somme(octets 2..63) & 0xFF ; cible 01 = la souris derrière le récepteur.
//   cmd 07 = niveau de batterie, cmd 0F = indicateur de charge. Réponse sur la même collection : [08][72][état][seq][00][cmd][01][valeur]… ; état FF = erreur. Pas de réponse = souris endormie ou éteinte.
//   Sources : HeyOkay/HaloBattery PR #350 (providers/atk_bitmouse.py ; vérifié par un propriétaire de VXE R1 : le niveau correspond à l'appli d'ATK, la charge suit le câble) et qwr79/mouse-battery.
// ATK Zero : même trame de 64 octets sur FF05:0001, cible 00 pour le câble 1154 ou 01 pour les récepteurs 124F / 1155 8K ; seul l'état 00 est frais ; la commande 07 lit le niveau sur chaque liaison, la commande 08 signale la
//   charge (02) sur le câble ; les récepteurs ne relaient que la requête de batterie (somme fixe 0x7D). Référence : Fan4Metal/mouse_tray (chipset/nordic54.py).
// ATK « 8K Nano Dongle » 373B:1178 : récepteur Holtek au protocole différent sur la collection vendeur FF10:0001 (entrée / sortie de 65 octets, fonctionnalité de 9 octets, pas d'identifiants de rapport) :
//   rapport de fonctionnalité 00 de 8 octets : [82][00 × 7] (GetConfigData) ; réponse en rapport d'entrée : [82][cadence][cran DPI][mode lumière][luminosité][vitesse][couleur][réservé][batterie][liaison]… ;
//   batterie bit 7 = en charge, bits 0..6 = % ; liaison 0 = aucune souris appairée / éveillée. D'après ATK HUB (v3-hub.atkgear.com 3.2.33, AtkRadioGroup).
namespace BatteryTray.Providers.Hid;

public static class AtkBitmouseDriver
{
    const ushort Vid = 0x373B, NanoDongle = 0x1216, Nano8kDongle = 0x1178, ZeroCable = 0x1154;

    public enum Link { NearLink, ZeroCable, ZeroReceiver }

    public static byte[] BuildQuery(byte cmd, Link link = Link.NearLink)
    {
        bool zero = link != Link.NearLink;
        var req = new byte[64];
        req[0] = 0x08; req[2] = 0x72; req[3] = 0x02; req[5] = (byte)(link == Link.ZeroCable ? 0x00 : 0x01); req[6] = cmd; req[7] = 0x01;
        int sum = 0; for (int i = 2; i < req.Length; i++) sum += req[i];
        req[1] = zero ? (byte)0x7D : (byte)(sum & 0xFF);
        return req;
    }

    /// <summary>Valeur d'une réponse à <paramref name="cmd"/>, ou null (autre réponse) ; -1 si erreur.</summary>
    public static int? ParseReply(byte[] r, byte cmd, Link link)
    {
        bool zero = link != Link.NearLink;
        if (r.Length < 8 || r[0] != 0x08 || r[1] != 0x72) return null;
        if (r[5] != cmd || (zero && r[6] != 0x01)) return null; // une réponse à une autre requête
        if (zero ? r[2] != 0x00 : r[2] == 0xFF) return -1;
        return r[7];
    }

    /// <summary>Réponse GetConfigData du Nano 8K : ((%, en charge)) ; null si la liaison est nulle ou le niveau invalide.</summary>
    public static (int Percent, bool Charging)? ParseNano8k(byte[] r)
    {
        if (r.Length < 10 || r[0] != 0x82 || r[9] == 0) return null;
        int level = r[8] & 0x7F;
        return level is < 1 or > 100 ? null : (level, (r[8] & 0x80) != 0);
    }

    static int Query(HidDev d, byte cmd, Link link = Link.NearLink)
    {
        bool zero = link != Link.NearLink;
        d.Flush();
        if (!d.Write(BuildQuery(cmd, link))) return -1;
        long end = Environment.TickCount64 + (zero ? 500 : 700);
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return -1;
            var r = d.Read(left);
            if (r == null) return -1;
            if (ParseReply(r, cmd, link) is { } v) return v;
        }
    }

    static void PollZero(PollContext ctx)
    {
        bool reported = false;
        // Le câble d'abord pour une lecture fraîche pendant la charge, et le même historique de batterie sur l'une ou l'autre liaison.
        foreach (ushort pid in new ushort[] { ZeroCable, 0x124F, 0x1155 })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            foreach (var c in ctx.Find(Vid, pid, c => c.Page == 0xFF05 && c.Usage == 0x0001 && c.OutLen == 64 && c.InLen == 64))
            {
                ctx.Claim(Vid, pid); // garde les anciennes trames Kysona hors de cette collection, même endormie
                if (reported) break;
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                bool wired = pid == ZeroCable;
                var link = wired ? Link.ZeroCable : Link.ZeroReceiver;
                int level = Query(d, 0x07, link);
                if (level is < 0 or > 100) continue;
                ctx.Out.Add(new DeviceStatus
                {
                    Key = "atkzero:" + HidUtil.Hex4(ZeroCable), Name = "ATK Zero", Kind = DeviceKind.Mouse, Via = wired ? "USB cable" : "2.4 GHz receiver",
                    Percent = level, Charging = wired && Query(d, 0x08, link) == 0x02, Vid = Vid,
                });
                reported = true;
                break;
            }
        }
    }

    static byte[]? QueryNano8kConfig(HidDev d)
    {
        d.Flush();
        if (!d.SetFeature(new byte[] { 0x00, 0x82 })) return null;
        long end = Environment.TickCount64 + 700;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null || r.Length < 10) return null;
            if (r[0] == 0x82) return r; // les autres rapports d'entrée (notifications 0x12 de DPI / cadence) sont ignorés
        }
    }

    static void PollNano8k(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Nano8kDongle)) return;
        foreach (var c in ctx.Find(Vid, Nano8kDongle, c => c.Page == 0xFF10 && c.Usage == 0x0001 && c.FeatLen >= 9 && c.InLen >= 11))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            if (QueryNano8kConfig(d) is not { } r) continue;
            // Le récepteur parle ce protocole : on empêche le pilote Kysona de le sonder avec ses trames de 17 octets.
            if (ParseNano8k(r) is not { } p) { ctx.Claim(Vid, Nano8kDongle); return; } // aucune souris liée (éteinte ou endormie), ou niveau invalide
            ctx.Add(new DeviceStatus { Key = "atknano8k:" + HidUtil.Hex4(Nano8kDongle), Name = "ATK 8K Nano mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging }, Vid, Nano8kDongle); // récepteur partagé par plusieurs modèles ATK / VXE
            return;
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollZero(ctx);
        PollNano8k(ctx);
        if (ctx.IsClaimed(Vid, NanoDongle)) return;
        foreach (var c in ctx.Find(Vid, NanoDongle, c => c.Page == 0xFF05 && c.Usage == 0x0001 && c.OutLen >= 64 && c.InLen >= 8))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int level = Query(d, 0x07);
            if (level is < 1 or > 100) continue; // endormie / éteinte, ou pas de lecture valide
            ctx.Add(new DeviceStatus
            {
                Key = "atknk:" + HidUtil.Hex4(NanoDongle), Name = "ATK / VXE NK mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = level, Charging = Query(d, 0x0F) == 1,
            }, Vid, NanoDongle);
            break;
        }
    }
}
