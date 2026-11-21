// Microphone MAONO PD200W sur son récepteur 2,4 GHz (352F:040E), testé sur matériel par son propriétaire : interface 3, page / usage 000C:0001, rapports d'entrée / sortie de 64 octets, pas de rapport de fonctionnalité.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/maono.cpp, porté en C# — voir NOTICE.md.
// Le registre de batterie 0x0042 suit le niveau du logiciel du fabricant à mesure que la batterie se décharge. Si le micro ne répond plus, le récepteur doit rester « pris » pour que le pilote HID générique ne signale pas un faux 0 %.
// Transport documenté pour un autre PID (352F:0411) : iamsh4/maomao (protocol-pd200w.md) ; registre de batterie du PD100W voisin : shahriyardx/maono (src/mic.rs). Seul GET (0x04) est envoyé, jamais SET (0x03). La charge n'est pas implémentée.
namespace BatteryTray.Providers.Hid;

public static class MaonoDriver
{
    const ushort Vid = 0x352F, Pid = 0x040E, BatteryRegister = 0x0042;
    const byte ReportId = 0xC4, Get = 0x04;
    const int Interface = 3, ReportLength = 64;

    public static ushort ReadLe16(byte[] d, int offset) => (ushort)(d[offset] | (d[offset + 1] << 8));

    public static ushort Checksum(byte[] d, int count)
    {
        uint sum = 0; for (int i = 0; i < count; i++) sum += d[i];
        return (ushort)((0x10000u - (sum & 0xFFFFu)) & 0xFFFFu);
    }

    /// <summary>Trame GET candidate : C4 09 00 00 04 42 00 ED FE (C4 est l'identifiant de rapport, le reste est du remplissage).</summary>
    public static byte[] BatteryRequest()
    {
        var r = new byte[ReportLength];
        r[0] = ReportId; r[1] = 9; r[4] = Get; r[5] = (byte)(BatteryRegister & 0xFF); r[6] = (byte)(BatteryRegister >> 8);
        ushort cs = Checksum(r, 7); r[7] = (byte)(cs & 0xFF); r[8] = (byte)(cs >> 8);
        return r;
    }

    /// <summary>Corps attendu : une ou plusieurs paires (registre, valeur) petit-boutistes, d'après l'implémentation du PD100W voisin.</summary>
    public static int? ParseBattery(byte[] reply)
    {
        if (reply.Length < 11 || reply[0] != ReportId) return null;
        int length = reply[1];
        if (length < 11 || length > reply.Length) return null;
        if (reply[2] != 0 || reply[3] != 0 || reply[4] != Get) return null;
        int bodyEnd = length - 2;
        if ((bodyEnd - 5) % 4 != 0) return null;
        if (ReadLe16(reply, bodyEnd) != Checksum(reply, bodyEnd)) return null;
        for (int offset = 5; offset < bodyEnd; offset += 4)
        {
            if (ReadLe16(reply, offset) != BatteryRegister) continue;
            uint value = ReadLe16(reply, offset + 2);
            return value > 100 ? null : (int)value;
        }
        return null;
    }

    static int ReadBattery(HidDev d)
    {
        // Une seule échéance absolue pour l'envoi et la réception.
        long deadline = Environment.TickCount64 + 1000;
        d.Flush();
        long before = Environment.TickCount64;
        if (before >= deadline) return -1;
        if (!d.Write(BatteryRequest(), (int)Math.Min(250, deadline - before))) return -1;
        for (int reports = 0; reports < 128; reports++) // les rapports sans rapport sont ignorés sans relancer l'échéance
        {
            long now = Environment.TickCount64;
            if (now >= deadline) break;
            var r = d.Read((int)(deadline - now));
            if (r == null || Environment.TickCount64 >= deadline) break;
            if (ParseBattery(r) is { } p) return p;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        var colls = ctx.Find(Vid, Pid, c => c.Iface == Interface && c.Page == 0x000C && c.Usage == 0x0001 && c.InLen == ReportLength && c.OutLen == ReportLength && c.FeatLen == 0);
        if (colls.Count == 0) return;
        // Le récepteur USB reste énuméré micro éteint : on réserve cet appareil exact même si l'ouverture ou la lecture échoue.
        ctx.Claim(Vid, Pid);
        foreach (var c in colls)
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            int pct = ReadBattery(d);
            if (pct < 0) continue;
            // Les registres de charge et de liaison ne sont pas établis : aucun état n'est déduit du pourcentage.
            ctx.Add(new DeviceStatus { Key = ctx.StableKey("maono:pd200w"), Name = "MAONO PD200W", Kind = DeviceKind.Microphone, Via = "2.4 GHz receiver", Percent = pct }, Vid, Pid);
            return;
        }
    }
}
