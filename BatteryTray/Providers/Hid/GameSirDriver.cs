// Manettes GameSir (VID 3537) : Nova Lite / Nova 2 / Nova 2 Lite et Cyclone 2.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/gamesir.cpp, porté en C# — voir NOTICE.md.
// Nova (récepteur 2,4 GHz 3537:1098, Nova 2 Lite sur câble 100F, récepteur Nova Lite 2 1040 non testé) : XInput les déclare filaires, la batterie se lit donc sur la
// collection vendeur (page FF7A, rapports de 64 octets sans identifiant) avec la requête « infos appareil » du client GameSir :
//   requête 01 01 ; réponse [0] = 01, [1..5] adresse du récepteur, [10..11] micrologiciel, [18] batterie : 0..100 %, FF = en charge (pas de pourcentage sur câble).
//   Des trames d'état « F4 00 » non sollicitées arrivent sur la même collection et sont ignorées.
//   Références : github.com/WWWyyy452/GamesirBatteryTray et github.com/AJAY0406/Gamesir-Nova-2-Lite-Battery-Detection (MIT).
// Cyclone 2 (3537:100B, mode XInput, récepteur ou câble) : collection vendeur FFF0:0040 (rapports de 64 octets, ids 0F sortie / 12 entrée) :
//   requête 0F 03 (lecture seule, la manette émet alors ses trames d'état) ; réponse rapport 12 : [35] = câble / en charge, [36] = pourcentage.
//   Référence : github.com/vdemonchy/cyclone2-linux (docs/protocol.md), confirmé sur un vrai Cyclone 2.
namespace BatteryTray.Providers.Hid;

public static class GameSirDriver
{
    const ushort Vid = 0x3537;

    public static bool IsNova(ushort pid) => pid is 0x1098 or 0x100F or 0x1040;
    public static bool IsCyclone2(ushort pid) => pid == 0x100B;

    /// <summary>Réponse Nova : (pourcentage ou -1, en charge) ; null si ce n'est pas la réponse « infos » ou si l'octet est invalide.</summary>
    public static (int Percent, bool Charging)? ParseNova(byte[] r)
    {
        if (r.Length < 19 || r[0] != 0x01) return null;
        if (r[18] == 0xFF) return (-1, true); // en charge : le pourcentage reste inconnu, la dernière valeur est conservée
        return r[18] <= 100 ? (r[18], false) : null;
    }

    /// <summary>Rapport 12 du Cyclone 2 : (pourcentage, en charge) ; null pour les rapports d'événements 0x10 (sans batterie) ou un niveau invalide.</summary>
    public static (int Percent, bool Charging)? ParseCyclone(byte[] r) => r.Length < 37 || r[0] != 0x12 || r[36] > 100 ? null : (r[36], r[35] == 1);

    static void PollNova(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (!IsNova(pid)) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF7A && c.OutLen >= 3))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                if (!d.Write(0x00, 0x01, 0x01)) continue;
                (int Percent, bool Charging)? found = null;
                long end = Environment.TickCount64 + 800;
                while (found == null && end - Environment.TickCount64 > 0)
                {
                    var r = d.Read(200);
                    if (r == null || r.Length < 19) continue;
                    if (r[0] != 0x01) continue; // trame d'état (F4 00…) ou autre
                    found = ParseNova(r);
                    if (found == null) break;
                }
                if (found == null) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "gamesir:" + HidUtil.Hex4(pid), Name = "GameSir Nova", Kind = DeviceKind.Gamepad,
                    Via = pid == 0x100F ? "USB cable" : "2.4 GHz receiver", Percent = found.Value.Percent, Charging = found.Value.Charging,
                }, vid, pid);
                break;
            }
        }
    }

    static void PollCyclone(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            if (!IsCyclone2(pid)) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFFF0 && c.Usage == 0x0040 && c.OutLen >= 3 && c.InLen >= 37))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                if (!d.Write(0x0F, 0x03)) continue;
                (int Percent, bool Charging)? found = null;
                long end = Environment.TickCount64 + 1500;
                while (found == null && end - Environment.TickCount64 > 0)
                {
                    var r = d.Read(200);
                    if (r == null || r.Length < 37) continue;
                    found = ParseCyclone(r);
                }
                if (found == null) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "gamesir:" + HidUtil.Hex4(pid), Name = "GameSir Cyclone 2", Kind = DeviceKind.Gamepad,
                    Via = "2.4 GHz receiver or USB", Percent = found.Value.Percent, Charging = found.Value.Charging,
                }, vid, pid);
                break;
            }
        }
    }

    public static void Poll(PollContext ctx) { PollNova(ctx); PollCyclone(ctx); }
}
