// Souris RAWM (Leviathan V4, familles ES21 / ER21 / SA-* / G11) et SADES Dagger sur leurs récepteurs 2,4 GHz ou leur câble USB (VID Nordic 1915).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/rawm.cpp et rawm_protocol.h, porté en C# — voir NOTICE.md.
// Liste de produits : table d'appareils de RAWM HUB (miracletek.net/game/device.php). Testé : Leviathan V4 (234A) sur le RAWM HS Receiver (1915:2346). Seules les requêtes que l'application RAWM HUB envoie à la
// connexion sont utilisées : la requête d'infos appareil et les « ping ». Non couverts : Leviathan V4 GT / NYX60 (VID 373E, autre protocole) et les claviers HS.
// Protocole « Hub Web 2.0 » (hub.miracletek.net/hub/js/library.min.js : send_event_query, send_event_ping, read_event, parse_device_info ; vérifié sur un Leviathan V4) :
//  * Rapports de 64 octets non numérotés sur la collection vendeur FF00:0001. Octet 0 d'un paquet : type (deux bits hauts) | taille de charge (six bits bas). 0x80 = données d'évènement, 0x40 = taille de tampon
//    (contrôle de flux), 0xC0 | n = « étendu » : l'octet suivant est un paquet pour le canal ESB n (la souris derrière un récepteur est le canal 0).
//  * Les données d'évènement forment un flot d'octets. Un évènement commence par FF FF FF FF (appareil → hôte seulement), puis [cmd | ((len >> 4) & 0xF0), len & 0xFF, corps…], len comptant les deux octets d'en-tête.
//  * L'appareil ne répond que tant que l'hôte envoie des paquets : l'hôte envoie des « ping ».
//  CMD_QUERY (0x01) et CMD_PING (0x0E) ; CMD_QUERY_RESULT (0x02) porte un objet JSON avec « battery » (%) et « chr » (1 = en charge) ; CMD_NOTIFY (0x0B) type 0x17 porte [batterie, charge] quand cela change.
namespace BatteryTray.Providers.Hid;

public static class RawmDriver
{
    const ushort Vid = 0x1915;
    public const int NoChannel = -1;
    const byte CmdQuery = 0x01, CmdQueryResult = 0x02, CmdNotify = 0x0B, CmdPing = 0x0E, NotifyBattery = 0x17, OsPc = 0x03;

    static readonly HashSet<ushort> Receivers = new() { 0x232A, 0x232B, 0x2335, 0x2336, 0x233B, 0x233C, 0x2346, 0x2347, 0x234B };
    static readonly HashSet<ushort> Mice = new()
    {
        0x2328, 0x2329, 0x232C, 0x232D, 0x232E, 0x232F, 0x2330, 0x2331, 0x2332, 0x2333, 0x2334, 0x2337, 0x2338, 0x2339, 0x233A, 0x233E, 0x233F, 0x2340, 0x2341, 0x2343, 0x2344, 0x2348, 0x2349, 0x234A, 0x2352,
    };

    public static bool IsReceiver(ushort pid) => Receivers.Contains(pid);
    public static bool IsMouse(ushort pid) => Mice.Contains(pid);

    /// <summary>Rapport de sortie (avec l'identifiant 0 en tête) portant un évènement, éventuellement pour un canal ESB.</summary>
    public static byte[] Packet(byte[] evt, int channel = NoChannel)
    {
        var p = new List<byte> { 0x00 };
        if (channel >= 0) p.Add((byte)(0xC0 | (channel & 0x3F)));
        p.Add((byte)(0x80 | (evt.Length & 0x3F)));
        p.AddRange(evt);
        return p.ToArray();
    }

    /// <summary>send_event_query : [CMD_QUERY, long, OS_PC, 0, 0, secondes Unix (8 octets, petit-boutiste)].</summary>
    public static byte[] QueryEvent(ulong unixSeconds)
    {
        var e = new List<byte> { CmdQuery, 0, OsPc, 0, 0 };
        for (int i = 0; i < 8; i++) e.Add((byte)(unixSeconds >> (8 * i)));
        e[1] = (byte)e.Count;
        return e.ToArray();
    }

    /// <summary>send_event_ping tel que l'application le transmet (sa chaîne de synchro se réduit à un octet nul).</summary>
    public static byte[] PingEvent() => new byte[] { CmdPing, 4, 0, 0 };

    public sealed class Status { public bool HaveInfo; public string Json = ""; public int Percent = -1; public bool Charging; }

    /// <summary>Valeur entière de "clé": dans un objet JSON plat.</summary>
    public static bool JsonInt(string json, string key, out long value)
    {
        value = 0;
        string k = "\"" + key + "\":";
        int p = json.IndexOf(k, StringComparison.Ordinal);
        if (p < 0) return false;
        int i = p + k.Length, start = i;
        if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
        while (i < json.Length && char.IsAsciiDigit(json[i])) i++;
        return i > start && long.TryParse(json.AsSpan(start, i - start), out value);
    }

    public static string JsonString(string json, string key)
    {
        string k = "\"" + key + "\":\"";
        int p = json.IndexOf(k, StringComparison.Ordinal);
        if (p < 0) return "";
        p += k.Length;
        int e = json.IndexOf('"', p);
        return e < 0 ? "" : json.Substring(p, e - p);
    }

    /// <summary>Reconstitue le flot d'évènements d'un canal et décode les évènements utiles.</summary>
    public sealed class Stream
    {
        readonly List<byte> _buf = new();
        public Status Status { get; } = new();

        public void Append(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++) _buf.Add(data[offset + i]);
            if (_buf.Count > 8192) _buf.RemoveRange(0, _buf.Count - 4096);
            Parse();
        }

        void Parse()
        {
            int o = 0;
            while (o + 6 <= _buf.Count)
            {
                if (!(_buf[o] == 0xFF && _buf[o + 1] == 0xFF && _buf[o + 2] == 0xFF && _buf[o + 3] == 0xFF)) { o++; continue; }
                int h = o + 4;
                int cmd = _buf[h] & 0x0F;
                int len = ((_buf[h] << 4) & 0xF00) | _buf[h + 1];
                if (len < 2) { o = h; continue; }
                if (h + len > _buf.Count) break; // on attend la suite
                int n = len - 2;
                if (cmd == CmdQueryResult) OnInfo(_buf.GetRange(h + 2, n).ToArray());
                else if (cmd == CmdNotify && n >= 3 && _buf[h + 2] == NotifyBattery && _buf[h + 3] <= 100) { Status.Percent = _buf[h + 3]; Status.Charging = _buf[h + 4] == 1; }
                o = h + len;
            }
            _buf.RemoveRange(0, o);
        }

        void OnInfo(byte[] body)
        {
            string s = System.Text.Encoding.Latin1.GetString(body);
            int z = s.IndexOf('\0'); if (z >= 0) s = s[..z];
            if (s.Length == 0 || s[0] != '{') return;
            Status.HaveInfo = true; Status.Json = s;
            if (JsonInt(s, "battery", out long v) && v is >= 0 and <= 100) Status.Percent = (int)v;
            if (JsonInt(s, "chr", out v)) Status.Charging = v == 1;
        }
    }

    /// <summary>Aiguille un rapport d'entrée (identifiant déjà retiré) vers le flot du récepteur ou celui d'un canal.</summary>
    public sealed class Link
    {
        public Stream Direct { get; } = new();   // l'appareil USB lui-même (récepteur, ou souris sur son câble)
        public Stream Channel0 { get; } = new(); // la souris derrière un récepteur

        public void Feed(byte[] r)
        {
            if (r.Length == 0) return;
            var s = Direct; int off = 0;
            if ((r[0] & 0xC0) == 0xC0)
            {
                if ((r[0] & 0x3F) != 0) return;
                s = Channel0; off = 1;
                if (r.Length <= off) return;
            }
            if ((r[off] & 0xC0) != 0x80) return; // contrôle de flux / paquets bruts
            int n = r[off] & 0x3F;
            if (n > r.Length - off - 1) n = r.Length - off - 1;
            s.Append(r, off + 1, n);
        }
    }

    static void Pump(HidDev d, Link link, int ms, long deadline)
    {
        long end = Math.Min(Environment.TickCount64 + ms, deadline);
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return;
            var r = d.Read(left);
            if (r == null) return;
            link.Feed(r);
        }
    }

    // Demande les infos de <channel> (ou de l'appareil USB lui-même) et continue d'envoyer des ping, l'appareil ne renvoyant des données que tant que l'hôte envoie.
    static Status? Query(HidDev d, Link link, int channel, long deadline)
    {
        var s = channel == NoChannel ? link.Direct : link.Channel0;
        if (!d.Write(Packet(QueryEvent((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), channel), 200)) return null;
        while (Environment.TickCount64 < deadline)
        {
            if (!d.Write(Packet(PingEvent(), channel), 200)) return null;
            Pump(d, link, 80, deadline);
            if (s.Status.HaveInfo) return s.Status;
        }
        return null;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            bool receiver = IsReceiver(pid);
            if (!receiver && !IsMouse(pid)) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0xFF00 && c.Usage == 0x0001 && c.InLen == 65 && c.OutLen == 65))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                d.Flush();
                // Salut du récepteur (~5 ms), puis les ~550 octets d'infos de la souris par ESB (~200 ms sur le Leviathan V4). Une échéance absolue couvre les deux ; une souris endormie l'utilise en entier.
                long deadline = Environment.TickCount64 + 1200;
                var link = new Link();
                var info = Query(d, link, NoChannel, deadline);
                if (info == null) continue;
                if (receiver)
                {
                    // Le récepteur doit répondre d'abord ; la souris derrière lui est alors le canal ESB 0.
                    info = Query(d, link, 0, deadline);
                    if (info == null) { ctx.Claim(vid, pid); break; } // récepteur trouvé, souris éteinte ou hors de portée
                }
                if (info.Percent < 0) continue;
                JsonInt(info.Json, "pi", out long mousePid);
                string name = JsonString(info.Json, "dn");
                if (name.Length == 0) name = "wireless mouse";
                if (!name.Contains("RAWM", StringComparison.Ordinal) && !name.Contains("SADES", StringComparison.Ordinal)) name = "RAWM " + name;
                var s = new DeviceStatus
                {
                    Key = "rawm:" + HidUtil.Hex4(mousePid is > 0 and <= 0xFFFF ? (int)mousePid : pid), Name = name, Kind = DeviceKind.Mouse,
                    Via = receiver ? "2.4 GHz receiver" : "USB cable", Percent = info.Percent, Charging = info.Charging,
                };
                // Une souris en charge peut être joignable par son câble et par le récepteur : une seule entrée, l'état de charge du câble l'emporte.
                var existing = ctx.Out.FirstOrDefault(o => o.Key == s.Key);
                if (existing == null) ctx.Add(s, vid, pid);
                else { if (!receiver) { s.Vid = vid; ctx.Out[ctx.Out.IndexOf(existing)] = s; } ctx.Claim(vid, pid); }
                break;
            }
        }
    }
}
