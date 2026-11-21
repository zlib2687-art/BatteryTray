// Fantech Raigor III, Raigor II WG10 et autres souris 2,4 GHz bon marché sur le récepteur Sharkoon réutilisé 1EA7:0066 / 0064.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/raigor.cpp, porté en C# — voir NOTICE.md.
// Des propriétaires des deux Raigor ont signalé la même collection FFB5:0001 et les mêmes identifiants (issues #2891, #411 et #1312 de BatteryHub).
// La batterie est dans la collection vendeur FFB5:0001, identifiant de rapport B5, rapports de 8 octets en entrée / sortie :
//   sortie [B5 05 00 00 00 00 00 00] → le récepteur diffuse sa page d'état de 256 octets en 64 trames [B5 05 adr 04 d0 d1 d2 d3], adr = 00, 04… FC ; le morceau 94 contient [id3, état, %].
//   le récepteur pousse aussi [B5 67 id[4] état niveau] au réveil / à la reliaison / au battement.
// La page répond même souris endormie mais garde le dernier rapport radio : une souris endormie garde sa dernière valeur. Ce micrologiciel ne signale pas la charge.
// Protocole : github.com/tuttizpro218/t20-battery (docs/PROTOCOL.md), vérifié sur un récepteur Raigor III 1EA7:0066 (morceau 94 = 35 09 40 38 → 64 %).
namespace BatteryTray.Providers.Hid;

public static class RaigorDriver
{
    public static byte[] BuildRequest() => new byte[] { 0xB5, 0x05, 0, 0, 0, 0, 0, 0 };

    /// <summary>Trame de 8 octets : pourcentage (morceau d'état 0x94 ou annonce 0x67), ou null.</summary>
    public static int? ParseFrame(byte[] r)
    {
        if (r.Length != 8 || r[0] != 0xB5) return null;
        if (r[1] == 0x05 && r[2] == 0x94 && r[3] == 0x04 && r[6] <= 100) return r[6];
        if (r[1] == 0x67 && r[7] <= 100) return r[7];
        return null;
    }

    static int QueryBattery(HidDev d)
    {
        d.Flush();
        if (!d.Write(BuildRequest())) return -1;
        long end = Environment.TickCount64 + 1200;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) break;
            var r = d.Read(Math.Min(left, 200));
            if (r == null) break;
            if (ParseFrame(r) is { } p) return p;
        }
        return -1;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x1EA7))
        {
            if (pid != 0x0066 && pid != 0x0064) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFFB5 && c.Usage == 0x0001 && c.InLen == 8 && c.OutLen == 8))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                int pct = QueryBattery(d);
                if (pct < 0) continue;
                var kind = HidUtil.KindFromCollections(ctx.Colls, vid, pid);
                ctx.Add(new DeviceStatus
                {
                    Key = "raigor:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = c.Product.Length > 0 ? c.Product : "2.4 GHz mouse",
                    Kind = kind == DeviceKind.Other ? DeviceKind.Mouse : kind, Via = "2.4 GHz receiver", Percent = pct,
                }, vid, pid);
                break;
            }
        }
    }
}
