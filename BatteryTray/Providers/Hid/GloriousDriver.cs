// Glorious, Hitscan, G-Wolves et WLMouse : souris sans fil au même protocole fournisseur (G-Wolves l'appelle « XVI »).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/glorious.cpp, porté en C# — voir NOTICE.md.
// Requête : rapport de fonctionnalité de 64 octets (id 0), octets [3]=02 [4]=02 [6]=83.
// Réponse : [1]=état, [4]=02, [6]=83, [7]=charge (1), [8]=pourcentage.
// État A1 = éveillée, A4 = en veille, A0 = se réveille, autre = injoignable.
// Sources citées par BatteryHub : github.com/ikajdan/glorious-indicator, github.com/louis4craft/glorious-ctl, configurateurs web
// de Hitscan (control.hitscan.com) et de G-Wolves (mouse.xyz). Le récepteur 1K de WLMouse est partagé avec d'autres modèles :
// seuls le récepteur YING 8K et sa collection batterie sont reconnus ici.
namespace BatteryTray.Providers.Hid;

public static class GloriousDriver
{
    // Noms de modèles et identifiants des câbles, d'après la liste d'appareils de G-Wolves (mouse.xyz).
    public static string GWolvesModelName(ushort pid) => pid switch
    {
        0x3808 or 0x3817 => "G-Wolves HTM Plus",
        0x6808 or 0x6817 => "G-Wolves HSK Pro 2.0",
        0x5608 or 0x5617 or 0x5618 or 0x5619 => "G-Wolves HTXU",
        0x3608 or 0x3617 or 0x3619 => "G-Wolves Fenrir Pro",
        0x3908 or 0x3917 => "G-Wolves VUK",
        0x7904 or 0x7913 => "G-Wolves HT-S2",
        0x3708 or 0x3717 => "G-Wolves Fenrir Max",
        0x5308 or 0x5317 => "G-Wolves HTS Ultra",
        0x7704 or 0x7713 => "G-Wolves HTR",
        0x3508 or 0x3517 => "G-Wolves Fenrir",
        0x7908 or 0x7917 => "G-Wolves HT-S2 Pro",
        0x5808 or 0x5817 => "G-Wolves HSK Pro",
        0x2708 or 0x2717 or 0x2719 => "G-Wolves HTX Mini",
        0x5408 or 0x5417 or 0x5418 or 0x5419 => "G-Wolves HTS Plus",
        0x5708 or 0x5717 => "G-Wolves HTX",
        0x5908 or 0x5917 => "G-Wolves HSK Plus",
        0x7204 or 0x7203 => "G-Wolves HSK Lite",
        0x7708 or 0x7717 => "G-Wolves HTR Pro",
        0x5804 or 0x5803 => "G-Wolves HSK Pro ACE",
        0x5404 or 0x5403 => "G-Wolves HTS Plus ACE",
        0x5704 or 0x5703 => "G-Wolves HTX ACE",
        0x5904 or 0x5903 => "G-Wolves HSK Plus ACE",
        0x4718 or 0x4719 => "G-Wolves Lycan",
        0x4219 => "G-Wolves WARG",
        0x5219 => "G-Wolves HTS Plus Pro",
        0x3519 => "G-Wolves Fenrir Asym",
        0x2619 => "G-Wolves HTX Mini Asym",
        0x1619 => "G-Wolves Fenrir Max Asym",
        0x2119 => "G-Wolves VUK Mini",
        _ => "G-Wolves wireless mouse",
    };

    static readonly HashSet<ushort> GWolvesWired = new()
    {
        0x1619, 0x2119, 0x2619, 0x2708, 0x2719, 0x3508, 0x3519, 0x3608, 0x3619, 0x3708, 0x3808, 0x3908,
        0x4219, 0x4718, 0x4719, 0x5219, 0x5308, 0x5404, 0x5408, 0x5418, 0x5419, 0x5608, 0x5618, 0x5619,
        0x5704, 0x5708, 0x5804, 0x5808, 0x5904, 0x5908, 0x6808, 0x7204, 0x7704, 0x7708, 0x7904, 0x7908,
    };
    public static bool GWolvesIsCable(ushort pid) => GWolvesWired.Contains(pid);

    static string? GloriousName(ushort pid) => pid switch
    {
        0x2022 or 0x2011 => "Glorious Model O Wireless",
        0x2023 or 0x2012 => "Glorious Model D Wireless",
        0x2024 or 0x2013 => "Glorious Model O- Wireless",
        0x2025 or 0x2014 => "Glorious Model D- Wireless",
        0x2033 => "Glorious Model O 2 Wireless",
        0x2034 or 0x2036 or 0x201A => "Glorious Model D 2 PRO",
        _ => null, // une autre marque sur la même puce Sinowealth (ex. claviers Redragon)
    };
    static bool GloriousCable(ushort pid) => pid is 0x2011 or 0x2012 or 0x2013 or 0x2014 or 0x201A;

    static string? HitscanName(ushort pid) => pid switch { 0x0136 => "Hitscan Hyperlight X", 0x0194 => "Hitscan Hyperlight N", _ => "Hitscan Hyperlight" };
    static bool HitscanCable(ushort pid) => pid is 0x0136 or 0x0194;

    sealed record Vendor(ushort Vid, string KeyPrefix, int ReplyDelayMs, Func<ushort, string?> Name, Func<ushort, bool> IsCable, Func<HidColl, bool>? Matches = null);

    static readonly Vendor[] Vendors =
    {
        new(0x258A, "glorious:", 50, GloriousName, GloriousCable),
        new(0x3770, "hitscan:", 200, HitscanName, HitscanCable),
        new(0x33E4, "gwolves:", 100, p => GWolvesModelName(p), GWolvesIsCable),
        new(0x36A7, "wlmouse:", 100, _ => "WLMouse YING", _ => false, c => c.Pid == 0xA874 && c.Page == 0xFFFF && c.Usage == 0),
    };

    public enum Awake { Invalid, WakingUp, Awake, Asleep }

    /// <summary>Lit une réponse (octets de rapport de fonctionnalité, [0] = identifiant). Logique pure, testée.</summary>
    public static (Awake State, bool Charging, int Percent) Parse(byte[] r)
    {
        if (r.Length < 9 || r[6] != 0x83 || (r[1] & 0xF0) != 0xA0) return (Awake.Invalid, false, -1);
        bool charging = r[7] == 0x01;
        if (r[1] == 0xA0) return (Awake.WakingUp, charging, -1);
        if (r[1] == 0xA1 && r[8] <= 100) return (Awake.Awake, charging, HidUtil.Clamp100(r[8] != 0 ? r[8] : 1));
        return (Awake.Asleep, charging, -1);
    }

    /// <summary>La requête de batterie : 65 octets (identifiant 0 puis 64 octets), [3]=02 [4]=02 [6]=83.</summary>
    public static byte[] BuildRequest()
    {
        var w = new byte[65];
        w[3] = 0x02; w[4] = 0x02; w[6] = 0x83;
        return w;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var v in Vendors)
        {
            foreach (var (vid, pid) in ctx.Products(v.Vid))
            {
                foreach (var c in ctx.Find(vid, pid, c => c.FeatLen == 65 && (v.Matches == null || v.Matches(c))))
                {
                    using var d = new HidDev();
                    if (!d.OpenFeatureOnly(c)) continue;
                    byte[]? r = null; bool ok = false;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        if (!d.SetFeature(BuildRequest())) break;
                        Thread.Sleep(v.ReplyDelayMs);
                        if (!d.GetFeature(0, out r) || r.Length < 9) break;
                        var parsed = Parse(r);
                        if (parsed.State == Awake.Invalid) break;                                  // pas ce protocole
                        if (parsed.State == Awake.WakingUp && attempt == 0) { Thread.Sleep(400); continue; } // se réveille : on redemande
                        ok = true;
                        break;
                    }
                    if (!ok || r == null) continue;

                    var (state, charging, percent) = Parse(r);
                    string? known = v.Name(pid);
                    bool cable = v.IsCable(pid);
                    var s = new DeviceStatus
                    {
                        Key = v.KeyPrefix + HidUtil.Hex4(pid),
                        Name = known ?? (string.IsNullOrWhiteSpace(c.Product) ? "Wireless device" : c.Product),
                        Kind = known != null ? DeviceKind.Mouse : SinowealthKind(ctx.Colls, vid, pid, c.Product),
                        Via = cable ? "USB cable" : "2.4 GHz receiver",
                        Charging = cable || charging,
                        Percent = state == Awake.Awake ? percent : -1,
                        Connected = state == Awake.Awake,
                    };
                    ctx.Add(s, vid, pid);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 258A est l'identifiant Sinowealth, partagé par beaucoup de marques : pour un produit inconnu, on déduit clavier ou souris
    /// d'après le nom puis d'après les collections exposées (un clavier expose deux collections clavier : boot + NKRO).
    /// </summary>
    public static DeviceKind SinowealthKind(IReadOnlyList<HidColl> all, ushort vid, ushort pid, string? name)
    {
        var kind = DeviceNames.Guess(name);
        if (kind != DeviceKind.Other) return kind;
        int keyboards = all.Count(c => c.Vid == vid && c.Pid == pid && c.Page == 0x01 && c.Usage == 0x06);
        if (keyboards >= 2) return DeviceKind.Keyboard;
        return all.Any(c => c.Vid == vid && c.Pid == pid && c.Page == 0x01 && c.Usage == 0x02) ? DeviceKind.Mouse
             : all.Any(c => c.Vid == vid && c.Pid == pid && c.Page == 0x01 && c.Usage == 0x05) ? DeviceKind.Gamepad
             : keyboards >= 1 ? DeviceKind.Keyboard : DeviceKind.Mouse;
    }
}
