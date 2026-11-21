// Récepteur DARKRIM SHADOW (3654:6901), testé sur matériel, sans logiciel du fabricant.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/darkrim.cpp, porté en C# — voir NOTICE.md.
// Source : app.darkrim.net/hub (DARKRIM 0.1.26), inspecté statiquement : sendGetBasicInfo et constructeur de trame. Seule la requête GET 0010 (Request 01), sélecteur TLV de batterie 01 00,
// est envoyée. La réponse porte le TLV 01 : bit 7 en charge, bits 0..6 pourcentage. Trame : FE seq cmd(LE) type index nombre longueur(LE) charge CRC-BE (CRC-16/CCITT-FALSE), puis identifiant de rapport 0.
namespace BatteryTray.Providers.Hid;

public static class DarkrimDriver
{
    const ushort Vid = 0x3654, Pid = 0x6901;

    /// <summary>CRC-16/CCITT-FALSE (polynôme 0x1021, initialisation FFFF) sur les <paramref name="count"/> premiers octets.</summary>
    public static ushort Crc(byte[] r, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= (ushort)(r[i] << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    public static byte[] BatteryRequest(byte seq)
    {
        var r = new byte[] { 0xFE, seq, 0x10, 0, 1, 0, 1, 2, 0, 1, 0 };
        ushort crc = Crc(r, r.Length);
        return new byte[] { 0 }.Concat(r).Concat(new[] { (byte)(crc >> 8), (byte)crc }).ToArray(); // identifiant de rapport 0 en tête
    }

    public static (int Percent, bool Charging)? ParseReply(byte[] r, byte seq)
    {
        if (r.Length < 11 || r[0] != 0xFE || r[1] != seq || r[2] != 0x10 || r[3] != 0 || r[4] != 2 || r[5] != 0 || r[6] > 1) return null;
        int len = r[7] | (r[8] << 8);
        if (len > 53 || len + 11 > r.Length) return null;
        int end = 9 + len;
        if (Crc(r, end) != (ushort)((r[end] << 8) | r[end + 1])) return null;
        int value = -1;
        for (int i = 9; i < end;)
        {
            if (end - i < 2) return null;
            byte type = r[i++], size = r[i++];
            if (size > end - i) return null;
            if (type == 1)
            {
                if (size != 1 || value >= 0 || (r[i] & 0x7F) > 100) return null;
                value = r[i];
            }
            i += size;
        }
        return value < 0 ? null : (value & 0x7F, (value & 0x80) != 0);
    }

    static byte _nextSeq;

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Iface == 3 && c.Page == 0xFF02 && c.Usage == 2 && c.InLen == 65 && c.OutLen == 65))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            byte seq = _nextSeq = (byte)(_nextSeq % 15 + 1);
            if (!d.Write(BatteryRequest(seq), 200)) continue;
            // Le récepteur envoie d'abord plusieurs notifications de démarrage ; la réponse GET a été mesurée à ~406 ms. Une seule échéance les couvre.
            long end = Environment.TickCount64 + 800;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = d.Read(left);
                if (r == null) break;
                if (ParseReply(r, seq) is not { } p) continue;
                ctx.Add(new DeviceStatus { Key = "darkrim:SHADOW", Name = "DARKRIM SHADOW", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging }, Vid, Pid);
                return;
            }
        }
    }
}
