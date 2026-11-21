// Apple AirPods / Beats (AirPods Max, AirPods Pro 1/2/3 et AirPods 1-5) en Bluetooth.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/airpods.cpp, porté en C# — voir NOTICE.md.
// Windows ne lit pas la batterie des AirPods (elle passe par des commandes HFP propres à Apple) : le repli Bluetooth n'affiche rien pour eux. Ils la diffusent pourtant en clair, plusieurs fois par seconde, dans le message BLE
// d'« appairage de proximité » qu'utilisent les iPhone pour leur fenêtre de batterie : données constructeur de l'entreprise 0x004C (Apple), type 0x07, longueur 0x19 :
//   [0] 07  [1] 19  [2] 01 (mode état appairé ; 00 = mode appairage)  [3..4] modèle (LE, = PID Bluetooth, p. ex. 0x200A AirPods Max, 0x2014 AirPods Pro 2)
//   [5] état : bit 5 = l'écouteur gauche est celui qui diffuse (modèles à deux écouteurs)
//   [6] deux écouteurs : quartet bas = écouteur qui diffuse, quartet haut = l'autre (0-10 = ×10 %, 15 = inconnu) ; batterie unique (AirPods Max, Beats) : quartet bas = batterie (0-10 = ×10 %), quartet haut = 0
//   [7] quartet haut : bit 0 diffusant / unique en charge, bit 1 autre écouteur en charge, bit 2 boîtier en charge (le bit 3 est toujours à 1 sur AirPods Max : 0x80 décharge, 0x90 charge) ; quartet bas : niveau du boîtier
// (disposition d'OpenPods / MagicPodsCore / AirPodsDesktop / librepods, vérifiée sur AirPods Max 200A).
// L'observateur BLE partagé (BleAdverts) démarre à la première vue d'un appareil audio Apple appairé et continue. Les AirPods d'autres personnes diffusent le même message avec des adresses aléatoires : seuls les messages dont le
// modèle correspond au PID d'un appareil appairé sont utilisés, et le signal le plus fort récent l'emporte.
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BatteryTray.Providers.Hid;

public static class AirPodsDriver
{
    public sealed class Advert
    {
        public ushort Model; public bool Single, LeftBroadcasts; public int Left = -1, Right = -1, Box = -1; public bool LeftChg, RightChg, BoxChg; public int Rssi = -127; public long Tick;
        public Advert Clone() => (Advert)MemberwiseClone();
    }

    static int Level(int nibble) => nibble <= 10 ? nibble * 10 : -1;

    // Casques à batterie unique (AirPods Max, Beats Solo / Studio / X / Flex / Powerbeats) : batterie dans le quartet bas de l'octet 6, quartet haut et boîtier à 0 (pas 15) : les décoder en « deux écouteurs » donnerait toujours 0 %.
    public static bool IsSingleBatteryModel(ushort model) => model is 0x200A or 0x201F or 0x202D or 0x2003 or 0x2005 or 0x2006 or 0x2009 or 0x200C or 0x200D or 0x2010 or 0x2017 or 0x2025;

    public static bool ParseProximityTlv(byte[] d, int offset, int n, Advert a)
    {
        // d[1] == 0x19 (25 octets suivent) et d[2] == 0x01 (mode état appairé ; 0x00 = mode appairage).
        if (n < 27 || d[offset] != 0x07 || d[offset + 1] != 0x19 || d[offset + 2] != 0x01) return false;
        var s = d.AsSpan(offset, n);
        a.Model = (ushort)(s[3] | s[4] << 8);
        if ((a.Model & 0xFF00) != 0x2000) return false;
        a.Single = IsSingleBatteryModel(a.Model);
        a.LeftBroadcasts = (s[5] & 0x20) != 0;
        int cur = s[6] & 0x0F, other = s[6] >> 4, chg = s[7] >> 4;
        if (a.Single)
        {
            int pct = Level(cur); bool charging = (chg & 0x01) != 0;
            if (cur == 0 && other is >= 1 and <= 10) { pct = Level(other); charging = (chg & 0x02) != 0; }
            if (pct < 0) return false;
            a.Left = pct; a.LeftChg = charging; a.Right = -1; a.Box = -1;
            return true;
        }
        a.Left = Level(a.LeftBroadcasts ? cur : other);
        a.Right = Level(a.LeftBroadcasts ? other : cur);
        a.LeftChg = a.Left >= 0 && (chg & (a.LeftBroadcasts ? 0x01 : 0x02)) != 0;
        a.RightChg = a.Right >= 0 && (chg & (a.LeftBroadcasts ? 0x02 : 0x01)) != 0;
        a.Box = Level(s[7] & 0x0F);
        a.BoxChg = a.Box >= 0 && (chg & 0x04) != 0;
        return a.Left >= 0 || a.Right >= 0 || a.Box >= 0;
    }

    /// <summary>Parcourt la séquence TLV de continuité d'Apple, au cas où un autre TLV précéderait le 0x07.</summary>
    public static Advert? Parse(byte[] buf)
    {
        for (int i = 0; i + 1 < buf.Length;)
        {
            byte type = buf[i], len = buf[i + 1];
            if (i + 2 + len > buf.Length) break;
            var a = new Advert();
            if (type == 0x07 && ParseProximityTlv(buf, i, 2 + len, a)) return a;
            i += 2 + len;
        }
        return null;
    }

    static readonly object Lock = new();
    static readonly Dictionary<ulong, Advert> Adverts = new();

    public static void OnAdvert(ulong addr, int rssi, byte[] data)
    {
        if (Parse(data) is not { } a) return;
        a.Rssi = rssi; a.Tick = Environment.TickCount64;
        lock (Lock)
        {
            Adverts[addr] = a;
            foreach (var k in Adverts.Where(kv => a.Tick - kv.Value.Tick > 10 * 60 * 1000).Select(kv => kv.Key).ToList()) Adverts.Remove(k); // les adresses tournent : on abandonne les anciennes
        }
    }

    /// <summary>Meilleur message pour un appareil : le modèle doit correspondre si le PID est connu ; le signal le plus fort des 90 dernières secondes l'emporte (ou le dernier des 5 dernières minutes si le Bluetooth classique est connecté).
    /// Quand un écouteur est dans l'oreille et l'autre dans le boîtier ouvert, les deux diffusent séparément : tout côté / boîtier manquant est complété par la dernière diffusion récente de l'autre écouteur.</summary>
    public static Advert? Pick(ushort pid, bool connected, IReadOnlyDictionary<ulong, Advert> adverts, long now)
    {
        long maxAge = connected ? 5 * 60 * 1000 : 90 * 1000; int minRssi = connected ? -90 : -80;
        Advert? best = null;
        foreach (var a in adverts.Values)
        {
            if (pid != 0 && a.Model != pid) continue;
            long age = now - a.Tick;
            if (age > maxAge || a.Rssi < minRssi) continue;
            bool fresh = age < 90_000, bestFresh = best != null && now - best.Tick < 90_000;
            if (best == null || (fresh && !bestFresh) || (fresh && bestFresh && a.Rssi > best.Rssi) || (!fresh && !bestFresh && a.Tick > best.Tick)) best = a;
        }
        if (best == null) return null;
        best = best.Clone();
        if (!best.Single)
            foreach (var a in adverts.Values)
            {
                if (a.Model != best.Model || a.Single || Math.Abs(best.Tick - a.Tick) > 15_000) continue;
                if (best.Left < 0 && a.Left >= 0) { best.Left = a.Left; best.LeftChg = a.LeftChg; }
                if (best.Right < 0 && a.Right >= 0) { best.Right = a.Right; best.RightChg = a.RightChg; }
                if (best.Box < 0 && a.Box >= 0) { best.Box = a.Box; best.BoxChg = a.BoxChg; }
            }
        return best;
    }

    static string Pct(int v) => v < 0 ? "?" : v + "%";

    public static (int Percent, bool Charging, string Detail) Summarise(Advert a)
    {
        if (a.Single) return (a.Left, a.LeftChg, "");
        var fp = new FastPairDriver.Battery { Left = a.Left, Right = a.Right, Box = a.Box, LeftChg = a.LeftChg, RightChg = a.RightChg, BoxChg = a.BoxChg };
        var s = FastPairDriver.Summarise(fp);
        return (s.Percent, s.Charging, $"L {Pct(a.Left)}{(a.LeftChg ? "⚡" : "")}  R {Pct(a.Right)}{(a.RightChg ? "⚡" : "")}  case {Pct(a.Box)}{(a.BoxChg ? "⚡" : "")}");
    }

    static readonly Regex AddrRx = new(@"&([0-9A-F]{12})_C", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>PID Apple (0x20xx) par adresse Bluetooth, lu dans les nœuds de service « _VID&amp;0001004C_PID&amp;xxxx » du registre.</summary>
    static Dictionary<ulong, ushort> ApplePids()
    {
        var map = new Dictionary<ulong, ushort>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
            if (root == null) return map;
            foreach (var name in root.GetSubKeyNames())
            {
                int v = name.IndexOf("_VID&0001004C_PID&", StringComparison.OrdinalIgnoreCase);
                if (v < 0) continue;
                if (!ushort.TryParse(name.AsSpan(v + "_VID&0001004C_PID&".Length, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort pid)) continue;
                using var k = root.OpenSubKey(name);
                if (k == null) continue;
                foreach (var inst in k.GetSubKeyNames())
                    if (AddrRx.Match(inst) is { Success: true } m && ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong addr)) map[addr] = pid;
            }
        }
        catch { /* registre illisible : aucun AirPods reconnu */ }
        return map;
    }

    static bool _watching;

    public static void Poll(PollContext ctx)
    {
        Dictionary<ulong, ushort> pids = ApplePids();
        if (pids.Count == 0) return; // aucun appareil audio Apple appairé
        List<(ulong Addr, string Name, bool Connected)> paired;
        try { paired = FastPairDriver.PairedDevices(); } catch { return; }
        var devices = paired.Where(p => pids.TryGetValue(p.Addr, out var pid) && (pid & 0xFF00) == 0x2000).OrderByDescending(p => p.Connected).ToList();
        if (devices.Count == 0) return;
        bool justStarted = BleAdverts.Ensure() && !_watching; _watching = true;
        IReadOnlyDictionary<ulong, Advert> Snapshot() { lock (Lock) return new Dictionary<ulong, Advert>(Adverts); }
        for (int i = 0; justStarted && i < 15 && !devices.Any(d => Pick(pids[d.Addr], d.Connected, Snapshot(), Environment.TickCount64) != null); i++) Thread.Sleep(200); // on laisse aux écouteurs le temps de diffuser
        var usedPid = new HashSet<ushort>();
        foreach (var (addr, name, connected) in devices)
        {
            ushort pid = pids[addr];
            if (!connected && usedPid.Contains(pid)) continue;
            if (Pick(pid, connected, Snapshot(), Environment.TickCount64) is not { } a) continue;
            usedPid.Add(pid);
            var (pct, chg, detail) = Summarise(a);
            ctx.Out.Add(new DeviceStatus { Key = $"airpods:{HidUtil.Hex4(pid)}:{name}", Name = name.Length > 0 ? name : "AirPods", Kind = DeviceKind.Headset, Via = "Bluetooth", Percent = pct, Charging = chg, Detail = detail.Length > 0 ? "Bluetooth · " + detail : null });
        }
    }
}
