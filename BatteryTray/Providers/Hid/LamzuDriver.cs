// LAMZU : souris sans fil sur leurs propres identifiants 373E et 37B0 (Maya X, Maya, Tachi, Inca, Paro, Thorn, Atlantis Mini / OG Champion, Orcus…).
// L'Atlantis sur Compx 3554 utilise le pilote Kysona. Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/lamzu.cpp, porté en C# — voir NOTICE.md.
// Protocole du pilote web Aurora de LAMZU (lamzu.net, modèles « IsNewProtocol ») :
//   SET_FEATURE rapport 0, 64 octets : [00][00][02][02][00][83][00 …]
//   ~100 ms plus tard GET_FEATURE rapport 0 : [A1][..][..][02][..][83][charge][pourcentage] après l'octet d'identifiant
//   (le pilote web accepte la réponse avec ou sans cet octet, nous aussi).
//   charge 1 = sur le câble ; pourcentage 0 = la souris n'est pas liée au récepteur.
// Identifiants de modèles : lamzu.net/Config/env-models.json. Plusieurs modèles récents partagent un identifiant de récepteur : nom générique.
// Lecture seule.
namespace BatteryTray.Providers.Hid;

public static class LamzuDriver
{
    sealed record Model(ushort Vid, ushort[] Pids, string Name);

    static readonly Model[] Models =
    {
        new(0x373E, new ushort[] { 0x001C, 0x001D, 0x001E }, "LAMZU Maya X"),
        new(0x373E, new ushort[] { 0x0016 }, "LAMZU Maya X V2"),
        new(0x37B0, new ushort[] { 0x001F }, "LAMZU Maya X V2"),
        new(0x37B0, new ushort[] { 0x0005, 0x000B, 0x000C }, "LAMZU Tachi"),
        new(0x37B0, new ushort[] { 0x001C, 0x001E }, "LAMZU Tachi Lite"),
        new(0x37B0, new ushort[] { 0x0009, 0x000F, 0x0010 }, "LAMZU Inca"),
        new(0x37B0, new ushort[] { 0x0011, 0x0013, 0x0015 }, "LAMZU Maya"),
        new(0x37B0, new ushort[] { 0x0007, 0x000D, 0x000E }, "LAMZU Paro"),
        new(0x37B0, new ushort[] { 0x0017, 0x0019, 0x001B }, "LAMZU Thorn"),
        new(0x37B0, new ushort[] { 0x0021, 0x0003, 0x0023 }, "LAMZU Thorn V2"),
        new(0x37B0, new ushort[] { 0x0030, 0x0032, 0x0040 }, "LAMZU Thorn V2"),
        new(0x37B0, new ushort[] { 0x0025, 0x0027 }, "LAMZU Atlantis OG Champion"),
        new(0x37B0, new ushort[] { 0x0028, 0x002B, 0x002A }, "LAMZU Atlantis Mini"),
        new(0x37B0, new ushort[] { 0x003E, 0x003F }, "LAMZU Atlantis Mini"),
        new(0x37B0, new ushort[] { 0x002C, 0x0052 }, "LAMZU Maya M"),
        new(0x37B0, new ushort[] { 0x003A, 0x003B, 0x0044 }, "LAMZU Maya X"),
        new(0x37B0, new ushort[] { 0x003C, 0x0050 }, "LAMZU Maya"),
        new(0x37B0, new ushort[] { 0x0042, 0x0056 }, "LAMZU Orcus"),
        new(0x37B0, new ushort[] { 0x0046, 0x0054 }, "LAMZU Mini"),
    };

    public static string? ModelName(ushort vid, ushort pid) => Models.FirstOrDefault(m => m.Vid == vid && m.Pids.Contains(pid))?.Name;

    static readonly HashSet<ushort> WiredPids = new()
    {
        0x0005, 0x0007, 0x0009, 0x0011, 0x0017, 0x001C, 0x001F, 0x0021, 0x0025, 0x0028, 0x002C, 0x0030, 0x0034, 0x0038, 0x003A, 0x003C,
        0x003E, 0x0040, 0x0042, 0x0044, 0x0046, 0x0050, 0x0052, 0x0056,
    };

    /// <summary>Identifiants filaires : la souris elle-même sur le câble USB.</summary>
    public static bool IsCable(ushort vid, ushort pid) => vid == 0x373E ? pid == 0x001C : WiredPids.Contains(pid);

    public static byte[] BuildRequest()
    {
        var req = new byte[65]; // [0] = identifiant de rapport 0
        req[3] = 0x02; req[4] = 0x02; req[6] = 0x83;
        return req;
    }

    /// <summary>Lit la réponse (avec ou sans l'octet d'identifiant) : (charge, pourcentage) ou null si ce n'est pas ce protocole / niveau invalide.</summary>
    public static (int Charging, int Percent)? ParseReply(byte[] r)
    {
        if (r.Length < 10) return null;
        int chg, pct;
        if (r[1] == 0xA1 && r[4] == 0x02 && r[6] == 0x83) { chg = r[7]; pct = r[8]; }
        else if (r[0] == 0xA1 && r[3] == 0x02 && r[5] == 0x83) { chg = r[6]; pct = r[7]; }
        else return null;
        return pct > 100 ? null : (chg, pct);
    }

    public static DeviceStatus ToStatus(ushort vid, ushort pid, int charging, int percent)
    {
        bool cable = IsCable(vid, pid);
        return new DeviceStatus
        {
            Key = $"lamzu:{HidUtil.Hex4(vid)}:{HidUtil.Hex4(pid)}",
            Name = ModelName(vid, pid) ?? "LAMZU wireless mouse",
            Kind = DeviceKind.Mouse,
            Via = cable ? "USB cable" : "2.4 GHz receiver",
            Connected = percent > 0 || cable, // 0 depuis un récepteur = souris éteinte ou hors de portée
            Percent = percent > 0 ? percent : -1,
            Charging = charging == 1,
        };
    }

    public static void Poll(PollContext ctx)
    {
        foreach (ushort vendor in new ushort[] { 0x373E, 0x37B0 })
        {
            foreach (var (vid, pid) in ctx.Products(vendor))
            {
                foreach (var c in ctx.Find(vid, pid, c => c.FeatLen == 65))
                {
                    using var d = new HidDev();
                    if (!d.OpenFeatureOnly(c)) continue;
                    if (!d.SetFeature(BuildRequest())) continue;
                    Thread.Sleep(100);
                    if (!d.GetFeature(0x00, out var r)) continue;
                    var parsed = ParseReply(r);
                    if (parsed == null) continue;
                    ctx.Add(ToStatus(vid, pid, parsed.Value.Charging, parsed.Value.Percent), vid, pid);
                    break;
                }
            }
        }
    }
}
