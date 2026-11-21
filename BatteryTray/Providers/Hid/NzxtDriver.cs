// Souris NZXT Lift Elite Wireless sur son récepteur (1E71:2101) ou son câble USB (1E71:2129), sans NZXT CAM.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/nzxt.cpp, porté en C# — voir NOTICE.md.
// Porté de HaloBattery (HeyOkay, MIT ; confirmé sur la souris du rapporteur), déduit de captures USB de NZXT CAM 4.76.5.
// Rapports de 64 octets sur la collection vendeur FFCA:0001 :
//   requête : 4E 02 81 00 B0 00… (02 = la souris, 81 = lire une propriété, B0 = télémétrie)
//   réponse : 4E 02 97… ; octets 17-18 = pourcentage (petit-boutiste), octets 8-9 = mV de la cellule (grand-boutiste), octet 22 bit 0 = sur le câble de charge.
//   Le récepteur commence par un accusé 4E E5, ignoré. Seule cette lecture est envoyée. NZXT CAM ouvert, le récepteur ne répond plus ; la dernière valeur est conservée.
namespace BatteryTray.Providers.Hid;

public static class NzxtDriver
{
    const ushort Vid = 0x1E71, Receiver = 0x2101, Cable = 0x2129;

    public static byte[] BuildRequest() { var r = new byte[65]; r[1] = 0x4E; r[2] = 0x02; r[3] = 0x81; r[5] = 0xB0; return r; } // identifiant de rapport 0 puis le paquet

    /// <summary>Réponse de télémétrie : (pourcentage, millivolts, en charge) ; null pour un accusé, un autre rapport ou un niveau supérieur à 100 (refusé plutôt qu'affiché).</summary>
    public static (int Percent, int Millivolts, bool Charging)? ParseTelemetry(byte[] r)
    {
        if (r.Length < 27 || r[0] != 0x4E || r[1] == 0xE5 || r[1] != 0x02 || r[2] != 0x97) return null;
        int level = r[17] | (r[18] << 8);
        return level > 100 ? null : (level, (r[8] << 8) | r[9], (r[22] & 0x01) != 0);
    }

    /// <summary>Ne conserve la tension que si elle est plausible pour une cellule Li-ion.</summary>
    public static bool PlausibleMv(int mv) => mv is >= 3000 and <= 4500;

    static (int Percent, int Millivolts, bool Charging)? ReadTelemetry(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(BuildRequest())) return null;
        for (int i = 0; i < 10; i++)
        {
            var r = d.Read(150);
            if (r == null) return null;
            if (r.Length < 27 || r[0] != 0x4E) continue;
            if (ParseTelemetry(r) is { } t) return t;
            if (r[1] != 0x02 && r[1] != 0xE5) continue;
            if (r[1] == 0x02 && r[2] == 0x97) return null; // niveau invalide : on refuse
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        // Une seule entrée pour la souris sur les deux identifiants ; la lecture du câble l'emporte si les deux répondent (c'est celle qui charge).
        bool any = false; (int Percent, int Millivolts, bool Charging)? have = null;
        foreach (ushort pid in new[] { Cable, Receiver })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            var colls = ctx.Find(Vid, pid, c => c.Page == 0xFFCA && c.Usage == 0x0001);
            if (colls.Count == 0) continue;
            any = true;
            ctx.Claim(Vid, pid);
            foreach (var c in colls)
                if (have == null && ReadTelemetry(c) is { } t) have = t;
        }
        if (!any) return;
        var s = new DeviceStatus { Key = "nzxt:2101", Name = "NZXT Lift Elite", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Vid = Vid };
        if (have is { } h) { s.Percent = h.Percent; s.Charging = h.Charging; }
        else s.Connected = false; // souris éteinte ou récepteur tenu par NZXT CAM : la dernière valeur est conservée
        ctx.Out.Add(s);
    }
}
