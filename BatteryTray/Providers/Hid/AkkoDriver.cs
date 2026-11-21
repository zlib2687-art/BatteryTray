// Akko V9 Dash Pro (38EE:000C) et Akko Dash V9 Ultra (38EE:000F, « Akko 8K Receiver ») en 2,4 GHz.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/akko.cpp, porté en C# — voir NOTICE.md.
// Même canal vendeur de 64 octets (page FF1C, usage 0092) que les appareils Evision / CHERRY / ATK (protocole de B1Chat/BatteryWatch, MIT) :
//   trame : [0] rapport 4, [1..2] somme de contrôle (LE) = somme des octets [3..fin] & 0xFFFF, [3] type, [4..] charge
//   requête : type 0x1A, charge 03 00 00 55 (demande d'état, lecture seule)
//   réponse : [0] 4, [3] 0x1A, [8] pourcentage (0..100), [9] non nul en charge
namespace BatteryTray.Providers.Hid;

public static class AkkoDriver
{
    const ushort Vid = 0x38EE, Page = 0xFF1C, Usage = 0x0092;
    const int Len = 64;
    const byte ReportId = 0x04, StatusType = 0x1A;

    public static readonly (ushort Pid, string Name, string Key)[] Models =
    {
        (0x000C, "Akko V9 Dash Pro", "akko:38ee:000c"),
        (0x000F, "Akko Dash V9 Ultra", "akko:38ee:000f"),
    };

    public static byte[] BuildQuery()
    {
        var f = new byte[Len];
        f[0] = ReportId; f[3] = StatusType; f[4] = 0x03; f[7] = 0x55;
        uint sum = 0;
        for (int i = 3; i < f.Length; i++) sum += f[i];
        f[1] = (byte)(sum & 0xFF); f[2] = (byte)((sum >> 8) & 0xFF);
        return f;
    }

    public static bool IsStatusReply(byte[] r) => r.Length >= 10 && r[0] == ReportId && r[3] == StatusType && r[8] <= 100;

    static byte[]? Query(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        var query = BuildQuery();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!d.Write(query, 300)) return null;
            long end = Environment.TickCount64 + 400;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = d.Read(left);
                if (r == null) break;
                if (IsStatusReply(r)) return r;
            }
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c => c.Page == Page && c.Usage == Usage && c.InLen == Len && c.OutLen == Len))
            {
                var r = Query(c);
                if (r == null) continue;
                ctx.Add(new DeviceStatus { Key = m.Key, Name = m.Name, Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = r[8], Charging = r[9] != 0 }, Vid, m.Pid);
                break;
            }
        }
    }
}
