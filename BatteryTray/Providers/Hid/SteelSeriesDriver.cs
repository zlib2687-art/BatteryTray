// SteelSeries : casques et souris sans fil (VID 1038).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/steelseries.cpp, porté en C# — voir NOTICE.md.
// Protocoles des casques : github.com/Sapd/HeadsetControl ; des souris : github.com/flozz/rivalcfg (Aerox 3 Wireless Gen 2 : ticket
// rivalcfg n° 276), recoupés avec github.com/yurtemre7/steel-mouse. Lecture seule : une requête de niveau, aucun réglage écrit.
using System.Diagnostics;

namespace BatteryTray.Providers.Hid;

public static class SteelSeriesDriver
{
    const ushort Vid = 0x1038;

    static DeviceStatus Headset(ushort pid, string name) => new()
    {
        Key = "steelseries:" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver",
    };

    static DeviceStatus Mouse(ushort pid, string name, bool wired) => new()
    {
        Key = "steelseries:" + HidUtil.Hex4(pid), Name = name, Kind = DeviceKind.Mouse, Via = wired ? "USB cable" : "2.4 GHz receiver",
    };

    // ------------------------------------------------------------------ analyse des réponses (logique pure, testée)

    /// <summary>Arctis 1 / 1 Xbox / 7X : requête 06 12 → [2]==01 hors ligne, [3] pourcentage.</summary>
    public static DeviceStatus? ParseArctis1(ushort pid, byte[] r)
    {
        if (r.Length < 4) return null;
        var s = Headset(pid, "SteelSeries Arctis 1 Wireless");
        s.Connected = r[2] != 0x01;
        if (s.Connected) s.Percent = HidUtil.Clamp100(r[3]);
        return s;
    }

    /// <summary>Arctis 7 / 7 2019 / Pro 2019 : requête 06 18 → [2] pourcentage.</summary>
    public static DeviceStatus? ParseArctis7(ushort pid, byte[] r)
    {
        if (r.Length < 3) return null;
        var s = Headset(pid, pid == 0x1252 ? "SteelSeries Arctis Pro" : "SteelSeries Arctis 7");
        s.Percent = HidUtil.Clamp100(r[2]);
        s.Connected = r[2] != 0;
        return s;
    }

    /// <summary>Arctis 9 : requête 00 20 → [3] brut (0x64..0x9A), [4]==01 en charge.</summary>
    public static DeviceStatus? ParseArctis9(ushort pid, byte[] r)
    {
        if (r.Length < 5) return null;
        var s = Headset(pid, "SteelSeries Arctis 9");
        s.Percent = HidUtil.MapTo100(r[3], 0x64, 0x9A);
        s.Charging = r[4] == 0x01;
        return s;
    }

    /// <summary>Arctis Pro Wireless : 41 AA → [0]==02 hors ligne (04 en ligne) ; 40 AA → [0] niveau 0..4.</summary>
    public static DeviceStatus? ParseArctisProWireless(ushort pid, byte[] status, byte[]? level)
    {
        if (status.Length == 0) return null;
        var s = Headset(pid, "SteelSeries Arctis Pro Wireless");
        s.Connected = status[0] != 0x02;
        if (s.Connected && level is { Length: > 0 } && level[0] <= 4) s.Percent = HidUtil.MapTo100(level[0], 0, 4);
        return s;
    }

    /// <summary>Arctis Nova Pro Wireless : 06 B0 → [15] état (01 hors ligne, 02 en charge), [6] niveau 0..8.</summary>
    public static DeviceStatus? ParseNovaProWireless(ushort pid, byte[] r)
    {
        if (r.Length < 16) return null;
        var s = Headset(pid, "SteelSeries Arctis Nova Pro Wireless");
        s.Connected = r[15] != 0x01;
        s.Charging = r[15] == 0x02;
        if (s.Connected) s.Percent = HidUtil.MapTo100(r[6], 0, 8);
        return s;
    }

    static readonly HashSet<ushort> Arctis7Plus = new() { 0x220E, 0x2212, 0x2216, 0x2236 };
    static readonly HashSet<ushort> Nova3PAndNova5 = new() { 0x2269, 0x226D, 0x2232, 0x2253 };
    static readonly HashSet<ushort> Nova5 = new() { 0x2232, 0x2253 };
    static readonly HashSet<ushort> NovaDiscrete = new() { 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4 };

    /// <summary>Famille Nova : 00 B0 → bloc d'état dont la disposition dépend du modèle.</summary>
    public static DeviceStatus? ParseNovaFamily(ushort pid, byte[] r)
    {
        if (r.Length < 7) return null;
        DeviceStatus s;
        if (Arctis7Plus.Contains(pid))
        {
            s = Headset(pid, "SteelSeries Arctis 7+");
            s.Connected = r[1] != 0x01;
            s.Percent = HidUtil.MapTo100(r[2], 0, 4);
            s.Charging = r[3] == 0x01;
        }
        else if (Nova3PAndNova5.Contains(pid))
        {
            s = Headset(pid, Nova5.Contains(pid) ? "SteelSeries Arctis Nova 5" : "SteelSeries Arctis Nova 3P Wireless");
            s.Connected = r[1] != 0x02;
            s.Percent = HidUtil.Clamp100(r[3]);
            s.Charging = r[4] == 0x01;
        }
        else if (pid == 0x230A) // GameBuds : le plus bas des écouteurs sortis du boîtier
        {
            s = Headset(pid, "SteelSeries Arctis GameBuds");
            int lvl = -1;
            if (r[3] == 0x03) lvl = r[5];
            if (r[4] == 0x03) lvl = lvl < 0 ? r[6] : Math.Min(lvl, r[6]);
            s.Connected = lvl >= 0;
            s.Percent = lvl >= 0 ? HidUtil.Clamp100(lvl) : -1;
        }
        else // Arctis Nova 7 / 7X / 7P et variantes
        {
            bool discrete = NovaDiscrete.Contains(pid);
            s = Headset(pid, "SteelSeries Arctis Nova 7");
            s.Connected = r[3] != 0x00;
            s.Charging = r[3] == 0x01 || r[3] == 0x02;
            s.Percent = discrete ? HidUtil.MapTo100(r[2], 0, 4) : HidUtil.Clamp100(r[2]);
        }
        if (!s.Connected) s.Percent = -1;
        return s;
    }

    public sealed record AeroxModel(ushort Pid, ushort ReceiverPid, string Name, bool Wired, bool PercentLayout);

    public static readonly AeroxModel[] Aerox =
    {
        new(0x1838, 0x1838, "SteelSeries Aerox 3 Wireless", false, false),
        new(0x183A, 0x1838, "SteelSeries Aerox 3 Wireless", true, false),
        new(0x1878, 0x1878, "SteelSeries Aerox 3 Wireless CS2 Dragon Lore Edition", false, false),
        new(0x187A, 0x1878, "SteelSeries Aerox 3 Wireless CS2 Dragon Lore Edition", true, false),
        new(0x1890, 0x1890, "SteelSeries Aerox 3 Wireless Gen 2", false, true),
        new(0x1892, 0x1890, "SteelSeries Aerox 3 Wireless Gen 2", true, true),
        new(0x1852, 0x1852, "SteelSeries Aerox 5 Wireless", false, false),
        new(0x1854, 0x1852, "SteelSeries Aerox 5 Wireless", true, false),
        new(0x1858, 0x1858, "SteelSeries Aerox 9 Wireless", false, false),
        new(0x185A, 0x1858, "SteelSeries Aerox 9 Wireless", true, false),
        new(0x1840, 0x1840, "SteelSeries Prime Wireless", false, false),
        new(0x1842, 0x1840, "SteelSeries Prime Wireless", true, false),
        new(0x1860, 0x1860, "SteelSeries Aerox 5 Wireless Diablo IV Edition", false, false), // matériel non testé
    };

    /// <summary>
    /// Aerox / Prime Wireless : 92 sur câble, D2 via le récepteur. Deux dispositions de réponse :
    ///  - paliers : [1] = (niveau/5 + 1) | charge&lt;&lt;7, et 00 = souris éteinte ou en veille ;
    ///  - pourcentage (Aerox 3 Wireless Gen 2) : [1] 00 sur batterie, 01 en charge, 02 chargée câble branché ; [2] = 0..100, ou FF
    ///    tant que la souris n'a pas mesuré. 00 00 via le récepteur = souris éteinte.
    /// Une souris éteinte ou en veille renvoie null : rien n'est affiché (l'application garde sa dernière valeur, en gris).
    /// </summary>
    public static DeviceStatus? ParseAerox(AeroxModel m, byte[] r)
    {
        if (r.Length < (m.PercentLayout ? 3 : 2)) return null;
        if (m.PercentLayout && r[1] > 0x02) return null; // pas un octet d'état Gen 2
        bool off = m.PercentLayout ? (!m.Wired && r[1] == 0x00 && r[2] == 0x00) : (r[1] & 0x7F) == 0;
        if (off) return null;
        var s = Mouse(m.Pid, m.Name, m.Wired);
        s.Key = "steelseries:" + HidUtil.Hex4(m.ReceiverPid);
        if (m.PercentLayout)
        {
            s.Charging = r[1] != 0x00;
            if (r[2] <= 100) s.Percent = r[2]; // FF (pas encore mesuré) et niveaux invalides restent inconnus
        }
        else if ((r[1] & 0x7F) <= 21) // seuls les niveaux 1..21 représentent 0..100 %
        {
            s.Percent = ((r[1] & 0x7F) - 1) * 5;
            s.Charging = (r[1] & 0x80) != 0;
        }
        return s;
    }

    /// <summary>Rival 3 Wireless (+ Gen 2), Rival 650 : AA 01 → [0] pourcentage, [2] en charge.</summary>
    public static DeviceStatus? ParseRival(ushort pid, byte[] r)
    {
        if (r.Length < 3) return null;
        bool rival650 = pid is 0x172B or 0x1726;
        var s = Mouse(pid, rival650 ? "SteelSeries Rival 650 Wireless" : pid == 0x1872 ? "SteelSeries Rival 3 Wireless Gen 2" : "SteelSeries Rival 3 Wireless", pid == 0x172B);
        if (rival650) s.Key = "steelseries:172B"; // une seule entrée, câble ou récepteur
        s.Percent = HidUtil.Clamp100(r[0]);
        s.Charging = r[2] != 0;
        return s;
    }

    /// <summary>Rival Pro (UWB) : 92 → 92 00 &lt;% souris&gt; &lt;drapeau&gt; &lt;% batterie de rechange&gt;.</summary>
    public static DeviceStatus? ParseRivalPro(ushort pid, byte[] r)
    {
        if (r.Length < 3 || r[0] != 0x92 || r[1] != 0x00 || r[2] > 100) return null;
        var s = Mouse(pid, "SteelSeries Rival Pro", false);
        s.Via = "UWB base station";
        s.Percent = r[2];
        return s;
    }

    // ------------------------------------------------------------------ échanges avec l'appareil

    static byte[]? WriteRead(HidDev d, byte[] req, int timeoutMs = 1000)
    {
        d.Flush();
        if (!d.Write(req)) return null;
        var r = d.Read(timeoutMs);
        return r is { Length: > 0 } ? r : null;
    }

    static DeviceStatus? Arctis1(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x06, 0x12 }) is { } r ? ParseArctis1(pid, r) : null;
    static DeviceStatus? Arctis7(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x06, 0x18 }) is { } r ? ParseArctis7(pid, r) : null;
    static DeviceStatus? Arctis9(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x00, 0x20 }) is { } r ? ParseArctis9(pid, r) : null;
    static DeviceStatus? NovaProWireless(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x06, 0xB0 }) is { } r ? ParseNovaProWireless(pid, r) : null;
    static DeviceStatus? NovaFamily(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x00, 0xB0 }) is { } r ? ParseNovaFamily(pid, r) : null;

    // HeadsetControl écrit 41 AA via hidapi, qui prend le premier octet pour un identifiant de rapport : cela n'atteint la base que
    // sous Linux. Sous Windows l'interface 0 ne numérote pas ses rapports : la commande doit suivre un identifiant 0, sinon elle n'arrive jamais.
    static byte[]? ArctisProCmd(HidDev d, byte cmd) =>
        WriteRead(d, new byte[] { 0x00, cmd, 0xAA }) ?? WriteRead(d, new byte[] { cmd, 0xAA }); // au cas où un micrologiciel numérote ses rapports

    static DeviceStatus? ArctisProWireless(HidDev d, ushort pid)
    {
        var status = ArctisProCmd(d, 0x41);
        if (status == null) return null;
        var level = status[0] != 0x02 ? ArctisProCmd(d, 0x40) : null;
        return ParseArctisProWireless(pid, status, level);
    }

    /// <summary>
    /// Envoie la commande et renvoie le premier rapport qui la répète. SteelSeries GG envoie ses propres commandes sur la même
    /// interface et le récepteur ne répond qu'à la dernière : la requête est renvoyée toutes les 100 ms jusqu'à l'écho, dans la limite
    /// d'une seconde. Les autres rapports sont ignorés, sauf 40 FF (le récepteur ne joint pas la souris, éteinte par exemple) qui arrête l'attente.
    /// </summary>
    static byte[]? QueryEcho(HidDev d, byte cmd, int minLen)
    {
        d.Flush();
        long start = Environment.TickCount64, end = start + 1000, resend = start;
        for (; ; )
        {
            long now = Environment.TickCount64;
            if (now >= end) return null;
            if (now >= resend)
            {
                if (!d.Write(0x00, cmd)) return null;
                resend = now + 100;
            }
            int wait = (int)(Math.Min(resend, end) - now);
            if (wait <= 0) continue;
            var r = d.Read(wait);
            if (r == null) continue;
            if (r.Length >= minLen && r[0] == cmd) return r;
            if (r.Length >= 2 && r[0] == 0x40 && r[1] == 0xFF) return null;
        }
    }

    static DeviceStatus? AeroxPoll(HidDev d, ushort pid)
    {
        var m = Aerox.FirstOrDefault(a => a.Pid == pid);
        if (m == null) return null;
        var r = QueryEcho(d, m.Wired ? (byte)0x92 : (byte)0xD2, m.PercentLayout ? 3 : 2);
        return r == null ? null : ParseAerox(m, r);
    }

    static DeviceStatus? Rival(HidDev d, ushort pid) => WriteRead(d, new byte[] { 0x00, 0xAA, 0x01 }, 300) is { } r ? ParseRival(pid, r) : null;

    static DeviceStatus? RivalPro(HidDev d, ushort pid)
    {
        d.Flush();
        if (!d.Write(0x00, 0x92)) return null;
        var sw = Stopwatch.StartNew();
        byte[]? r;
        do
        {
            r = d.Read((int)Math.Max(1, 500 - sw.ElapsedMilliseconds));
            if (r == null || r.Length < 3) return null;
        } while (!(r[0] == 0x92 && r[1] == 0x00) && sw.ElapsedMilliseconds < 500);
        return ParseRivalPro(pid, r);
    }

    sealed record Model(ushort[] Pids, Func<HidColl, bool> Accept, Func<HidDev, ushort, DeviceStatus?> Poll);

    static bool PageFF43(HidColl c) => c.Page == 0xFF43 && c.Usage == 0x0202;
    static bool PageFFC0(HidColl c) => c.Page == 0xFFC0 && c.Usage == 0x0001;
    static Func<HidColl, bool> Interface(int n) => c => c.Iface == n && c.OutLen > 0;

    static readonly Model[] Models =
    {
        new(new ushort[] { 0x12B3, 0x12B6, 0x12D7 }, PageFF43, Arctis1),
        new(new ushort[] { 0x1260, 0x12AD, 0x1252 }, PageFF43, Arctis7),
        new(new ushort[] { 0x12C2 }, Interface(0), Arctis9),
        new(new ushort[] { 0x1290 }, Interface(0), ArctisProWireless),
        new(new ushort[] { 0x12E0, 0x225D }, Interface(4), NovaProWireless),
        new(new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236, 0x2269, 0x226D, 0x2232, 0x2253, 0x230A, 0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E,
                           0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7 }, PageFFC0, NovaFamily),
        new(Aerox.Select(a => a.Pid).ToArray(), Interface(3), AeroxPoll),
        new(new ushort[] { 0x1830, 0x1872 }, Interface(3), Rival),
        new(new ushort[] { 0x172B, 0x1726 }, Interface(0), Rival), // Rival 650 : câble, récepteur 2,4 GHz
        new(new ushort[] { 0x1880 }, Interface(2), RivalPro),
    };

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            foreach (var m in Models)
            {
                if (!m.Pids.Contains(pid)) continue;
                foreach (var c in ctx.Find(vid, pid, m.Accept))
                {
                    using var d = new HidDev();
                    if (!d.Open(c)) continue;
                    var s = m.Poll(d, pid);
                    if (s == null) continue;
                    // Aerox/Prime et Rival 650 peuvent répondre sur deux produits : une seule entrée, le câble de préférence.
                    var dup = ctx.Out.FindIndex(o => o.Key == s.Key);
                    if (dup < 0) ctx.Add(s, vid, pid);
                    else
                    {
                        if (s.Via == "USB cable") { s.Vid = vid; ctx.Out[dup] = s; }
                        ctx.Claim(vid, pid);
                    }
                    break;
                }
                break;
            }
        }
    }
}
