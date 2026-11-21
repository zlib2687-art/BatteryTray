// NuPhy WH80 en USB (19F5:A011), testé sur matériel.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/nuphy.cpp, porté en C# — voir NOTICE.md.
// Source : SDK NuPhyIO (drive.nuphy.io), getBatteryState(0) : GetFunc (05), décalage 0020, un octet. Seul ce GET est envoyé.
// Rapports de 64 octets non numérotés, usage 0001:0000, interface 1 :
//   55 05 00 21 01 20 00 00… → AA 05 00 somme 01 20 00 00 valeur…
// Somme = octet de poids faible de la somme des octets 4..63. Valeur : bit 7 en charge, bits 0..6 pourcentage (le WH80 donne des paliers de 10 %). Bluetooth / récepteur non testés.
namespace BatteryTray.Providers.Hid;

public static class NuphyDriver
{
    const ushort Vid = 0x19F5, Pid = 0xA011;

    public static byte[] BuildRequest() => new byte[] { 0, 0x55, 0x05, 0, 0x21, 1, 0x20, 0, 0 };

    public static (int Percent, bool Charging)? ParseReply(byte[] r)
    {
        if (r.Length != 64 || r[0] != 0xAA || r[1] != 0x05 || r[2] != 0 || r[4] != 1 || r[5] != 0x20 || r[6] != 0 || r[7] != 0) return null;
        int sum = 0; for (int i = 4; i < r.Length; i++) sum += r[i];
        if ((sum & 0xFF) != r[3] || (r[8] & 0x7F) > 100) return null;
        return (r[8] & 0x7F, (r[8] & 0x80) != 0);
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Pid)) return;
        foreach (var c in ctx.Find(Vid, Pid, c => c.Iface == 1 && c.Page == 1 && c.Usage == 0 && c.InLen == 65 && c.OutLen == 65))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(BuildRequest(), 400)) continue;
            long end = Environment.TickCount64 + 400;
            for (; ; )
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = d.Read(left);
                if (r == null) break;
                if (ParseReply(r) is not { } p) continue;
                ctx.Add(new DeviceStatus { Key = "nuphy:WH80", Name = "NuPhy WH80", Kind = DeviceKind.Keyboard, Via = "USB cable", Percent = p.Percent, Charging = p.Charging }, Vid, Pid);
                return;
            }
        }
    }
}
