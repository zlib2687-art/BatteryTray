// Souris sans fil Cooler Master (VID 2516) : MM712 (câble 0169) et MM731 (câble 0167). Elles s'appairent au même récepteur
// « Cooler Master Mice Dongle » (01B7). Interface vendeur 3, page FF0A, sans identifiant de rapport ; rapports de 32 octets sur le
// récepteur, de 64 octets sur le câble.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/coolermaster.cpp, porté en C# — voir NOTICE.md.
//   Requête : [46][81][00] → rapport d'état [47][81][modèle] … [10] %, [11] en charge (0 = non), [12] liaison (0 câble, 1 récepteur).
//   La souris l'envoie aussi d'elle-même quand le niveau change. [2] distingue les modèles sur le récepteur partagé : 50 = MM712 ; 70 vu
//   dans un rapport MM731. Une souris en veille ou éteinte ne répond pas. MM712 vérifiée sur matériel (2516:01B7) ; MM731 : même famille de
//   protocole selon MM731-Controller, octets de batterie et de modèle non vérifiés.
// Protocole : github.com/syntactic-spice/coolermaster-usb-protocol (docs/PROTOCOL.md) et github.com/riVFerd/MM731-Controller. Lecture seule.
namespace BatteryTray.Providers.Hid;

public static class CoolerMasterDriver
{
    const ushort Dongle = 0x01B7, Mm712Cable = 0x0169, Mm731Cable = 0x0167;

    public static string ModelName(ushort pid, byte model) =>
        pid == Mm712Cable || (pid == Dongle && model == 0x50) ? "Cooler Master MM712"
        : pid == Mm731Cable || (pid == Dongle && model == 0x70) ? "Cooler Master MM731"
        : "Cooler Master wireless mouse";

    public static byte[] BuildRequest() => new byte[] { 0x00, 0x46, 0x81, 0x00 };

    /// <summary>Rapport d'état [47][81]… : renvoie l'état, ou null si ce n'est pas la réponse attendue (trop court ou autre rapport).</summary>
    public static DeviceStatus? ParseStatus(ushort pid, byte[] r)
    {
        if (r.Length < 13 || r[0] != 0x47 || r[1] != 0x81) return null;
        return new DeviceStatus
        {
            Key = "coolermaster:" + HidUtil.Hex4(pid),
            Name = ModelName(pid, r[2]),
            Kind = DeviceKind.Mouse,
            Via = r[12] == 0 ? "USB cable" : "2.4 GHz receiver",
            Percent = r[10] is >= 1 and <= 100 ? r[10] : -1,
            Charging = r[11] != 0,
        };
    }

    static byte[]? QueryStatus(HidDev d)
    {
        d.Flush();
        if (!d.Write(BuildRequest())) return null;
        long end = Environment.TickCount64 + 500;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null || r.Length < 13) return null;
            if (r[0] == 0x47 && r[1] == 0x81) return r;
        }
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(0x2516)) // triés : les identifiants de câble avant le récepteur
        {
            if (pid != Dongle && pid != Mm712Cable && pid != Mm731Cable) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Iface == 3 && c.Page == 0xFF0A))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var r = QueryStatus(d);
                if (r == null) continue;
                var s = ParseStatus(pid, r);
                if (s == null) continue;
                // Câble et récepteur branchés ensemble : la souris apparaît deux fois, on n'en garde qu'une.
                bool dup = false;
                foreach (var o in ctx.Out.Where(o => o.Name == s.Name)) { dup = true; o.Charging |= s.Charging; }
                if (dup) ctx.Claim(vid, pid); else ctx.Add(s, vid, pid);
                break;
            }
        }
    }
}
