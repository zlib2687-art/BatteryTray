// Clavier AJAZZ AK992 sur son récepteur 2,4 GHz Telink (320F:50C3).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/ajazz_ak992.cpp, porté en C# — voir NOTICE.md.
// Requête de batterie EVision en lecture seule (HeyOkay/HaloBattery, docs/protocols.md), vérifiée sur ce récepteur :
// le rapport numéroté 04 répète l'identifiant de requête (petit-boutiste) et la commande 1A, puis donne le pourcentage et l'état de charge
// aux octets 8 et 9 (identifiant de rapport compris).
namespace BatteryTray.Providers.Hid;

public static class AjazzDriver
{
    const ushort Vid = 0x320F, Pid = 0x50C3;
    const byte Report = 0x04, BatteryCommand = 0x1A;
    const int ReportSize = 64;

    public static byte[] BuildRequest(ushort sequence) => new byte[] { Report, (byte)sequence, (byte)(sequence >> 8), BatteryCommand };

    /// <summary>Réponse à la requête <paramref name="sequence"/> : (pourcentage, en charge) ou null.</summary>
    public static (int Percent, bool Charging)? ParseReply(byte[] r, ushort sequence)
    {
        if (r.Length < 10 || r[0] != Report || r[1] != (byte)sequence || r[2] != (byte)(sequence >> 8) || r[3] != BatteryCommand
            || r[5] != 0 || r[6] != 0 || r[8] > 100 || r[9] > 2) return null;
        return (r[8], r[9] != 0);
    }

    static (int Percent, bool Charging)? ReadBattery(HidDev d)
    {
        ushort seq = (ushort)Environment.TickCount64;
        d.Flush();
        if (!d.Write(BuildRequest(seq), 300)) return null;
        long deadline = Environment.TickCount64 + 600;
        for (long now = Environment.TickCount64; now < deadline; now = Environment.TickCount64)
        {
            var r = d.Read((int)(deadline - now));
            if (r == null) return null;
            if (ParseReply(r, seq) is { } b) return b;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Iface == 1 && c.Page == 0xFF1C && c.Usage == 0x0092 && c.InLen == ReportSize && c.OutLen == ReportSize && c.FeatLen == 0))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            var b = ReadBattery(d);
            if (b == null) continue;
            ctx.Add(new DeviceStatus
            {
                Key = "ajazz-ak992:320F:50C3", Name = "AJAZZ AK992", Kind = DeviceKind.Keyboard, Via = "2.4 GHz receiver",
                Percent = b.Value.Percent, Charging = b.Value.Charging,
            }, Vid, Pid);
            return;
        }
    }
}
