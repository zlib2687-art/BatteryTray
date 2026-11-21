// Souris EWEADN (série S7 / S9) sur l'identifiant propre à EWEADN, 372E :
//  * 372E:103D récepteur « Gaming 2.4G » partagé par les S7 Max / Pro / Ultra et S9 Max / Pro / Ultra / Ultra+
//  * 372E:1027 EWEADN S9 Ultra sur son câble USB
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/eweadn.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote web officiel d'EWEADN (tableau d'appareils : solutionRovider « beiying », hidReportId 3) :
//  * Requêtes = rapports de fonctionnalité de 64 octets, identifiant 3 (page FF00, interface 2), relus par GetFeature ~100 ms plus tard (jusqu'à 3 fois).
//    Requête : 03 <somme> 50 <long> 00 <cmd> <sous-cmd> 00…   <somme> = (somme des 63 octets suivant l'identifiant) & FF
//    Réponse : 03 50 <ack> <long> 00 <sous-cmd> <données…>      ack 0 = OK, données à partir de l'octet 6
//  * 47 00 (liaison sans fil) : données[0] = 1 si la souris est liée au récepteur.
//  * 4F 81 (informations de base, commande de lecture) : données[0] bit 7 = en charge, bits 0-6 = pourcentage.
// Seules ces deux commandes de lecture sont envoyées ; rien n'est écrit dans les réglages de la souris.
namespace BatteryTray.Providers.Hid;

public static class EweadnDriver
{
    const ushort Vid = 0x372E, Receiver = 0x103D, S9UltraCable = 0x1027;
    const byte ReportId = 0x03;

    public static byte[] BuildRequest(byte cmd, byte sub)
    {
        var req = new byte[64];
        req[0] = ReportId; req[2] = 0x50; req[5] = cmd; req[6] = sub;
        int sum = 0;
        for (int i = 1; i < req.Length; i++) sum += req[i];
        req[1] = (byte)(sum & 0xFF);
        return req;
    }

    /// <summary>Données d'une réponse valide (ack 0, paquet unique), ou null.</summary>
    public static byte[]? ParseReply(byte[] reply)
    {
        if (reply.Length < 8 || reply[0] != ReportId || reply[2] != 0 || reply[4] != 0) return null;
        int len = reply[3];
        if (len == 0 || 6 + len > reply.Length) return null;
        return reply[6..(6 + len)];
    }

    /// <summary>Octet « informations de base » → (pourcentage ou -1, en charge).</summary>
    public static (int Percent, bool Charging) ParseBasics(byte b) => ((b & 0x7F) <= 100 ? b & 0x7F : -1, (b & 0x80) != 0);

    static byte[]? Query(HidDev d, byte cmd, byte sub)
    {
        if (!d.SetFeature(BuildRequest(cmd, sub))) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(100);
            if (d.GetFeature(ReportId, out var reply) && ParseReply(reply) is { } data) return data;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (pid != Receiver && pid != S9UltraCable) continue; // les autres produits 372E (AULA, claviers) n'ont pas ce protocole
            bool cable = pid == S9UltraCable;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF00 && c.Usage == 1 && c.FeatLen == 64))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                var s = new DeviceStatus
                {
                    Key = ctx.StableKey("eweadn:" + HidUtil.Hex4(pid)), Name = cable ? "EWEADN S9 Ultra" : "EWEADN wireless mouse",
                    Kind = DeviceKind.Mouse, Via = cable ? "USB cable" : "2.4 GHz receiver",
                };
                if (!cable)
                {
                    var link = Query(d, 0x47, 0x00);
                    if (link == null) continue;
                    if (link[0] != 1) { s.Connected = false; ctx.Add(s, vid, pid); break; } // récepteur présent, souris éteinte ou hors de portée
                }
                var basics = Query(d, 0x4F, 0x81);
                if (basics == null) continue;
                var (pct, chg) = ParseBasics(basics[0]);
                if (pct < 0) continue;
                s.Percent = pct; s.Charging = chg;
                ctx.Add(s, vid, pid);
                break;
            }
        }
    }
}
