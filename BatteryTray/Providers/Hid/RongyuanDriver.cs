// Récepteurs 2,4 GHz Rongyuan, utilisés par Attack Shark, Ajazz et de nombreuses autres souris et claviers (et certains appareils MSI). Un rapport de fonctionnalité renvoie à la fois l'emplacement clavier et souris :
//   requête [00 F7 00…] sur l'interface 2, collection vendeur FFFF:0002 ; réponse [00][ok][% clavier][% souris][clavier hors ligne][souris hors ligne]…
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/rongyuan.cpp, porté en C# — voir NOTICE.md.
// Liste des récepteurs et protocole : qu1ncky/BatteryTray (RongyuanDongleProvider, MIT), tirés du pilote web officiel ATTACK SHARK. Le rapport d'état ne porte pas de nom de modèle : les noms viennent de la chaîne produit
// du récepteur sans les mots de liaison (« AJAZZ 2.4G 8K » → « AJAZZ wireless mouse »). 3151:402D est partagé par plusieurs souris (Attack Shark R3, AJAZZ AJ179 Pro…) : on ne l'appelle « Attack Shark R3 » que si la chaîne
// produit ne nomme aucune autre marque.
namespace BatteryTray.Providers.Hid;

public static class RongyuanDriver
{
    static readonly HashSet<uint> Receivers = new()
    {
        0x0DB0197E, 0x0DB08E9A, 0x145F0376, 0x145F038D, 0x14A5C045, 0x25AA2008, 0x25AA4008, 0x25AA4018, 0x31514011, 0x31514014, 0x31514017, 0x31514021, 0x31514027, 0x3151402D, 0x31514038, 0x31515006, 0x31515007,
        0x31515008, 0x31515020, 0x31515026, 0x3151502B, 0x31515031, 0x31515037, 0x31515038, 0x3151503D, 0x3151503F, 0x31515040, 0x31515042, 0x31515045, 0x3151504B, 0x3151504E, 0x31515050, 0x31515052, 0x31515055,
        0x3151505A, 0x342DE3E0, 0x342DE3E1, 0x342DE3E2, 0x342DE3E6, 0x342DE3E7, 0x342DE3E8, 0x342DE3F5, 0x342DE3F6, 0x342DE3F7, 0x342DE3FB, 0x342DE3FC, 0x342DE3FD, 0x347AA302, 0x347AA305, 0x374AA205, 0x374AA217,
        0x374AA220, 0x374AA223, 0x374AA226, 0x374AA229, 0x374AA234, 0x374AA237, 0x374AA251, 0x374AA263, 0x374AA268, 0x374AA311, 0x374AA314, 0x374AA319, 0x374AA322, 0x374AA329, 0x374AA355, 0x379A1804, 0x379A1807,
        0x38A90002, 0x38EE0000, 0x38EE0002, 0x38EE0039, 0x391D1A04, 0x391D1A08, 0x391D1A0E, 0x391D1A13, 0x391D1A25, 0x391D1A2B, 0x391D1A34, 0x391D2017, 0x391D2A05,
    };

    public static bool IsReceiver(ushort vid, ushort pid) => Receivers.Contains(((uint)vid << 16) | pid);

    static readonly HashSet<string> LinkWords = new(StringComparer.OrdinalIgnoreCase) { "2.4G", "2.4GHZ", "8K", "4K", "2K", "1K", "RECEIVER", "WIRELESS", "DONGLE", "USB" };

    /// <summary>Chaîne produit sans mots de liaison : « AJAZZ 2.4G 8K » → « AJAZZ », « 2.4G Wireless Receiver » → « ».</summary>
    public static string Brand(string product) =>
        string.Join(" ", product.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !LinkWords.Contains(w)));

    public static byte[] BuildQuery(int featLen) { var c = new byte[Math.Max(featLen, 7)]; c[1] = 0xF7; return c; }

    public static bool IsValidStatus(byte[] r) => r.Length >= 7 && (r[2] | r[3] | r[4] | r[5] | r[6]) != 0;

    /// <summary>Nom d'un emplacement d'après le récepteur et la chaîne produit.</summary>
    public static string SlotName(ushort vid, ushort pid, bool keyboard, string product)
    {
        bool generic = product.Length == 0 || product.Contains("Receiver", StringComparison.Ordinal) || product.Contains("2.4G", StringComparison.Ordinal);
        string brand = Brand(product);
        bool otherBrand = brand.Length > 0 && !brand.Contains("ATTACK SHARK", StringComparison.OrdinalIgnoreCase);
        if (vid == 0x3151 && pid == 0x402D && !keyboard && !otherBrand) return "Attack Shark R3";
        if (vid == 0x3151 && pid == 0x5038 && keyboard) return "MonsGeek keyboard"; // le récepteur dit « MonsGeek 2.4G Wireless Keyboard »
        if (generic && brand.Length > 0) return brand + (keyboard ? " wireless keyboard" : " wireless mouse");
        if (vid == 0x3151 && pid == 0x5006 && keyboard && product.Contains("Akko", StringComparison.Ordinal)) return "Akko Air 01";
        if (generic) return keyboard ? "Wireless keyboard" : "Wireless mouse";
        return product;
    }

    static byte[]? QueryStatus(HidDev d)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(100);
            if (!d.SetFeature(BuildQuery(d.Coll.FeatLen))) return null;
            Thread.Sleep(20);
            if (d.GetFeature(0, out var r) && IsValidStatus(r)) return r;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        var products = ctx.Colls.Where(c => IsReceiver(c.Vid, c.Pid) && !ctx.IsClaimed(c.Vid, c.Pid)).Select(c => (c.Vid, c.Pid)).Distinct().ToList();
        foreach (var (vid, pid) in products)
        {
            foreach (var c in ctx.Find(vid, pid, c => c.Iface == 2 && c.Page == 0xFFFF && c.Usage == 0x0002 && c.FeatLen >= 7))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                if (QueryStatus(d) is not { } r) continue;
                // Disposition des emplacements : [2] % clavier, [3] % souris, [4] clavier hors ligne, [5] souris hors ligne.
                for (int slot = 0; slot < 2; slot++)
                {
                    bool keyboard = slot == 0;
                    if (r[keyboard ? 4 : 5] != 0) continue; // rien de connecté dans cet emplacement
                    byte level = r[keyboard ? 2 : 3];
                    var s = new DeviceStatus
                    {
                        Key = "rongyuan:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid) + (keyboard ? ":kb" : ":ms"), Name = SlotName(vid, pid, keyboard, c.Product),
                        Kind = keyboard ? DeviceKind.Keyboard : DeviceKind.Mouse, Via = "2.4 GHz receiver", Vid = vid,
                    };
                    if (level is >= 1 and <= 100) s.Percent = level;
                    ctx.Out.Add(s);
                }
                ctx.Claim(vid, pid);
                break;
            }
        }
    }
}
