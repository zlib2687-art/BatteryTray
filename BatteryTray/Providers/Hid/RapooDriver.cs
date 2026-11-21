// Souris de jeu Rapoo VT. La batterie est poussée périodiquement ; aucune commande ni poignée de main n'est nécessaire.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/rapoo.cpp et rapoo_protocol.h, porté en C# — voir NOTICE.md.
// Sources :
//  * Rapoo Hub officiel (hub.rapoo.cn), décodeur VT_nrf54L et manifestes matériels : rapport 07 sur FF00:0002, état à l'octet 7, pourcentage à l'octet 8, PID de la souris
//    facultatif en 13..14. La connexion est le quartet bas de l'octet 1 (0 radio / 1 Bluetooth / 2 USB).
//  * PyFlat/Device-Battery-Info (RapooProtocol.cs) : anciens rapports de 7 octets BB B0 sur FF00:0002, état à l'octet 5, pourcentage à l'octet 6 ; en charge, 7F peut signifier « inconnu ».
//  * VT1 Pro Max (récepteur 1233, câble 4433), VT3 Pro Max (récepteur 24AE:1244) : mêmes rapports passifs de 7 octets (BB B0 71 84 03 01 64 toutes les ~3,2 s = 100 % ; sur câble en charge BB B0 71 84 03 02 5F = 95 %).
//    Source VT3 Pro Max : github.com/zbndev/rapoo_battery. La sémantique « BB » héritée est réservée aux identifiants connus : le micrologiciel Telink des autres modèles diffère.
//  * VT3 Max Gen 2 (récepteur 1417, autre révision) : collection de 13 octets ; l'état et le pourcentage aux octets 7/8 conviennent, mais pas le PID de la souris (13..14) : l'identité retombe sur celui du récepteur.
namespace BatteryTray.Providers.Hid;

public static class RapooDriver
{
    public enum Connection { Unknown, Receiver, Bluetooth, Cable }
    public enum Profile { Nordic54L, LegacyVT3Pro }
    public sealed record Battery(int Percent, bool Charging, Connection Connection, ushort MousePid);

    /// <summary>Décode un rapport passif (identifiant de rapport compris). null si ce n'est pas un rapport d'état valide.</summary>
    public static Battery? DecodeBattery(byte[] d, Profile profile = Profile.Nordic54L)
    {
        if (d.Length == 0) return null;
        int state, percent; var conn = Connection.Unknown; ushort mousePid = 0;
        if (profile == Profile.Nordic54L && d[0] == 0x07)
        {
            // Nordic 54L : mode, cran de DPI, DPI X/Y, état de la batterie, pourcentage.
            if (d.Length < 9 || (d[1] & 0x0F) > 2) return null;
            conn = (d[1] & 0x0F) switch { 0 => Connection.Receiver, 1 => Connection.Bluetooth, _ => Connection.Cable };
            state = d[7]; percent = d[8];
            if (d.Length >= 15) mousePid = (ushort)(d[13] | (d[14] << 8));
        }
        else if (profile == Profile.LegacyVT3Pro && d[0] == 0xBB)
        {
            // Micrologiciel VT3 Pro : BB B0 mode DPI-lo DPI-hi état pourcentage.
            if (d.Length < 7 || d[1] != 0xB0) return null;
            state = d[5]; percent = d[6];
        }
        else return null;
        // 0 = invalide / transition. Certains anciens micrologiciels envoient 7F au lieu d'un niveau pendant la charge.
        if (state != 1 && state != 2) return null;
        if (percent > 100 && !(profile == Profile.LegacyVT3Pro && state == 2 && percent == 0x7F)) return null;
        return new Battery(percent <= 100 ? percent : -1, state == 2, conn, mousePid);
    }

    public sealed record Model(ushort Cable, ushort Receiver, string Name);
    // Noms choisis dans les manifestes de Rapoo. La prise en charge est décidée par la collection et une trame d'état valide : d'autres modèles du même protocole fonctionnent sans étendre la table.
    public static readonly Model[] Models =
    {
        new(0x4610, 0x1410, "Rapoo VT0"), new(0x4620, 0x1420, "Rapoo VT0 Max"), new(0x4615, 0x1415, "Rapoo VT1"), new(0x4644, 0x1444, "Rapoo VT1 Max"),
        new(0x4622, 0x1422, "Rapoo VT2"), new(0x4623, 0x1423, "Rapoo VT2 Max"), new(0x4612, 0x1412, "Rapoo VT3"), new(0x4617, 0x1417, "Rapoo VT3 Max (Gen 2)"),
        new(0x4B06, 0x1B06, "Rapoo VT3 Max V2"), new(0x4611, 0x1411, "Rapoo VT3S"), new(0x4621, 0x1421, "Rapoo VT3S Max"), new(0x4613, 0x1413, "Rapoo VT7"),
        new(0x4616, 0x1416, "Rapoo VT7 Max"), new(0x4614, 0x1414, "Rapoo VT9 (Gen 2)"), new(0x4618, 0x1418, "Rapoo VT9 Max"), new(0x4415, 0x1215, "Rapoo VT3 Pro"),
        new(0x4433, 0x1233, "Rapoo VT1 Pro Max"),
    };

    public static Model? FindModel(ushort pid) => Models.FirstOrDefault(m => m.Cable == pid || m.Receiver == pid);

    /// <summary>Souris confirmées pour le rapport d'état BB B0 de 7 octets.</summary>
    public static bool IsLegacyVT3Pro(ushort pid) => pid is 0x1215 or 0x4415 or 0x1233 or 0x4433 or 0x1244;

    public static bool IsBatteryCollection(HidColl c, ushort pid) =>
        c.Page == 0xFF00 && c.Usage == 0x0002 && (c.InLen == 16 || c.InLen == 19 || (c.InLen == 13 && pid == 0x1417) || (c.InLen == 7 && IsLegacyVT3Pro(pid)));

    // Poignées conservées entre les lectures : Windows met en mémoire les rapports passifs arrivés pendant l'intervalle de 60 s au lieu de manquer les souris
    // dont l'intervalle d'émission dépasse 5 s.
    static readonly Dictionary<string, HidDev> Open = new();

    static Battery? DrainQueued(HidDev d, Profile profile)
    {
        Battery? got = null;
        for (int i = 0; i < 64; i++)
        {
            var r = d.Read(30);
            if (r == null) break;
            if (DecodeBattery(r, profile) is { } b) got = b;
        }
        return got;
    }

    static Battery? WaitBattery(HidDev d, Profile profile, long end)
    {
        do
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null) return null;
            if (DecodeBattery(r, profile) is { } b) return b;
        } while (Environment.TickCount64 < end);
        return null;
    }

    static Battery? ReadBattery(HidDev slot, HidColl c, Profile profile, bool fresh)
    {
        if (!fresh && DrainQueued(slot, profile) is { } q) return q;
        // Un rapport passif peut mettre plusieurs secondes (VT1 Pro Max ~4 s ; VT9 Gen 2 ~5-10 s à la première ouverture) : jusqu'à 5 s si rien n'était en file.
        long start = Environment.TickCount64, end = start + 5000;
        if (WaitBattery(slot, profile, end) is { } w) return w;
        // Si une poignée persistante a échoué aussitôt (récepteur réinitialisé entre deux lectures), on la rouvre une fois.
        if (!fresh && Environment.TickCount64 - start < 50) { slot.Close(); if (slot.Open(c)) return WaitBattery(slot, profile, end); }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        var present = new HashSet<string>();
        foreach (var (vid, pid) in ctx.Products(0x24AE))
        {
            foreach (var c in ctx.Find(vid, pid, c => IsBatteryCollection(c, pid)))
            {
                present.Add(c.Path);
                bool fresh = !Open.TryGetValue(c.Path, out var slot);
                if (fresh)
                {
                    slot = new HidDev();
                    if (!slot.Open(c)) { slot.Dispose(); continue; }
                    Open[c.Path] = slot;
                }
                var profile = c.InLen == 7 ? Profile.LegacyVT3Pro : Profile.Nordic54L;
                if (ReadBattery(slot!, c, profile, fresh) is not { } b) continue;

                var model = FindModel(b.MousePid != 0 ? b.MousePid : pid);
                ushort identity = model?.Cable ?? (b.MousePid != 0 ? b.MousePid : pid);
                string name = model?.Name ?? c.Product;
                if (pid == 0x1244) name = "Rapoo VT3 Pro Max"; // pas de PID de câble confirmé pour le récepteur 1244
                if (name.Length == 0 || name == "Rapoo Gaming Device") name = "Rapoo wireless mouse";
                var s = new DeviceStatus
                {
                    Key = "rapoo:" + HidUtil.Hex4(identity), Name = name, Kind = DeviceKind.Mouse,
                    Via = b.Connection == Connection.Cable || (b.Connection == Connection.Unknown && model != null && pid == model.Cable) ? "USB cable"
                        : b.Connection == Connection.Bluetooth ? "Bluetooth" : "2.4 GHz receiver",
                    Percent = b.Percent, Charging = b.Charging,
                };
                // Une souris en charge peut répondre par le câble et par le récepteur : une seule entrée, l'état du câble l'emporte mais un pourcentage disponible est conservé.
                var existing = ctx.Out.FirstOrDefault(o => o.Key == s.Key);
                if (existing == null) ctx.Add(s, vid, pid);
                else
                {
                    if (s.Via == "USB cable") { int rp = existing.Percent; ctx.Out[ctx.Out.IndexOf(existing)] = s; if (s.Percent < 0) s.Percent = rp; s.Vid = vid; }
                    else if (existing.Percent < 0 && s.Percent >= 0) existing.Percent = s.Percent;
                    ctx.Claim(vid, pid);
                }
                break;
            }
        }
        foreach (var k in Open.Keys.Where(k => !present.Contains(k)).ToList()) { Open[k].Dispose(); Open.Remove(k); }
    }
}
