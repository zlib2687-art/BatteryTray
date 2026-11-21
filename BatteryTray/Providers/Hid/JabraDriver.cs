// Casques Jabra parlant le protocole GN (GNP), sur leur câble USB ou derrière un récepteur Jabra Link. GNP passe par la collection
// vendeur : page FF00, usage 05 (Link 390, Evolve3) ou 01 (Link 380, Evolve2), rapport 05, 64 octets. Au lieu d'une liste d'identifiants,
// tout produit Jabra ayant cette collection est interrogé ; ce qui diffère d'un produit à l'autre est déterminé à l'exécution.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/jabra.cpp, porté en C# — voir NOTICE.md.
//   requête : [05][dst][src 00][seq][40|len][classe][op][charge]     len = octets après l'identifiant de rapport
//   réponse : [05][dst 00][src][seq][C0|len][classe][op][charge]     classe FE = NAK, op = motif
// Adresses : 08 = casque sur son propre câble USB, 01 = récepteur Link, 04 = casque derrière le récepteur.
// Batterie = classe 12 / op 02, charge [drapeaux][niveau] ; drapeaux bit 0 = en charge. Nom = classe 02 / op 00, charge [n][n octets UTF-8].
// Un casque éteint derrière un récepteur répond NAK F3 (pair hors ligne) alors que le récepteur reste branché. Les bases DECT sont exclues.
// Protocole : github.com/Watchdog0x/jabridge et pilote GNP de HeadsetControl. Lecture seule.
using System.Text;

namespace BatteryTray.Providers.Hid;

public static class JabraDriver
{
    const ushort Jabra = 0x0B0E;
    const byte DongleHeadset = 0x04, UsbHeadset = 0x08, NakPeerOffline = 0xF3;

    // Bases et récepteurs DECT (Engage 65/75, PRO 9xx, GO 6400, Link 400, NXT UC-7500, H40), triés.
    static readonly ushort[] DectBases =
    {
        0x1000, 0x1016, 0x1017, 0x1018, 0x1021, 0x1022, 0x1023, 0x1037, 0x1038, 0x1039, 0x1041, 0x1042,
        0x1043, 0x1110, 0x1112, 0x1113, 0x1118, 0x111A, 0x111B, 0x111D, 0x111E, 0x111F, 0x1120, 0x1121,
        0x1122, 0x1123, 0x1124, 0x1126, 0x1131, 0x1132, 0x1133, 0x1134, 0x1135, 0x1136, 0x1140, 0x1141,
        0x1142, 0x1146, 0x1147, 0x1148, 0x114A, 0x114B, 0x114C, 0x114D, 0x114E, 0x114F, 0x1150, 0x1151,
        0xA700, 0xA701, 0xA702, 0xA703, 0xA704, 0xA705, 0xA706, 0xAE60, 0xAE62, 0xAE63,
    };

    public static bool IsDectBase(ushort pid) => Array.BinarySearch(DectBases, pid) >= 0;

    public enum Kind { Ok, Nak, None }

    public static byte[] BuildQuery(byte dst, byte seq, byte cls, byte op) => new byte[] { 0x05, dst, 0x00, seq, 0x46, cls, op };

    /// <summary>
    /// Analyse une réponse à la requête (dst, seq, cls, op). « None » : pas la réponse attendue (on continue à lire) ;
    /// « Nak » : charge = [motif] ; « Ok » : charge de la réponse.
    /// </summary>
    public static (Kind Result, byte[] Payload) ParseReply(byte[] r, byte dst, byte seq, byte cls, byte op)
    {
        if (r.Length < 7) return (Kind.None, Array.Empty<byte>());
        if (r[0] != 0x05 || r[1] != 0x00 || r[2] != dst || r[3] != seq || (r[4] & 0xC0) != 0xC0) return (Kind.None, Array.Empty<byte>());
        if (r[5] == 0xFE) return (Kind.Nak, new[] { r[6] });
        int len = r[4] & 0x3F; // compté à partir de r[1]
        if (r[5] != cls || r[6] != op || len < 6 || len + 1 > r.Length) return (Kind.None, Array.Empty<byte>());
        return (Kind.Ok, r[7..(len + 1)]);
    }

    /// <summary>Charge de la batterie [drapeaux][niveau] → (pourcentage ou -1, en charge) ; null si trop courte.</summary>
    public static (int Percent, bool Charging)? ParseBattery(byte[] p) =>
        p.Length < 2 ? null : (p[1] <= 100 ? p[1] : -1, (p[0] & 0x01) != 0);

    /// <summary>Charge du nom [n][n octets UTF-8] ; chaîne vide si invalide.</summary>
    public static string ParseName(byte[] p)
    {
        if (p.Length == 0 || p[0] == 0 || p[0] + 1 > p.Length) return "";
        var s = Encoding.UTF8.GetString(p, 1, p[0]);
        return new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }

    static byte _seq = 0x20;
    // Adresse du casque par produit (retenue une fois qu'un casque a répondu) et nom par produit (demandé une fois).
    static readonly Dictionary<ushort, byte> Addresses = new();
    static readonly Dictionary<ushort, string> Names = new();
    // Produits qu'on ne réinterroge plus avant cette échéance (long = jamais : tous les NAK, casque filaire).
    static readonly Dictionary<ushort, long> SkipUntil = new();
    const long SilentRetryMs = 5 * 60 * 1000;

    static (Kind, byte[]) Query(HidDev d, byte dst, byte cls, byte op, int timeoutMs)
    {
        byte s = _seq++;
        d.Flush();
        if (!d.Write(BuildQuery(dst, s, cls, op))) return (Kind.None, Array.Empty<byte>());
        long end = Environment.TickCount64 + timeoutMs;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return (Kind.None, Array.Empty<byte>());
            var r = d.Read(left);
            if (r == null) return (Kind.None, Array.Empty<byte>());
            var p = ParseReply(r, dst, s, cls, op);
            if (p.Result != Kind.None) return p;
        }
    }

    public static void Poll(PollContext ctx)
    {
        long now = Environment.TickCount64;
        foreach (var (vid, pid) in ctx.Products(Jabra))
        {
            if (IsDectBase(pid)) continue;
            if (SkipUntil.TryGetValue(pid, out var until) && now < until) continue;
            var colls = ctx.Find(vid, pid, c => c.Page == 0xFF00 && (c.Usage == 0x05 || c.Usage == 0x01) && c.OutLen >= 64 && c.InLen >= 64)
                .OrderByDescending(c => c.Usage == 0x05).ToList(); // collection 05 d'abord
            bool reported = false, probed = false, skippable = true, allNak = true;
            foreach (var c in colls)
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                bool known = Addresses.TryGetValue(pid, out var knownAddr);
                bool dongle = known ? knownAddr == DongleHeadset : (c.Product ?? "").Contains("Link", StringComparison.OrdinalIgnoreCase);
                byte[] order = known ? new[] { knownAddr } : dongle ? new[] { DongleHeadset, UsbHeadset } : new[] { UsbHeadset, DongleHeadset };

                byte[] p = Array.Empty<byte>();
                byte address = 0; bool offline = false;
                if (dongle || order.Length == 1) skippable = false;
                foreach (var a in order)
                {
                    var (rep, pay) = Query(d, a, 0x12, 0x02, 700);
                    if (rep == Kind.Ok && pay.Length >= 2) { address = a; p = pay; break; }
                    // Un récepteur signale son casque hors ligne (F3) plutôt qu'absent ; ailleurs, tout autre NAK = pas de casque à batterie.
                    if (rep == Kind.Nak && a == DongleHeadset && (dongle || pay[0] == NakPeerOffline)) { address = a; offline = true; break; }
                    if (rep != Kind.Nak) allNak = false;
                }
                if (address == 0) { Addresses.Remove(pid); probed = true; continue; }
                Addresses[pid] = address;

                var s = new DeviceStatus
                {
                    Key = "jabra:" + HidUtil.Hex4(pid),
                    Kind = DeviceKind.Headset,
                    Via = address == DongleHeadset ? "Jabra Link dongle" : "USB cable",
                };
                if (!offline)
                {
                    var b = ParseBattery(p)!.Value;
                    s.Percent = b.Percent; s.Charging = b.Charging;
                    if (!Names.ContainsKey(pid))
                    {
                        var (nr, np) = Query(d, address, 0x02, 0x00, 400);
                        var n = nr == Kind.Ok ? ParseName(np) : "";
                        Names[pid] = n.Length == 0 ? "Jabra headset" : n;
                    }
                    s.Name = Names[pid];
                }
                else
                {
                    s.Connected = false; // récepteur présent, casque éteint ou hors de portée
                    s.Name = Names.TryGetValue(pid, out var nm) ? nm : "Jabra headset";
                    Names.Remove(pid);
                }
                ctx.Add(s, vid, pid);
                reported = true;
                break;
            }
            if (!reported && probed && skippable) SkipUntil[pid] = allNak ? long.MaxValue : now + SilentRetryMs;
        }
    }
}
