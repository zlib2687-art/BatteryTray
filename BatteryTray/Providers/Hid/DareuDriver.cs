// DAREU : récepteur EM901X (260D:1074) et câble USB (260D:1090), vérifiés sur matériel ; Sharkoon Skiller SGM50W (260D:1125, non testé) ; clavier A84 (260D:0022, puce SONiX) ;
// protocole « Togran » : récepteur 260D:1131 et câble A980 Pro 260D:1148 (vérifiés sur matériel).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/dareu.cpp, porté en C# — voir NOTICE.md.
// EM901X : requête de batterie du pilote officiel AIM-WL v1.0 (GetPower / SendCommand) ; analyse : wushuai0802/myfinder-device-battery (engine/upstream-protocols.md). Contrairement au transport
//   Kysona, le rapport de fonctionnalité 08 part sur FF02:0002 et le rapport d'entrée 09 arrive sur la collection sœur FF01:0000. Seule la commande 04 est nécessaire.
//   Réponse : [09][04][état = 0]…[6 = %][7 = en charge]. Aucune règle de somme de contrôle établie. Le Sharkoon SGM50W utilise la même paire de collections (outil Compx de Sharkoon).
// A84 : pilote officiel « DAREU A84 THREE MODE MECHANICAL KEYBOARD V1.6 » : son lanceur ouvre l'interface 0 (collection clavier, rapport de fonctionnalité de 65 octets) et n'envoie qu'une commande :
//   SetFeature [00][04 F6 00…] (65 octets), puis GetFeature 0 (trois lectures écartées), puis jusqu'à trois lectures jusqu'à ce que l'octet 15 soit non nul ; l'octet 5 est le niveau en %. Pas de charge.
// Togran : rapport de fonctionnalité 0A (65 octets) sur la collection de contrôle grand public : [0A][emplacement << 4][long][classe][cmd], la réponse répète classe / cmd ;
//   emplacement 0 classe 00 / A0 liste les souris appairées, emplacement n classe 07 / 80 donne [7] = état [8] = niveau [9] = max, état 1 = en charge.
namespace BatteryTray.Providers.Hid;

public static class DareuDriver
{
    const ushort Vid = 0x260D;

    // ------------------------------------------------------------------ logique pure (testée)

    public static byte[] BuildTogranRequest(byte slot, byte len, byte cls, byte cmd)
    {
        var r = new byte[65]; r[0] = 0x0A; r[1] = (byte)(slot << 4); r[2] = len; r[3] = cls; r[4] = cmd; return r;
    }

    public static bool IsTogranReply(byte[] r, byte cls, byte cmd) => r.Length >= 11 && (r[1] & 0x0F) == 2 && r[3] == cls && r[4] == cmd;

    /// <summary>Réponse Togran batterie (07 80) : (pourcentage, en charge) ; null si le niveau dépasse le maximum.</summary>
    public static (int Percent, bool Charging)? ParseTogranBattery(byte[] b)
    {
        if (b.Length < 10) return null;
        int max = b[9] != 0 ? b[9] : 100;
        return b[8] > max ? null : (b[8] * 100 / max, b[7] == 1);
    }

    /// <summary>Liste des souris appairées (00 A0) : (emplacement, PID de la souris) pour chaque entrée active, 5 au plus.</summary>
    public static List<(int Slot, ushort MousePid)> ParseTogranList(byte[] r)
    {
        var list = new List<(int, ushort)>();
        if (r.Length < 8) return list;
        int count = r[7];
        for (int slot = 1; slot <= count && slot <= 5; slot++)
        {
            int e = 8 + (slot - 1) * 3;
            if (e + 2 >= r.Length) break;
            if (r[e] != 0) list.Add((slot, (ushort)((r[e + 1] << 8) | r[e + 2])));
        }
        return list;
    }

    /// <summary>Requête GetPower EM901X : [08][04][00…][49] (0x55 moins la somme des octets 0..15).</summary>
    public static byte[] BuildEm901xRequest() { var r = new byte[17]; r[0] = 0x08; r[1] = 0x04; r[16] = 0x49; return r; }

    public static (int Percent, bool Charging)? ParseEm901x(byte[] r) => r.Length != 17 || r[0] != 0x09 || r[1] != 0x04 || r[2] != 0 || r[6] > 100 ? null : (r[6], r[7] != 0);

    public static byte[] BuildA84Request() { var r = new byte[65]; r[1] = 0x04; r[2] = 0xF6; return r; }

    /// <summary>Réponse A84 : niveau (octet 5) si l'octet 15 est non nul ; -1 sinon ; null si &gt; 100.</summary>
    public static int? ParseA84(byte[] r) => r.Length < 16 || r[15] == 0 ? -1 : r[5] > 100 ? null : r[5];

    // ------------------------------------------------------------------ échanges

    static byte[]? TogranQuery(HidDev d, byte slot, byte len, byte cls, byte cmd)
    {
        if (!d.SetFeature(BuildTogranRequest(slot, len, cls, cmd))) return null;
        for (int tries = 0; tries < 15; tries++)
        {
            Thread.Sleep(30);
            if (!d.GetFeature(0x0A, out var r) || r.Length < 11) return null;
            if (IsTogranReply(r, cls, cmd)) return r;
        }
        return null;
    }

    static void AddTogranBattery(PollContext ctx, HidDev d, byte slot, ushort pid, ushort mousePid)
    {
        if (TogranQuery(d, slot, 4, 0x07, 0x80) is not { } b || ParseTogranBattery(b) is not { } p) return;
        ctx.Add(new DeviceStatus
        {
            Key = "dareu:260D:" + HidUtil.Hex4(pid) + (pid == mousePid ? "" : ":" + HidUtil.Hex4(mousePid)),
            Name = mousePid == 0x1148 ? "DAREU A980 Pro" : "DAREU " + HidUtil.Hex4(mousePid),
            Kind = DeviceKind.Mouse, Via = slot != 0 ? "2.4 GHz receiver" : "USB cable", Percent = p.Percent, Charging = p.Charging,
        }, Vid, pid);
    }

    // Le récepteur 1131 liste les souris par emplacement ; l'A980 Pro sur câble (1148) répond sur l'emplacement 0.
    // Branchée, la souris reste listée par le récepteur avec l'état 0 : elle n'est donc pas affichée deux fois.
    static void PollTogran(PollContext ctx)
    {
        foreach (ushort pid in new ushort[] { 0x1131, 0x1148 })
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            var colls = ctx.Find(Vid, pid, c => c.Page == 0x000C && c.Usage == 1 && c.FeatLen == 65);
            if (colls.Count != 1) continue;
            using var d = new HidDev();
            if (!d.OpenFeatureOnly(colls[0])) continue;
            if (pid != 0x1131) { AddTogranBattery(ctx, d, 0, pid, pid); continue; }
            if (TogranQuery(d, 0, 6, 0x00, 0xA0) is not { } list) continue;
            foreach (var (slot, mousePid) in ParseTogranList(list)) AddTogranBattery(ctx, d, (byte)slot, pid, mousePid);
        }
    }

    static void PollA84(PollContext ctx)
    {
        const ushort pid = 0x0022;
        if (ctx.IsClaimed(Vid, pid)) return;
        var colls = ctx.Find(Vid, pid, c => c.Iface == 0 && c.Page == 0x01 && c.Usage == 0x06 && c.FeatLen == 65);
        if (colls.Count != 1) return;
        using var d = new HidDev();
        if (!d.OpenFeatureOnly(colls[0]) || !d.SetFeature(BuildA84Request())) return;
        Thread.Sleep(10);
        for (int i = 0; i < 3; i++) { d.GetFeature(0, out _); Thread.Sleep(10); } // le pilote officiel écarte ces lectures
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) Thread.Sleep(10);
            if (!d.GetFeature(0, out var r)) continue;
            var lvl = ParseA84(r);
            if (lvl == null) return;
            if (lvl < 0) continue;
            ctx.Add(new DeviceStatus { Key = "dareu:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(pid), Name = "DAREU A84", Kind = DeviceKind.Keyboard, Via = "HID", Percent = lvl.Value }, Vid, pid);
            return;
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollTogran(ctx);
        PollA84(ctx);
        // On préfère une lecture valide du câble à une télémétrie de récepteur éventuellement en cache.
        bool em901xRead = false;
        foreach (ushort pid in new ushort[] { 0x1090, 0x1074, 0x1125 })
        {
            if (ctx.IsClaimed(Vid, pid) || (em901xRead && pid == 0x1074)) continue;
            var inputs = ctx.Find(Vid, pid, c => c.Iface == 1 && c.Page == 0xFF01 && c.Usage == 0 && c.InLen == 17);
            var features = ctx.Find(Vid, pid, c => c.Iface == 1 && c.Page == 0xFF02 && c.Usage == 2 && c.FeatLen == 17);
            if (inputs.Count != 1 || features.Count != 1) continue; // pas d'appariement de collections arbitraires quand plusieurs appareils partagent un PID
            using var input = new HidDev(); using var feature = new HidDev();
            if (!input.Open(inputs[0]) || !feature.OpenFeatureOnly(features[0])) continue;
            input.Flush();
            if (!feature.SetFeature(BuildEm901xRequest())) continue;
            long end = Environment.TickCount64 + 1000;
            for (int reports = 0; reports < 32; reports++)
            {
                int left = (int)(end - Environment.TickCount64);
                if (left <= 0) break;
                var r = input.Read(left);
                if (r == null) break;
                if (ParseEm901x(r) is not { } p) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "dareu:" + HidUtil.Hex4(Vid) + ":" + HidUtil.Hex4(pid), Name = pid == 0x1125 ? "Sharkoon Skiller SGM50W" : "DAREU EM901X", Kind = DeviceKind.Mouse,
                    Via = pid == 0x1090 ? "USB cable" : "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging,
                }, Vid, pid);
                em901xRead = pid != 0x1125;
                break;
            }
        }
    }
}
