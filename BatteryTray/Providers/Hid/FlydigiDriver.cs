// Manettes Flydigi Vader 5 Pro (37D7:2401) et Apex 5 (37D7:2501), collection vendeur FFA0:0001.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/flydigi.cpp, porté en C# — voir NOTICE.md.
// Référence du protocole : SDL (SDL_hidapi_flydigi.c) et HeyOkay/HaloBattery issue #191 (traces Windows). L'identifiant de modèle 135 de l'Apex 5
// n'est pas dans la table de SDL : vu sur un vrai Apex 5 sur récepteur 2,4 GHz. Seules des requêtes d'architecture / d'infos sont envoyées.
// Vader 5 Pro : rapports de sortie non numérotés ; Apex 5 : rapport 03.
namespace BatteryTray.Providers.Hid;

public static class FlydigiDriver
{
    const ushort Vid = 0x37D7;

    public sealed record Model(ushort Pid, byte ReportId, byte[] Ids, string Name);
    public static readonly Model[] Models =
    {
        new(0x2401, 0x00, new byte[] { 130 }, "Flydigi Vader 5 Pro"),
        new(0x2501, 0x03, new byte[] { 128, 129, 133, 134, 135 }, "Flydigi Apex 5"),
    };

    public static byte[] BuildCommand(byte reportId, byte command, byte p1) => new byte[] { reportId, 0x5A, 0xA5, command, p1, 0 };

    /// <summary>Normalise une réponse (certains micrologiciels gardent un identifiant d'entrée 04) ; null si ce n'est pas la réponse à <paramref name="command"/>.</summary>
    public static byte[]? NormalizeReply(byte[] r, byte command)
    {
        if (r.Length >= 4 && r[0] == 0x04 && r[1] == 0x5A && r[2] == 0xA5) r = r[1..];
        return r.Length >= 5 && r[0] == 0x5A && r[1] == 0xA5 && r[2] == command ? r : null;
    }

    /// <summary>Réponse d'infos (commande 01) → (pourcentage, en charge, liaison, identifiant de modèle) ; null si invalide pour ce modèle.</summary>
    public static (int Percent, bool Charging, bool Receiver)? ParseInfo(Model m, byte[] r, bool modern)
    {
        if (r.Length < 12 || !m.Ids.Contains(r[5])) return null; // identité du modèle confirmée
        int level = r[11] & 0x0F, state = r[11] >> 4;
        if (state > 2 || level > 5) return null;
        int connection = r[6];
        if (modern ? connection > 1 : connection != 1 && connection != 2) return null;
        return (state == 2 ? 100 : level * 20, state == 1, connection == (modern ? 1 : 2));
    }

    static byte[]? Reply(HidDev d, byte command)
    {
        long end = Environment.TickCount64 + 800;
        while (Environment.TickCount64 < end)
        {
            int remaining = (int)(end - Environment.TickCount64);
            if (remaining <= 0) return null;
            var raw = d.Read(remaining);
            if (raw == null) return null;
            if (NormalizeReply(raw, command) is { } r) return r;
        }
        return null;
    }

    static void PollModel(PollContext ctx, Model m)
    {
        if (ctx.IsClaimed(Vid, m.Pid)) return;
        foreach (var c in ctx.Find(Vid, m.Pid, c => c.Page == 0xFFA0 && c.Usage == 1 && c.InLen >= 12 && c.OutLen >= 6))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            d.Flush();
            if (!d.Write(BuildCommand(m.ReportId, 0x07, 0))) continue;
            var arch = Reply(d, 0x07);
            bool modern = arch != null && arch.Length > 4 && arch[3] == 1 && arch[4] == 0;
            if (!d.Write(BuildCommand(m.ReportId, 0x01, 2)) || Reply(d, 0x01) is not { } info) continue;
            if (ParseInfo(m, info, modern) is not { } p) continue;
            ctx.Add(new DeviceStatus
            {
                Key = ctx.StableKey("flydigi:" + HidUtil.Hex4(m.Pid)), Name = m.Name, Kind = DeviceKind.Gamepad,
                Via = p.Receiver ? "2.4 GHz receiver" : "USB cable", Percent = p.Percent, Charging = p.Charging,
            }, Vid, m.Pid);
        }
    }

    public static void Poll(PollContext ctx) { foreach (var m in Models) PollModel(ctx, m); }
}
