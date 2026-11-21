// Souris MCHOSE A5 de première génération (VID 2023) : A5 Pro et A5 Pro Max. Antérieures à M HUB, elles utilisent une autre puce et un autre protocole que les familles 5253 / 3837 de MchoseDriver :
//   2023:F013 récepteur 1K 2,4 GHz (partagé par A5 Pro et A5 Pro Max ; chaîne produit « MCHOSE A5 2.4G ») ; 2023:F015 récepteur 4K (partagé aussi) ; 2023:F017 A5 Pro sur câble ;
//   2023:F018 récepteur 1K de l'A5 Pro Max (table Klegus/mchose-macos) ; 2023:F019 A5 Pro Max sur câble.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/mchose_a5.cpp, porté en C# — voir NOTICE.md.
// Transport : rapports de fonctionnalité de 64 octets (65 avec l'identifiant) sur la collection vendeur, page FFFF (interface 2), identifiant 0. Seule la lecture de la batterie est envoyée :
//   requête : [00 identifiant][00 00][route 02 = souris][long 02][page 00][cmd 83][00…] ; réponse relue par un get-feature sur l'identifiant 0 : [00][A1 succès][..][..][..][page 00][cmd 83][charge][niveau]
//   (0xA1 = OK, 0xA3 = non pris en charge ; page et commande en écho). La souris ne répond que si elle est éveillée : la référence écrit une fois, attend 34 ms, relit jusqu'à huit fois avec un renvoi au milieu ;
//   un récepteur muet signifie souris endormie ou éteinte.
// Sources : z750sasr/mchose-a5-pro-max-web-driver (MIT, docs/PROTOCOL.md), HeyOkay/HaloBattery (MIT, providers/mchose.py : commande 0x83 page 0, charge à l'octet 6 de la réponse et niveau à l'octet 7, après le marqueur A1 ; confirmé sur
// un A5 Pro Max sur le récepteur F013, issue #149), Klegus/mchose-macos (MIT, table d'identifiants ; testé sur un A5 Pro avec le récepteur 4K), OpenMouse-Project/mouse-protocol (docs/mchose-a5-gen1.md).
// Non testé ici sur matériel réel. F017 et F018 viennent de la seule table Klegus. Le VID 2023 n'est utilisé par aucun autre pilote (0x2023 de Glorious est un identifiant de produit sous le VID 258A).
namespace BatteryTray.Providers.Hid;

public static class MchoseA5Driver
{
    const ushort Vid = 0x2023;
    const byte Ok = 0xA1, Page = 0x00, CmdBattery = 0x83;

    public sealed record Model(ushort Pid, string Name, bool Cable);
    public static readonly Model[] Models =
    {
        new(0xF013, "MCHOSE A5 Pro / Pro Max", false), new(0xF015, "MCHOSE A5 Pro / Pro Max", false), new(0xF017, "MCHOSE A5 Pro", true),
        new(0xF018, "MCHOSE A5 Pro Max", false), new(0xF019, "MCHOSE A5 Pro Max", true),
    };

    public static byte[] Request(int len) { var r = new byte[Math.Max(len, 9)]; r[3] = 0x02; r[4] = 2; r[5] = Page; r[6] = CmdBattery; return r; }

    /// <summary>r[0] est l'identifiant de rapport sous Windows ; le marqueur A1 suit. Renvoie (niveau, en charge) pour une réponse batterie.</summary>
    public static (int Level, bool Charging)? Parse(byte[] r)
    {
        if (r.Length < 9) return null;
        if (r[0] != Ok && r[1] != Ok) return null;
        int at = r[1] == Ok ? 1 : 0;
        if (r.Length <= at + 7 || r[at + 4] != Page || r[at + 5] != CmdBattery) return null;
        byte lv = r[at + 7];
        return lv == 0xFF ? null : (lv > 100 ? 100 : lv, r[at + 6] != 0); // la référence borne à 100
    }

    // 1 = batterie lue, 0 = la collection fonctionne mais la souris n'a pas répondu, -1 = impossible de lui parler.
    static (int State, int Level, bool Charging) Query(HidColl c)
    {
        using var d = new HidDev();
        if (!d.OpenFeatureOnly(c)) return (-1, 0, false);
        var req = Request(c.FeatLen);
        if (!d.SetFeature(req)) return (-1, 0, false);
        Thread.Sleep(34);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (attempt == 3) { if (!d.SetFeature(req)) return (-1, 0, false); Thread.Sleep(34); }
            if (d.GetFeature(0x00, out var r) && Parse(r) is { } p) return (1, p.Level, p.Charging);
            Thread.Sleep(30);
        }
        return (0, 0, false);
    }

    static readonly HashSet<ushort> Answered = new(); // un récepteur muet n'est montré « endormi » qu'une fois que sa souris a répondu

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            var colls = ctx.Find(Vid, m.Pid, c => c.Page == 0xFFFF && c.FeatLen >= 65).OrderByDescending(c => c.Iface == 2).ToList(); // l'interface 2 d'abord : c'est là que parlent les références
            int state = -1, level = 0; bool charging = false;
            foreach (var c in colls)
            {
                var q = Query(c);
                if (q.State > state) { state = q.State; level = q.Level; charging = q.Charging; }
                if (q.State == 1) break;
            }
            if (state < 0) continue;
            var s = new DeviceStatus { Key = "mchose-a5:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Mouse, Via = m.Cable ? "USB cable" : "2.4 GHz receiver" };
            if (state == 1) { Answered.Add(m.Pid); s.Percent = level; s.Charging = charging; }
            else { if (m.Cable || !Answered.Contains(m.Pid)) continue; s.Connected = false; }
            ctx.Add(s, Vid, m.Pid);
        }
    }
}
