// Souris sans fil Redragon sur un récepteur 2,4 GHz Evision (320F:226F, « 2.4G Wireless Receiver »), p. ex. la K1NG M999.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/redragon_evision.cpp, porté en C# — voir NOTICE.md.
// Même requête de batterie que le pilote ATK V75X (HaloBattery / ATK HUB), qui est aussi le protocole d'Evision. Confirmé sur une Redragon M999 par une capture USB (Wireshark + USBPcap) du logiciel du fabricant :
//   rapport de sortie 04 sur FF1C:0092, 31 octets de charge : [04][seq lo][seq hi][1A][02][00…] ; rapport d'entrée 04 : [04][seq lo][seq hi][1A][02][00 00][00][%][état] (0x61 = 97 % correspond à l'appli du fabricant).
// L'octet d'état (0 sur batterie, 1 / 2 en charge) vient du pilote ATK ; sur la M999 l'indicateur de charge apparaît sur sa base magnétique. Câble USB branché, le récepteur continuait de répondre 97 % état 0
// (la souris passe probablement en filaire et ne parle plus au récepteur, qui répète sa dernière valeur). Le logiciel du fabricant répète la requête toutes les ~300 ms ; cette appli demande une fois par lecture.
namespace BatteryTray.Providers.Hid;

public static class RedragonEvisionDriver
{
    const ushort Vid = 0x320F, Receiver = 0x226F;

    public static byte[] BuildRequest(ushort seq) { var r = new byte[32]; r[0] = 0x04; r[1] = (byte)seq; r[2] = (byte)(seq >> 8); r[3] = 0x1A; r[4] = 0x02; return r; }

    /// <summary>Réponse à la requête <paramref name="seq"/> : ignore (null) un autre rapport ; (-1) si valeurs invalides ; sinon (%, en charge).</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r, ushort seq)
    {
        if (r.Length < 10 || r[0] != 0x04 || r[1] != (byte)seq || r[2] != (byte)(seq >> 8) || r[3] != 0x1A || r[5] != 0 || r[6] != 0) return null;
        if (r[8] > 100 || r[9] > 2) return (-1, false);
        return (r[8], r[9] == 1 || r[9] == 2);
    }

    static ushort _seq;

    static (int Percent, bool Charging)? ReadBattery(HidColl c)
    {
        _seq = (ushort)(_seq % 0xFFFF + 1);
        ushort seq = _seq;
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(BuildRequest(seq))) return null;
        long end = Environment.TickCount64 + 1000;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read(100);
            if (r == null) continue;
            if (ParseReply(r, seq) is not { } p) continue;
            return p.Percent < 0 ? null : p;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Receiver)) return;
        foreach (var c in ctx.Find(Vid, Receiver, c => c.Page == 0xFF1C && c.Usage == 0x0092))
        {
            // L'identifiant du récepteur peut être partagé par plusieurs modèles ; seule la M999 est vérifiée.
            var s = new DeviceStatus { Key = "redragon_evision:" + HidUtil.Hex4(Receiver), Name = "Redragon wireless mouse", Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver" };
            if (ReadBattery(c) is { } p) { s.Percent = p.Percent; s.Charging = p.Charging; }
            else s.Connected = false; // pas de réponse : endormie, éteinte ou occupée ; on garde la dernière valeur
            ctx.Add(s, Vid, Receiver);
            return;
        }
    }
}
