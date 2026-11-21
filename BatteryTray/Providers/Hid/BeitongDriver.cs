// Manettes BEITONG (Betop) KP20 / BTP-KP20 (20BC:5128) et KP40D / BTP-KP40D (20BC:515C) sur leurs récepteurs 2,4 GHz, mode XInput.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/beitong.cpp, porté en C# — voir NOTICE.md.
// XInput déclare le récepteur comme une manette filaire : la batterie se lit donc sur la collection vendeur (page 00FF, usage 0003, rapports de 64 octets, identifiant 02)
// avec la requête « infos de base » qu'envoie BEITONG iControl pour afficher la batterie.
// Tout octet après l'identifiant de rapport est brouillé dans les deux sens par un chiffre fixe de quartets :
//   enc(x) = S[x >> 4] << 4 | ((x & 0x0F) ^ 0x08),  S = 0 A 6 F 1 C 2 9 E 3 7 D 4 B 5 8 ; le remplissage de zéros après la charge est envoyé en clair.
//   Requête (claire) : 02 15 → sur le fil : 02 AD 00 00… ; réponse (claire) : 02 15 <batterie 0..100> 00 <état de charge>
// Le KP40D envoie la même requête et lit la réponse de la même façon, sauf qu'il ne garde que le quartet bas de l'octet d'état de charge. Le récepteur répond sans le mode
// configuration d'iControl. Les autres trames de la collection (02 25 état des touches, 02 21 synchro) n'apparaissent que iControl ouvert et sont ignorées.
// Protocole déduit de BEITONG iControl 1.0.5 (kp20.js, kp40d.js) et du trafic USB d'un récepteur BTP-KP20 ; chiffre confirmé sur plus de 32 000 octets décodés. Récepteur KP40D non testé sur matériel.
namespace BatteryTray.Providers.Hid;

public static class BeitongDriver
{
    const ushort Vid = 0x20BC, Kp20 = 0x5128, Kp40d = 0x515C;
    const byte ReportId = 0x02, GetBaseInfo = 0x15; // commande 5 (état), sous-commande 1 (infos de base)

    static readonly byte[] Nibble = { 0x0, 0xA, 0x6, 0xF, 0x1, 0xC, 0x2, 0x9, 0xE, 0x3, 0x7, 0xD, 0x4, 0xB, 0x5, 0x8 };

    public static byte Enc(byte x) => (byte)((Nibble[x >> 4] << 4) | ((x & 0x0F) ^ 0x08));

    public static byte Dec(byte x)
    {
        byte hi = 0;
        for (byte i = 0; i < 16; i++) if (Nibble[i] == (x >> 4)) hi = i;
        return (byte)((hi << 4) | ((x & 0x0F) ^ 0x08));
    }

    public static string? ReceiverName(ushort pid) => pid switch { Kp20 => "BEITONG KP20", Kp40d => "BEITONG KP40D", _ => null };

    public static byte[] BuildRequest() => new byte[] { ReportId, Enc(GetBaseInfo) };

    /// <summary>Réponse « infos de base » : (pourcentage, en charge) ; null si ce n'est pas la bonne réponse ou si le niveau dépasse 100.</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r, ushort pid)
    {
        if (r.Length < 5 || r[0] != ReportId || Dec(r[1]) != GetBaseInfo) return null;
        byte level = Dec(r[2]);
        if (level > 100) return null;
        byte charge = Dec(r[4]);
        if (pid == Kp40d) charge &= 0x0F; // kp40d.js masque l'état à son quartet bas
        return (level, charge == 1);
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (ReceiverName(pid) is not { } name) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0x00FF && c.Usage == 0x0003 && c.OutLen >= 3 && c.InLen >= 5))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                if (!d.Write(BuildRequest())) continue;
                (int Percent, bool Charging)? found = null;
                long end = Environment.TickCount64 + 800;
                while (found == null && end - Environment.TickCount64 > 0)
                {
                    var r = d.Read(200);
                    if (r == null || r.Length < 5) continue;
                    if (r[0] == ReportId && Dec(r[1]) == GetBaseInfo) { found = ParseReply(r, pid); if (found == null) break; }
                }
                if (found == null) continue;
                ctx.Add(new DeviceStatus { Key = "beitong:" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Gamepad, Via = "2.4 GHz receiver", Percent = found.Value.Percent, Charging = found.Value.Charging }, vid, pid);
                break;
            }
        }
    }
}
