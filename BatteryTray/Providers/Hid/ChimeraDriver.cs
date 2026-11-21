// Souris sans fil Chimera V2 (VID 3554 / PID F512).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/chimera.cpp, porté en C# — voir NOTICE.md.
// Protocole batterie sur la collection vendeur FF02:0002 (17 en entrée / 17 en sortie) : identifiant de rapport 0x08, somme = 0x55 − somme(octets 0..15).
//   cmd 0x03 → état en ligne : réponse [08 03 00 00 00 état…] (octet 5 ≠ 0 = en ligne)
//   cmd 0x04 → batterie : réponse [08 04 00 00 00 état % charge mV_hi mV_lo…] ; octet 5 == 02 : connectée ; octet 6 : % (1..100) ; octet 7 ≠ 0 : en charge ; octets 8..9 grand-boutiste : millivolts.
// Lecture seule : seul l'état de la batterie est demandé. Protocole découvert en sondant l'appareil (--dump montre 3554:F512 sur FF02:0002 17/17/0).
namespace BatteryTray.Providers.Hid;

public static class ChimeraDriver
{
    public static byte[] BuildQuery(byte cmd)
    {
        var req = new byte[17]; req[0] = 0x08; req[1] = cmd;
        int sum = 0; for (int i = 0; i < 16; i++) sum += req[i];
        req[16] = (byte)(0x55 - sum);
        return req;
    }

    /// <summary>Réponse valide à <paramref name="cmd"/> : 17 octets, bonne commande, somme de contrôle exacte.</summary>
    public static bool IsValidReply(byte[] r, byte cmd)
    {
        if (r.Length < 17 || r[0] != 0x08 || r[1] != cmd) return false;
        int rs = 0; for (int i = 0; i < 16; i++) rs += r[i];
        return r[16] == (byte)(0x55 - rs);
    }

    public static bool IsOnline(byte[] r) => r.Length >= 7 && r[5] != 0;

    /// <summary>Réponse « batterie » : (%, en charge, mV) ; null si pas connectée ou niveau hors 1..100.</summary>
    public static (int Percent, bool Charging, int Millivolts)? ParseBattery(byte[] r) =>
        r.Length < 10 || r[5] != 0x02 || r[6] is < 1 or > 100 ? null : (r[6], r[7] != 0, (r[8] << 8) | r[9]);

    static byte[]? Query(HidDev d, byte cmd, int deadlineMs)
    {
        var req = BuildQuery(cmd);
        long end = Environment.TickCount64 + deadlineMs;
        d.Flush();
        for (; ; )
        {
            long left = end - Environment.TickCount64;
            if (left <= 0) return null;
            if (!d.Write(req)) return null;
            var r = d.Read((int)Math.Min(left, 800));
            if (r != null && IsValidReply(r, cmd)) return r;
        }
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x3554))
        {
            if (pid != 0xF512) continue; // Chimera V2 seulement
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF02 && c.Usage == 0x0002 && c.InLen >= 17 && c.OutLen >= 17))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                // On réveille l'appareil par la requête d'état en ligne, puis on lit la batterie.
                if (Query(d, 0x03, 800) is not { } on || !IsOnline(on)) continue;
                if (Query(d, 0x04, 800) is not { } r || ParseBattery(r) is not { } b) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "chimera:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = "Chimera V2", Kind = DeviceKind.Mouse,
                    Via = "2.4 GHz receiver", Percent = b.Percent, Charging = b.Charging,
                }, vid, pid);
                break;
            }
        }
    }
}
