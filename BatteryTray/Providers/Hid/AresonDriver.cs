// Souris sans fil Redragon sur récepteurs 2,4 GHz Areson / CompX (VID 25A7), p. ex. M913 Impact Elite et M915RGB-WL.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/areson.cpp, porté en C# — voir NOTICE.md.
// La batterie n'est pas poussée : il faut la demander avec la commande « GetPower » en lecture seule ; rien n'est jamais écrit dans la souris (ni LED, ni DPI, ni configuration) :
//   requête : SET_FEATURE sur la collection FF02:0002 de l'interface 1, 17 octets : [08][04][00…][somme] avec somme = 0x55 − somme(octets 0..15)
//   réponse : rapport d'entrée sur la collection FF01:0000 de l'interface 1 : [09][04][00][..][..][..][niveau][charge][00…][somme] ; la trame entière totalise 0x55 (mod 256) ;
//   niveau 0..10 → 0..100 % (le logiciel officiel affichait 80 % pour le niveau 8) ; charge = tout octet non nul.
// Sources : Qehbr/m913-ctl, Doomguy-BFG9000/redragon-m913-battery-tray, capture du logiciel Redragon officiel sur un M915RGB-WL (récepteur 25A7:FA07).
// Seuls les identifiants FA07 / FA08 sont traités : la famille FA7B / FA7C donne le niveau sur une autre échelle (voir FantechDriver).
namespace BatteryTray.Providers.Hid;

public static class AresonDriver
{
    public static byte[] BuildGetPower()
    {
        var req = new byte[17]; req[0] = 0x08; req[1] = 0x04;
        int sum = 0; for (int i = 0; i < 16; i++) sum += req[i];
        req[16] = (byte)(0x55 - sum);
        return req;
    }

    /// <summary>Réponse GetPower : (pourcentage, en charge) ; null si la somme n'est pas 0x55, l'en-tête est faux ou le niveau dépasse 10.</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r)
    {
        if (r.Length < 17) return null;
        int total = 0; foreach (var b in r) total += b;
        if ((total & 0xFF) != 0x55 || r[0] != 0x09 || r[1] != 0x04 || r[2] != 0x00 || r[6] > 10) return null;
        return (HidUtil.Clamp100(r[6] * 10), r[7] != 0);
    }

    static (int Percent, bool Charging)? QueryPower(HidDev cmd, HidDev reply)
    {
        var req = BuildGetPower();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) Thread.Sleep(200);
            reply.Flush(); // un rapport arrivé avant la requête serait pris pour la réponse
            if (!cmd.SetFeature(req)) return null; // récepteur parti : réessayer n'aidera pas
            var r = reply.Read(1800);
            if (r == null || r.Length < 17) continue; // endormie ou hors de portée
            if (ParseReply(r) is { } p) return p;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x25A7))
        {
            if (pid != 0xFA07 && pid != 0xFA08) continue;
            bool done = false;
            foreach (var c in ctx.Find(vid, pid, c => c.Iface == 1 && c.Page == 0xFF02 && c.Usage == 0x0002 && c.FeatLen >= 17))
            {
                foreach (var i in ctx.Find(vid, pid, c => c.Iface == 1 && c.Page == 0xFF01 && c.Usage == 0x0000 && c.InLen >= 17))
                {
                    using var cmd = new HidDev(); using var reply = new HidDev();
                    if (!cmd.OpenFeatureOnly(c) || !reply.Open(i)) continue; // l'accès nul suffit pour les rapports de fonctionnalité
                    if (QueryPower(cmd, reply) is not { } p) continue; // muette : on ne prend rien, la souris garde sa dernière valeur
                    ctx.Add(new DeviceStatus
                    {
                        Key = "areson:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "Redragon wireless mouse", Kind = DeviceKind.Mouse,
                        Via = "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging,
                    }, vid, pid);
                    done = true; break;
                }
                if (done) break;
            }
        }
    }
}
