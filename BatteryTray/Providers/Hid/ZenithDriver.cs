// Souris Zenith Pro (260D:1114 récepteur, 260D:1143 câble ; logiciel vendeur « Segment »).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/zenith.cpp, porté en C# — voir NOTICE.md.
// Observé sur matériel : on écrit un rapport de 17 octets sur la collection FF02:0002, [08][04][0 × 14][somme = 0x55 − somme(octets 0..15)] ; la souris répond par 17 octets :
// [08][04]… [6 = %][7 = en charge][8..9 = millivolts grand-boutiste][16 = somme : la somme des octets de tout le rapport vaut 0x55 modulo 256].
namespace BatteryTray.Providers.Hid;

public static class ZenithDriver
{
    const ushort Vid = 0x260D;

    public static byte[] BuildRequest() { var r = new byte[17]; r[0] = 0x08; r[1] = 0x04; r[16] = 0x49; return r; }

    public static (int Percent, bool Charging, int Millivolts)? ParseReply(byte[] r)
    {
        if (r.Length != 17 || r[0] != 0x08 || r[1] != 0x04 || r[6] > 100) return null;
        byte sum = 0; foreach (var b in r) sum += b;
        return sum != 0x55 ? null : (r[6], r[7] != 0, (r[8] << 8) | r[9]);
    }

    static void PollPid(PollContext ctx, ushort pid, string via)
    {
        if (ctx.IsClaimed(Vid, pid)) return;
        var colls = ctx.Find(Vid, pid, c => c.Iface == 1 && c.Page == 0xFF02 && c.Usage == 2 && c.InLen == 17 && c.OutLen == 17);
        if (colls.Count != 1) return;
        using var dev = new HidDev();
        if (!dev.Open(colls[0])) return;
        dev.Flush();
        if (!dev.Write(BuildRequest())) return;
        long end = Environment.TickCount64 + 1000;
        for (int reports = 0; reports < 32; reports++)
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return;
            var r = dev.Read(left);
            if (r == null) return;
            if (ParseReply(r) is not { } p) continue;
            ctx.Add(new DeviceStatus { Key = "zenith:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(pid), Name = "Zenith Pro", Kind = DeviceKind.Mouse, Via = via, Percent = p.Percent, Charging = p.Charging }, Vid, pid);
            return;
        }
    }

    public static void Poll(PollContext ctx) { PollPid(ctx, 0x1114, "2.4 GHz receiver"); PollPid(ctx, 0x1143, "USB cable"); }
}
