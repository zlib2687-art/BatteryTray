// ASUS ROG : claviers et souris sans fil, casques ROG Pelta / Delta II, écouteurs Cetra Open (VID 0B05), y compris via le ROG Omni Receiver (1ACE).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/rog.cpp, porté en C# — voir NOTICE.md.
// Requête : [id rapport][12][sous-cmd]   01 = batterie clavier, 07 = batterie souris.
// Réponse : [id rapport][12][sous-cmd]…  clavier : [6] % (Falchion : [11]), [9] == 1 en charge ; souris : [5] niveau (×25 sur les anciennes
//   jauges 0-4), [10] en charge. [1..2] == FF AA : l'appareil derrière le récepteur n'a pas répondu (en veille ou éteint).
// Les décalages comptent l'octet d'identifiant de rapport. Seules des requêtes de lecture sont envoyées.
// Casques Pelta / Delta II : collection vendeur FF00:0001, interface 3, rapport CC, 64 octets : CC 12 07 → [6] batterie % (FE = inconnu),
//   CC 12 08 → [5] == 1 en charge. Cetra Open : sous-cmd 12 → [5] gauche %, [6] droit %, [7] boîtier %.
// Protocole et table des modèles : github.com/SteveWang92/peripheral-battery-tray (rog-battery, MIT, d'après G-Helper) et github.com/1gcat/BatteryTool.
namespace BatteryTray.Providers.Hid;

public static class RogDriver
{
    const ushort Vid = 0x0B05, Omni = 0x1ACE, CetraOpen = 0x1CAC;

    public enum ModelKind { Keyboard, Mouse }

    /// <param name="ReportId">0 = rapports non numérotés</param>
    /// <param name="Iface">interface USB (-1 = n'importe quelle collection vendeur)</param>
    /// <param name="Col">étiquette de collection exigée dans le chemin (« col02 »), ou null</param>
    /// <param name="Offset">octet de batterie</param>
    /// <param name="Scale">multiplicateur du niveau</param>
    public sealed record Model(ushort Pid, string Name, ModelKind Kind, byte ReportId, int Iface, string? Col, int Offset, int Scale);

    static Model Kb(ushort pid, string n, byte rid, int iface, string? col, int off) => new(pid, n, ModelKind.Keyboard, rid, iface, col, off, 1);
    static Model Ms(ushort pid, string n, byte rid, int iface, string? col, int off, int scale) => new(pid, n, ModelKind.Mouse, rid, iface, col, off, scale);

    public static readonly Model[] Models =
    {
        // Claviers (Omni = col02 rapport 02 ; récepteur / filaire = interface 1 non numérotée)
        Kb(0x1A85, "ROG Azoth", 2, 2, "col02", 6), Kb(0x1A85, "ROG Azoth", 0, 1, null, 6), Kb(0x1A83, "ROG Azoth", 0, 1, null, 6),
        Kb(0x1B42, "ROG Azoth Extreme", 2, 2, "col02", 6), Kb(0x1B3F, "ROG Azoth Extreme", 0, 1, null, 6),
        Kb(0x1CF1, "ROG Azoth Extreme SE", 2, 2, "col02", 6), Kb(0x1CEF, "ROG Azoth Extreme SE", 0, 1, null, 6),
        Kb(0x1C25, "ROG Azoth X", 0, 1, null, 6), Kb(0x1C24, "ROG Azoth X", 0, 1, null, 6),
        Kb(0x193E, "ROG Falchion", 0, 1, null, 11), Kb(0x193C, "ROG Falchion", 0, 1, null, 11),
        Kb(0x1B04, "ROG Falchion RX", 0, -1, null, 11), Kb(0x1B06, "ROG Falchion RX Low Profile", 2, 2, "col02", 11),
        Kb(0x1AB0, "ROG Strix Scope II 96", 2, 2, "col02", 6), Kb(0x1AAE, "ROG Strix Scope II 96", 0, 1, null, 6),
        Kb(0x1B7A, "ROG Strix Scope II 96 RX", 2, 2, "col02", 6), Kb(0x1B78, "ROG Strix Scope II 96 RX", 0, 1, null, 6),
        Kb(0x1A07, "ROG Strix Scope RX TKL", 0, -1, null, 6),
        // Souris (Omni = col03 rapport 03 ; récepteur / filaire = interface 0 non numérotée)
        Ms(0x18E5, "ROG Chakram", 0, 0, null, 5, 25), Ms(0x18E3, "ROG Chakram", 0, 0, null, 5, 25),
        Ms(0x1A1A, "ROG Chakram X", 0, 0, null, 5, 1), Ms(0x1A18, "ROG Chakram X", 0, 0, null, 5, 1),
        Ms(0x18A0, "ROG Gladius II Wireless", 0, 2, null, 5, 25),
        Ms(0x197F, "ROG Gladius III Wireless", 0, 0, null, 5, 1), Ms(0x197D, "ROG Gladius III Wireless", 0, 0, null, 5, 1),
        Ms(0x1A72, "ROG Gladius III Aimpoint", 3, 2, "col03", 5, 1), Ms(0x1A72, "ROG Gladius III Aimpoint", 0, 0, null, 5, 1), Ms(0x1A70, "ROG Gladius III Aimpoint", 0, 0, null, 5, 1),
        Ms(0x1B0C, "ROG Gladius III Aimpoint EVA-02", 0, 0, null, 5, 1), Ms(0x1B0A, "ROG Gladius III Aimpoint EVA-02", 0, 0, null, 5, 1),
        Ms(0x1A94, "ROG Harpe Ace Aim Lab Edition", 3, 2, "col03", 5, 1), Ms(0x1A94, "ROG Harpe Ace Aim Lab Edition", 0, 0, null, 5, 1), Ms(0x1A92, "ROG Harpe Ace Aim Lab Edition", 0, 0, null, 5, 1),
        Ms(0x1B68, "ROG Harpe Ace Extreme", 3, 2, "col03", 5, 1), Ms(0x1B69, "ROG Harpe Ace Extreme", 3, 2, "col03", 5, 1), Ms(0x1B67, "ROG Harpe Ace Extreme", 0, 0, null, 5, 1),
        Ms(0x1B65, "ROG Harpe Ace Mini", 3, 2, "col03", 5, 1), Ms(0x1B63, "ROG Harpe Ace Mini", 0, 0, null, 5, 1),
        Ms(0x1AD0, "ROG Harpe II Ace", 3, 2, "col03", 5, 1), Ms(0x1C69, "ROG Harpe II Ace", 0, 0, null, 5, 1),
        Ms(0x1960, "ROG Keris Wireless", 0, 0, null, 5, 25), Ms(0x195E, "ROG Keris Wireless", 0, 0, null, 5, 25),
        Ms(0x1A59, "ROG Keris Wireless EVA Edition", 0, 0, null, 5, 25), Ms(0x1A57, "ROG Keris Wireless EVA Edition", 0, 0, null, 5, 25),
        Ms(0x1A68, "ROG Keris Wireless Aimpoint", 3, 2, "col03", 5, 1), Ms(0x1A6A, "ROG Keris Wireless Aimpoint", 3, 2, "col03", 5, 1),
        Ms(0x1A68, "ROG Keris Wireless Aimpoint", 0, 0, null, 5, 1), Ms(0x1A66, "ROG Keris Wireless Aimpoint", 0, 0, null, 5, 1),
        Ms(0x1B1A, "ROG Keris II Ace", 3, 2, "col03", 5, 1), Ms(0x1B18, "ROG Keris II Ace", 3, 2, "col03", 5, 1), Ms(0x1B16, "ROG Keris II Ace", 0, 0, null, 5, 1),
        Ms(0x1C0E, "ROG Keris II Origin", 3, 2, "col03", 5, 1), Ms(0x1C0C, "ROG Keris II Origin", 0, 0, null, 5, 1),
        Ms(0x1D4E, "ROG Keris II Origin KJP", 3, 2, "col03", 5, 1), Ms(0x1D4C, "ROG Keris II Origin KJP", 0, 0, null, 5, 1),
        Ms(0x1908, "ROG Pugio II", 0, 0, null, 5, 25), Ms(0x1906, "ROG Pugio II", 0, 0, null, 5, 25),
        Ms(0x1979, "ROG Spatha X", 0, 0, null, 5, 1), Ms(0x1977, "ROG Spatha X", 0, 0, null, 5, 1),
        Ms(0x18B4, "ROG Strix Carry", 0, 1, null, 7, 25),
        Ms(0x1949, "ROG Strix Impact II Wireless", 0, 0, null, 5, 25), Ms(0x1947, "ROG Strix Impact II Wireless", 0, 0, null, 5, 25),
        Ms(0x1AD7, "ROG Strix Impact III Wireless", 3, 2, "col03", 5, 1),
    };

    public static readonly (ushort Pid, string Name)[] Headsets = { (0x1B84, "ROG Pelta"), (0x1AFA, "ROG Delta II") };

    public enum Kind { Ok, Asleep, Skip, Invalid }

    public static byte[] BuildQuery(byte rid, byte cmd, byte sub) => new byte[] { rid, cmd, sub };

    /// <summary>
    /// Réponse d'un clavier / d'une souris. <paramref name="raw"/> : rapport lu (identifiant 0 déjà retiré par la couche HID).
    /// Renvoie le rapport complet (identifiant remis en place) pour que les décalages correspondent.
    /// « Invalid » : trop court, on arrête ; « Skip » : autre réponse, on continue à lire.
    /// </summary>
    public static (Kind Result, byte[] Report) ParseReply(byte[] raw, byte rid, byte cmd, byte sub)
    {
        if (raw.Length < 11) return (Kind.Invalid, Array.Empty<byte>());
        var r = rid == 0 ? new byte[] { 0 }.Concat(raw).ToArray() : raw;
        if (r.Length < 12) return (Kind.Skip, Array.Empty<byte>());
        if (r[1] == 0xFF && r[2] == 0xAA) return (Kind.Asleep, r);
        if (r[1] == cmd && r[2] == sub) return (Kind.Ok, r);
        return (Kind.Skip, Array.Empty<byte>());
    }

    /// <summary>Remplit niveau et charge depuis une réponse « Ok » ; asleep = niveau indisponible (0 ne veut pas dire vide).</summary>
    public static (int Percent, bool Charging, bool Asleep) ReadLevel(Model m, byte[] r)
    {
        if (m.Offset >= r.Length || r.Length <= 10) return (-1, false, true);
        if (m.Kind == ModelKind.Keyboard)
            return (r[m.Offset] <= 100 ? r[m.Offset] : -1, r[9] == 1, false);
        int v = r[m.Offset] * m.Scale;
        return v is > 0 and <= 100 ? (v, r[10] != 0, false) : (-1, r[10] != 0, true);
    }

    /// <summary>Identifiants des appareils appairés à l'Omni Receiver : créneaux de 4 octets à partir de l'octet 5, 0 = fin.</summary>
    public static List<ushort> ParseOmniList(byte[] r)
    {
        var list = new List<ushort>();
        for (int at = 5; at + 1 < r.Length; at += 4)
        {
            ushort pid = (ushort)(r[at] | (r[at + 1] << 8));
            if (pid == 0) break;
            list.Add(pid);
        }
        return list;
    }

    /// <summary>Réponse des casques (rapport CC, ≥ 8 octets) à la sous-commande <paramref name="cmd"/>.</summary>
    public static (Kind Result, byte[] Report) ParseHeadsetReply(byte[] r, byte cmd)
    {
        if (r.Length < 8) return (Kind.Invalid, Array.Empty<byte>());
        if (r[0] == 0xCC && r[1] == 0xFF && r[2] == 0xAA) return (Kind.Asleep, r);
        if (r[0] == 0xCC && r[1] == 0x12)
        {
            if (r[5] == 0xFF && r[6] == 0xAA) return (Kind.Asleep, r);
            if (r[2] == cmd) return (Kind.Ok, r);
        }
        return (Kind.Skip, Array.Empty<byte>());
    }

    /// <summary>Niveau d'un casque Pelta / Delta II : FE (inconnu) → -1.</summary>
    public static int HeadsetLevel(byte[] r) => r.Length > 6 && r[6] <= 100 ? r[6] : -1;

    /// <summary>Cetra Open : niveaux gauche / droite / boîtier (0 ou > 100 = non communiqué, -1).</summary>
    public static (int Left, int Right, int Case) CetraLevels(byte[] r)
    {
        int L(int at) => r.Length > at && r[at] > 0 && r[at] <= 100 ? r[at] : -1;
        return (L(5), L(6), L(7));
    }

    public static string CetraDetail(int left, int right, int box)
    {
        var parts = new List<string>();
        if (left >= 0) parts.Add($"L {left}%");
        if (right >= 0) parts.Add($"R {right}%");
        if (box >= 0) parts.Add($"case {box}%");
        return string.Join(", ", parts);
    }

    static bool PathHas(string path, string tag) => path.Contains(tag, StringComparison.OrdinalIgnoreCase);

    static (Kind, byte[]) Query(HidDev d, byte rid, byte cmd, byte sub)
    {
        d.Flush();
        if (!d.Write(BuildQuery(rid, cmd, sub))) return (Kind.Invalid, Array.Empty<byte>());
        long end = Environment.TickCount64 + 500;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return (Kind.Invalid, Array.Empty<byte>());
            var raw = d.Read(left);
            if (raw == null) return (Kind.Invalid, Array.Empty<byte>());
            var p = ParseReply(raw, rid, cmd, sub);
            if (p.Result == Kind.Skip) continue;
            return p;
        }
    }

    static (Kind, byte[]) HeadsetQuery(HidDev d, byte cmd)
    {
        d.Flush();
        if (!d.Write(0xCC, 0x12, cmd)) return (Kind.Invalid, Array.Empty<byte>());
        long end = Environment.TickCount64 + 1200; // la première réponse après une reconnexion peut prendre ~750 ms
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return (Kind.Invalid, Array.Empty<byte>());
            var r = d.Read(left);
            if (r == null) return (Kind.Invalid, Array.Empty<byte>());
            var p = ParseHeadsetReply(r, cmd);
            if (p.Result == Kind.Skip) continue;
            return p;
        }
    }

    static DeviceStatus? ReadModel(HidDev d, Model m)
    {
        var (res, r) = Query(d, m.ReportId, 0x12, m.Kind == ModelKind.Keyboard ? (byte)0x01 : (byte)0x07);
        if (res is Kind.Invalid or Kind.Skip) return null;
        var s = new DeviceStatus
        {
            Key = "rog:" + HidUtil.Hex4(m.Pid) + (m.Kind == ModelKind.Keyboard ? ":kb" : ":ms"),
            Name = m.Name,
            Kind = m.Kind == ModelKind.Keyboard ? DeviceKind.Keyboard : DeviceKind.Mouse,
            Via = m.ReportId != 0 ? "ROG Omni Receiver" : "2.4 GHz receiver / USB",
        };
        if (res == Kind.Asleep) { s.Connected = false; return s; }
        var (pct, chg, asleep) = ReadLevel(m, r);
        if (asleep) s.Connected = false; else s.Percent = pct;
        s.Charging = chg;
        return s;
    }

    static void AddOnce(PollContext ctx, HashSet<string> seen, DeviceStatus s)
    {
        if (seen.Add(s.Name)) { s.Vid = Vid; ctx.Out.Add(s); }
    }

    static void PollOmni(PollContext ctx, HashSet<string> seen)
    {
        if (ctx.IsClaimed(Vid, Omni)) return;
        var list = ctx.Find(Vid, Omni, c => c.Iface == 2 && c.OutLen >= 12 && PathHas(c.Path, "col01"));
        if (list.Count == 0) return;
        using var d = new HidDev();
        if (!d.Open(list[0])) return;
        var (res, r) = Query(d, 0x01, 0xA0, 0x00);
        if (res != Kind.Ok) return;
        ctx.Claim(Vid, Omni);
        foreach (var pid in ParseOmniList(r))
        {
            var m = Models.FirstOrDefault(x => x.Pid == pid && x.ReportId != 0); // seule l'entrée « canal Omni » du modèle
            if (m == null) continue;
            foreach (var c in ctx.Find(Vid, Omni, c => c.Iface == 2 && c.OutLen >= 12 && PathHas(c.Path, m.Col!)))
            {
                using var dev = new HidDev();
                if (!dev.Open(c)) continue;
                var s = ReadModel(dev, m);
                if (s != null) { AddOnce(ctx, seen, s); break; }
            }
        }
    }

    static IEnumerable<HidColl> HeadsetColls(PollContext ctx, ushort pid) =>
        ctx.Find(Vid, pid, c => c.Iface == 3 && c.Page == 0xFF00 && c.Usage == 0x0001 && c.InLen >= 8 && c.OutLen >= 8);

    static void PollHeadsets(PollContext ctx)
    {
        foreach (var (pid, name) in Headsets)
        {
            if (ctx.IsClaimed(Vid, pid)) continue;
            foreach (var c in HeadsetColls(ctx, pid))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var (res, r) = HeadsetQuery(d, 0x07);
                if (res is Kind.Invalid or Kind.Skip) continue;
                var s = new DeviceStatus { Key = "rog:" + HidUtil.Hex4(pid) + ":hs", Name = name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
                if (res == Kind.Asleep) s.Connected = false;
                else
                {
                    int lvl = HeadsetLevel(r);
                    if (lvl >= 0) s.Percent = lvl; else s.Connected = false; // FE = niveau pas encore connu
                    var (cr, ch) = HeadsetQuery(d, 0x08);
                    if (cr == Kind.Ok && ch.Length > 5) s.Charging = ch[5] == 1;
                }
                ctx.Add(s, Vid, pid);
                break;
            }
        }
    }

    static void PollCetraOpen(PollContext ctx)
    {
        if (ctx.IsClaimed(Vid, CetraOpen)) return;
        foreach (var c in HeadsetColls(ctx, CetraOpen))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            var (res, r) = HeadsetQuery(d, 0x12);
            if (res is Kind.Invalid or Kind.Skip) continue;
            var s = new DeviceStatus { Key = "rog:" + HidUtil.Hex4(CetraOpen) + ":hs", Name = "ROG Cetra Open Wireless", Kind = DeviceKind.Headset, Via = "2.4 GHz receiver" };
            var (l, rt, box) = res == Kind.Ok ? CetraLevels(r) : (-1, -1, -1);
            if (l < 0 && rt < 0) s.Connected = false;
            else
            {
                s.Percent = l < 0 ? rt : rt < 0 ? l : Math.Min(l, rt);
                s.Detail = CetraDetail(l, rt, box);
            }
            ctx.Add(s, Vid, CetraOpen);
            break;
        }
    }

    // Récepteur dont aucun appareil n'est appairé : il répond aussi « en veille » ; on n'affiche un modèle que s'il a vraiment répondu.
    static readonly HashSet<ushort> Awake = new();

    public static void Poll(PollContext ctx)
    {
        var seen = new HashSet<string>(); // une entrée par nom (filaire + récepteur présents pendant la charge)
        PollHeadsets(ctx);
        PollCetraOpen(ctx);
        PollOmni(ctx, seen);
        foreach (var m in Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c =>
                     {
                         if (c.OutLen < 12 || c.Page < 0xFF00) return false;
                         if (m.Iface >= 0 && c.Iface != m.Iface) return false;
                         return m.Col == null || PathHas(c.Path, m.Col);
                     }))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                var s = ReadModel(d, m);
                if (s == null) continue;
                if (m.ReportId != 0 && !s.Connected && !Awake.Contains(m.Pid)) continue;
                if (m.ReportId != 0 && s.Connected) Awake.Add(m.Pid);
                AddOnce(ctx, seen, s);
                ctx.Claim(Vid, m.Pid);
                break;
            }
        }
    }
}
