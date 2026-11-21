// Souris sans fil Endgame Gear 4K (OP1w 4k v1/v2, XM2w 4k v1/v2) sur le récepteur 4K partagé 3367:1970.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/endgame.cpp, porté en C# — voir NOTICE.md.
// Protocole de github.com/johanneszab/vole (re/PROTOCOL.md, vérifié sur OP1w 4k v1 et v2, micrologiciel 1.01) et de
// github.com/Janlhin/Endgame_Gear_Web_Configurator :
//   Canal de configuration = collection vendeur FF01:0002, rapport de fonctionnalité A1 (64 octets, sans somme de contrôle) :
//     [A1][cmd][cible][longueur de charge][00…], charge à partir de l'octet 16.
//   SET_FEATURE de la requête, attente ~150 ms, GET_FEATURE du rapport A1. L'octet 1 de la réponse est l'état :
//     01 ok, 03 occupé (relire), 08 / 11 souris en veille ou hors de portée.
//   B4 (batterie) : réponse[16] = pourcentage (pas d'indicateur de charge). 0E (infos) : [16..17] = 67 33 (VID), [18..19] = PID de la souris (LE).
//   0F (sonde) : après une commande en échec le récepteur se tait jusqu'à ce qu'un 0F passe ; envoyée seulement en cas d'échec
//   (l'envoyer à chaque lecture empêcherait la souris de s'endormir).
// Seules ces commandes de lecture sont utilisées. La famille « WE » (XM2we / OP1we), au protocole de 17 octets, n'est pas prise en charge.
namespace BatteryTray.Providers.Hid;

public static class EndgameDriver
{
    const ushort Vid = 0x3367, Dongle4K = 0x1970;

    public static string? MouseName(ushort pid) => pid switch
    {
        0x1972 => "Endgame Gear OP1w 4k",
        0x1984 => "Endgame Gear OP1w 4k v2",
        0x1968 => "Endgame Gear XM2w 4k",
        0x1982 => "Endgame Gear XM2w 4k v2",
        _ => null,
    };

    public static byte[] BuildCommand(byte cmd, byte target) { var r = new byte[64]; r[0] = 0xA1; r[1] = cmd; r[2] = target; return r; }

    /// <summary>Réponse valide (rapport A1, ≥ 20 octets) → octet d'état ; null sinon.</summary>
    public static int? ReplyStatus(byte[] r) => r.Length >= 20 && r[0] == 0xA1 ? r[1] : null;

    /// <summary>Niveau d'une réponse B4 réussie, ou -1 s'il est impossible.</summary>
    public static int Level(byte[] r) => r.Length > 16 && r[16] <= 100 ? r[16] : -1;

    /// <summary>Nom de la souris d'après une réponse 0E ; null si ce n'est pas un appareil Endgame Gear connu.</summary>
    public static string? NameFromInfo(byte[] info) =>
        info.Length >= 20 && info[16] == 0x67 && info[17] == 0x33 ? MouseName((ushort)(info[18] | (info[19] << 8))) : null;

    static int Command(HidDev d, byte cmd, byte target, out byte[] r)
    {
        r = Array.Empty<byte>();
        if (!d.SetFeature(BuildCommand(cmd, target))) return -1;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(150 + attempt * 100);
            if (!d.GetFeature(0xA1, out r) || ReplyStatus(r) is not { } st) return -1;
            if (st != 0x03) return st; // 03 = occupé, relire
        }
        return 0x03;
    }

    // Par chemin de récepteur : la souris ne s'identifie que réveillée.
    static readonly Dictionary<string, string> LastName = new();

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, Dongle4K)) return;
        foreach (var c in ctx.Find(Vid, Dongle4K, c => c.Page == 0xFF01 && c.Usage == 0x0002 && c.FeatLen >= 64))
        {
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(c)) continue;
            int st = Command(d, 0xB4, 0x00, out var r);
            if (st != 0x01 && st != 0x08 && st != 0x11)
            {
                // Réponse inattendue ou absente : on sonde le récepteur puis la souris une fois, et on réessaie.
                Command(d, 0x0F, 0x01, out _);
                Command(d, 0x0F, 0x0F, out _);
                st = Command(d, 0xB4, 0x00, out r);
            }
            bool awake = st == 0x01 && Level(r) >= 0, asleep = st == 0x08 || st == 0x11;
            if (!awake && !asleep) continue;
            var s = new DeviceStatus { Key = "endgame:" + HidUtil.Hex4(Dongle4K), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Connected = !asleep };
            if (awake)
            {
                s.Percent = Level(r);
                if (Command(d, 0x0E, 0x00, out var info) == 0x01 && NameFromInfo(info) is { } n) LastName[c.Path] = n;
            }
            s.Name = LastName.TryGetValue(c.Path, out var known) ? known : "Endgame Gear 4k wireless mouse";
            ctx.Add(s, Vid, Dongle4K);
            break;
        }
    }
}
