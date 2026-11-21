// Kysona, Pulsar et ATK / VXE (VID 3554, récepteurs ATK 373B), modèles G-Wolves de ce protocole (VID 33E4, ex. Fenrir Asym), Hitscan
// Hyperlight d'origine (VID 3770, vérifiée sur le récepteur 1K 3770:0200), nouvelle génération XS-1 de Pulsar (VID 3710, vérifiée sur
// le récepteur 8K 3710:5406), souris Redragon à base Compx (M916 Pro 1K, M913 tri-mode, G49 : récepteur 3554:F55D / F5D5, câble F55E),
// HECATE (35BB). Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/kysona.cpp, porté en C# — voir NOTICE.md.
// Tous partagent un protocole : rapport de 17 octets, id 08 : [08][cmd][0 x14][somme], somme = 0x55 - somme(octets 0..15).
//   cmd 03 = en ligne  → réponse [6] = 1 si la souris est connectée au récepteur
//   cmd 04 = batterie  → réponse [6] = %, [7] = en charge, [8..9] = millivolts
// La famille ATK VXE Dragonfly R1 (récepteurs 3554:F58A / F58E / F505, câbles F58C / F58F / F506) utilise le même protocole sur la
// collection vendeur FF02:0002 ; ses récepteurs exposent d'autres collections de 17 octets (FF03..FF06) : FF02:0002 est essayée en premier.
// Identifiants et protocole : github.com/S1berla/vxe-r1-pro-max-battery, github.com/orcunturkol/BatteryTray (PR n° 1), HeyOkay/HaloBattery
// (ticket 273) ; référence : pilote Linux drivers/hid/hid-kysona.c (Lode Willems) ; Redragon : github.com/EliottV17/redragon-battery-omarchy
// et github.com/pingpingjinping/g49-web-control. Lecture seule.
namespace BatteryTray.Providers.Hid;

public static class KysonaDriver
{
    // Redragon utilise les identifiants de Compx ; son récepteur se nomme seulement « 2.4G Wireless Receiver ».
    public static bool IsRedragon(ushort vid, ushort pid) => vid == 0x3554 && pid is 0xF55D or 0xF5D5 or 0xF55E;
    public static bool IsVxeR1(ushort vid, ushort pid) => vid == 0x3554 && pid is 0xF58A or 0xF58C or 0xF58E or 0xF58F or 0xF505 or 0xF506;

    /// <summary>Le câble d'une VXE R1 se déclare sous l'identifiant de son récepteur : une seule entrée, quelle que soit la connexion.</summary>
    public static ushort KeyPid(ushort vid, ushort pid) =>
        vid == 0x3554 && pid == 0xF58C ? (ushort)0xF58A :
        vid == 0x3554 && pid == 0xF58F ? (ushort)0xF58E :
        vid == 0x3554 && pid == 0xF506 ? (ushort)0xF505 : pid;

    // HECATE (Edifier) vend aussi des casques sous 35BB (G2 Wireless 7.1) dont la collection FF02:0002 de 64 octets parle autre chose :
    // seuls les identifiants de souris reçoivent des trames Kysona.
    public static bool IsHecateMouse(ushort pid) => pid is 0xD3FA or 0xD3FB or 0xD3FC or 0xD3FD;

    public static bool IsCable(ushort vid, ushort pid) =>
        (vid == 0x33E4 && GloriousDriver.GWolvesIsCable(pid))
        || (vid == 0x35BB && pid is 0xD3FA or 0xD3FC or 0xD3FD)
        || (vid == 0x3554 && pid is 0xF55E or 0xF58C or 0xF58F or 0xF506);

    /// <summary>Nom d'affichage : retire « Dongle », « Receiver »… et remplace les noms de récepteurs trop génériques.</summary>
    public static string CleanName(string name, ushort vid, ushort pid)
    {
        if (vid == 0x33E4) return GloriousDriver.GWolvesModelName(pid);
        if (IsRedragon(vid, pid)) return "Redragon wireless mouse"; // partagé par plusieurs modèles
        if (IsVxeR1(vid, pid)) return "VXE Dragonfly R1";           // les récepteurs se nomment « VXE NordicMouse 1K Dongle »…
        string n = name ?? "";
        foreach (var suffix in new[] { " Dongle", " Receiver", " 2.4G", " Wireless Receiver" })
            if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal)) n = n[..^suffix.Length];
        if (vid == 0x3770) return "Hitscan Hyperlight"; // le récepteur se nomme seulement « Hitscan USB Receiver »
        if (vid == 0x35BB) return "HECATE mouse";
        if (n.Length == 0) n = vid == 0x373B ? "ATK wireless mouse" : "Wireless mouse";
        return n;
    }

    /// <summary>
    /// Interprète les réponses 03 (en ligne) et 04 (batterie) ; null = la requête n'a pas eu de réponse valide.
    /// « asleep » : le récepteur dit la souris en ligne mais elle n'a pas répondu — on garde la dernière valeur.
    /// </summary>
    public static (bool Answered, bool Connected, int Percent, bool Charging, bool Asleep) Parse(byte[]? online, byte[]? battery)
    {
        bool answered = false, connected = false, charging = false, asleep = false;
        int percent = -1;
        if (online is { Length: >= 17 }) { answered = true; connected = online[6] != 0; }
        if ((!answered || connected) && battery is { Length: >= 17 })
        {
            answered = true; connected = true;
            if (battery[6] <= 100) percent = battery[6];
            charging = battery[7] != 0;
        }
        else if (answered && connected) asleep = true;
        return (answered, connected, percent, charging, asleep);
    }

    /// <summary>Faut-il envoyer des trames de souris à ce produit ? Exclut les casques ATK, le récepteur 8K Nano, les récepteurs de claviers « BY » et les casques HECATE.</summary>
    public static bool ShouldProbe(ushort vid, ushort pid)
    {
        // Casques ATK N9 Pro / N9 Ultra (protocole série) et récepteur 8K Nano 1178 (Holtek) : jamais de trames de souris.
        if (vid == 0x373B && pid is 0x11CA or 0x11CB or 0x1203 or 0x1204 or 0x1178) return false;
        // 3554:FA09 / FB40 : récepteurs de claviers « BY » (AULA, MCHOSE, Redragon K673) : leur collection FF02:0002 passerait le contrôle de taille
        // et un clavier recevait deux trames de souris à chaque lecture, ce qui l'empêchait de s'endormir (ticket 191).
        if (vid == 0x3554 && pid is 0xFA09 or 0xFB40) return false;
        // Casques HECATE : trames de souris seulement pour les identifiants de souris.
        if (vid == 0x35BB && !IsHecateMouse(pid)) return false;
        return true;
    }

    static byte[]? Query(HidDev d, byte cmd)
    {
        d.Flush();
        if (!d.Write(CorsairDriver.BuildCompxRequest(cmd))) return null;
        long end = Environment.TickCount64 + 500;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return null;
            var r = d.Read(left);
            if (r == null || r.Length < 17) return null;
            if (CorsairDriver.IsValidCompxReply(r, cmd)) return r;
        }
    }

    public static void Poll(PollContext ctx)
    {
        var seenNames = new List<string>();
        foreach (ushort vendor in new ushort[] { 0x3554, 0x373B, 0x3770, 0x33E4, 0x3710, 0x35BB })
        {
            foreach (var (vid, pid) in ctx.Products(vendor))
            {
                if (!ShouldProbe(vid, pid)) continue;
                // Kysona expose une collection dédiée de 17 octets ; les récepteurs ATK / VXE mettent le rapport 08 dans une collection vendeur
                // qui porte aussi des rapports plus grands. La réponse à somme de contrôle écarte tout ce qui ne parle pas ce protocole.
                var colls = ctx.Find(vid, pid, c => (c.OutLen == 17 && c.InLen == 17) || (c.Page >= 0xFF00 && c.OutLen >= 17 && c.InLen >= 17))
                    .OrderByDescending(c => c.Page == 0xFF02 && c.Usage == 0x0002) // le canal batterie est FF02:0002 quand il existe
                    .ToList();
                foreach (var c in colls)
                {
                    using var d = new HidDev();
                    if (!d.Open(c)) continue;
                    var online = Query(d, 0x03);
                    bool connected = online != null && online[6] != 0;
                    var battery = online == null || connected ? Query(d, 0x04) : null;
                    var (answered, isConnected, percent, charging, asleep) = Parse(online, battery);
                    if (!answered) continue;
                    if (!isConnected) { ctx.Claim(vid, pid); break; }

                    var s = new DeviceStatus
                    {
                        Key = $"kysona:{HidUtil.Hex4(vid)}:{HidUtil.Hex4(KeyPid(vid, pid))}",
                        Name = CleanName(c.Product, vid, pid),
                        Kind = HidUtil.KindFromCollections(ctx.Colls, vid, pid),
                        Via = IsCable(vid, pid) ? "USB cable" : "2.4 GHz receiver",
                        Percent = asleep ? -1 : percent, Charging = charging, Connected = !asleep,
                    };
                    if (s.Kind == DeviceKind.Other) s.Kind = DeviceKind.Mouse;
                    // Une souris branchée pour charger peut apparaître deux fois (câble + récepteur) : on n'en garde qu'une.
                    if (seenNames.Contains(s.Name))
                    {
                        ctx.Claim(vid, pid);
                        if (s.Charging) foreach (var o in ctx.Out.Where(o => o.Name == s.Name)) o.Charging = true;
                    }
                    else { seenNames.Add(s.Name); ctx.Add(s, vid, pid); }
                    break;
                }
            }
        }
    }
}
