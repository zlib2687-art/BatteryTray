// Souris sans fil MSI sur leurs récepteurs 2,4 GHz.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/msi.cpp, porté en C# — voir NOTICE.md.
// Protocole (module Gaming Gear de MSI Center, vérifié sur de vraies souris) :
//   rapport de sortie (id 0, charge de 64 octets) : [00][0F 20 00…] ; réponse (identifiant 0 retiré) : [0] = 0F, [1] = 20, [14] = batterie : bit 7 = en charge, bits 0..6 = %.
// Un niveau de 0 hors charge est traité comme « endormie » (MSI Center journalise des 0 entre des valeurs normales). MSI Center envoie aussi 41 80 (« contrôle à distance »)
// qui bascule la souris en mode piloté par logiciel : volontairement non envoyé, la requête 0F 20 suffit. Une requête inconnue reçoit FF AA 00 00 et un écho.
// VERSA 300 ELITE WIRELESS (0DB0:9E22) et CLUTCH GM31 (récepteur 0D3C, câble 0D3B) répondent à la requête, sur FF0D:00B0 et FF00:0001. GM31 confirmée contre MSI Center :
// la trame poussée 0F 20 00 00 23 05 7A 91 00 00 00 00 05 80 56 xx donne 0x56 = 86 %.
// CLUTCH GM41 (récepteur 0D4C, câble 0D4B) n'a pas de requête : sa collection (FF10:0006) pousse A0 <brut> 07 <drapeaux> 94 00 94 tant que la souris bouge, et <brut> baisse avec la
// batterie (captures USBPcap de MSI Center : PR #1 de DARL0K/Wireless-Device-Battery-Monitor et liamgibsonn/mouse-protocol). Identifiants GM41 et câbles non testés.
namespace BatteryTray.Providers.Hid;

public static class MsiDriver
{
    const ushort Vid = 0x0DB0;

    public enum Mode { Query, Passive }
    public sealed record Model(ushort Pid, bool Cable, Mode Mode, ushort Page, ushort Usage, string Name);

    public static readonly Model[] Models =
    {
        new(0x9E22, false, Mode.Query, 0xFF0D, 0x00B0, "MSI VERSA 300 ELITE WIRELESS"),
        new(0x0D3C, false, Mode.Query, 0xFF00, 0x0001, "MSI Clutch GM31 Lightweight Wireless"),
        new(0x0D3B, true, Mode.Query, 0xFF00, 0x0001, "MSI Clutch GM31 Lightweight Wireless"),
        new(0x0D4C, false, Mode.Passive, 0xFF10, 0x0006, "MSI Clutch GM41 Lightweight Wireless"),
        new(0x0D4B, true, Mode.Passive, 0xFF10, 0x0006, "MSI Clutch GM41 Lightweight Wireless"),
    };

    public static bool Matches(Model m, HidColl c) =>
        c.Iface == 1 && c.Page == m.Page && c.Usage == m.Usage && (m.Mode == Mode.Query ? c.OutLen >= 65 && c.InLen >= 65 : c.InLen >= 7);

    /// <summary>Réponse à 0F 20 : (pourcentage, en charge) ; null si ce n'est pas la réponse ; (−1, false) si le niveau dépasse 100.</summary>
    public static (int Percent, bool Charging)? ParseStatus(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x0F || r[1] != 0x20) return null;
        int level = r[14] & 0x7F;
        return level > 100 ? (-1, false) : (level, (r[14] & 0x80) != 0);
    }

    /// <summary>GM41 : ajustement linéaire par (0x4D → 57 %), (0x48 → 54 %), (0x45 → 53 %) : environ 0,51 % par pas.</summary>
    public static int Gm41RawToPercent(int raw) => HidUtil.Clamp100((raw * 51 + 1760 + 50) / 100);

    /// <summary>Trame poussée par la GM41 : (brut, drapeaux) ou null.</summary>
    public static (int Raw, byte Flags)? ParseGm41(byte[] r) => r.Length < 7 || r[0] != 0xA0 || r[2] != 0x07 ? null : (r[1], r[3]);

    static (int Percent, bool Charging)? QueryStatus(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        d.Flush();
        if (!d.Write(0x00, 0x0F, 0x20)) return null;
        long end = Environment.TickCount64 + 1000;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            if (ParseStatus(r) is { } s) return s.Percent < 0 ? null : s;
        }
        return null;
    }

    // La GM41 ne parle que lorsque la souris bouge : on attend un peu plus longtemps ; une souris endormie ne donne rien et l'appli garde sa dernière valeur.
    static (int Raw, byte Flags)? ListenGm41(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        long end = Environment.TickCount64 + 2000;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) break;
            if (ParseGm41(r) is { } g) return g;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, 0x9E22)) return;
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c => Matches(m, c)))
            {
                var s = new DeviceStatus { Key = "msi:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Mouse, Via = m.Cable ? "USB cable" : "2.4 GHz receiver" };
                if (m.Mode == Mode.Query)
                {
                    if (QueryStatus(c) is not { } q) break;
                    if (q.Percent == 0 && !q.Charging) s.Connected = false; else { s.Percent = q.Percent; s.Charging = q.Charging; }
                }
                else
                {
                    if (ListenGm41(c) is not { } g) break;
                    s.Percent = Gm41RawToPercent(g.Raw);
                    s.Detail = $"approximate (raw 0x{g.Raw:X2}, flags 0x{g.Flags:X2})";
                }
                ctx.Add(s, Vid, m.Pid);
                break;
            }
        }
    }
}
