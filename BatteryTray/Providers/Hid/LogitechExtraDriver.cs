// Logitech HID++ (VID 046D) : les cas que LogitechHidppProvider ne couvre pas — casques G à mesure de tension (feature 0x1F20), anciens appareils
// HID++ 1.0 (registres 0x0D et 0x07) et récepteur LIGHTSPEED PRO X3 SUPERSTRIKE (046D:C54F, HID++ sur FF43). Les appareils qui ont déjà
// une batterie 0x1004 / 0x1000 / 0x1001 sont laissés à LogitechHidppProvider pour ne pas être signalés deux fois.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/logitech.cpp, porté en C# — voir NOTICE.md.
// Message : [rapport][index appareil][index feature][fonction << 4 | id logiciel][params…] ; rapport 0x10 = court (7 octets), 0x11 = long (20).
//   Erreur HID++ 2.0 : [.., 0xFF, feature, fn|sw, code] ; erreur HID++ 1.0 : [.., 0x8F, sous-id, adresse, code].
// Références : spécifications HID++ 2.0 (brouillons Logitech), Solaar, HeadsetControl (calibration en tension des casques) ;
// PRO X Wireless : modèle d'énergie de G HUB pour 046D:0ABA, vérifié contre G HUB.
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryTray.Providers.Hid;

public static class LogitechExtraDriver
{
    const ushort Vid = 0x046D, PassthroughDongle = 0xC54F;
    const byte SwId = 0x0D;

    // ------------------------------------------------------------------ tables et analyse (logique pure, testée)

    static readonly int[] G533v = { 4200, 3850, 3790, 3750, 3680, 3330 }, G533p = { 100, 50, 30, 20, 5, 0 };
    static readonly int[] G535v = { 4175, 3817, 3766, 3730, 3664, 3310 };
    static readonly int[] G633v = { 4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150 }, G633p = { 100, 80, 60, 40, 20, 10, 5, 0 };
    static readonly int[] GProv = { 4150, 3830, 3780, 3740, 3670, 3320 };
    static readonly int[] ProXv = { 4102, 4046, 3993, 3950, 3915, 3877, 3838, 3809, 3789, 3772, 3758, 3747, 3736, 3722, 3707, 3687, 3661, 3644, 3548, 3426, 3293 };
    static readonly int[] ProXp = { 100, 95, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 10, 5, 0 };
    static readonly int[] Defv = { 4100, 3850, 3780, 3730, 3650, 3300 };

    /// <summary>Tension (mV) → pourcentage pour un casque G (calibration de HeadsetControl).</summary>
    public static int HeadsetPercent(ushort pid, int mv) => pid switch
    {
        0x0A66 => HidUtil.Interpolate(mv, G533v, G533p),
        0x0AC4 => HidUtil.Interpolate(mv, G535v, G533p),
        0x0A5C or 0x0A89 or 0x0A5B or 0x0A87 or 0x0AB5 or 0x0AFE => HidUtil.Interpolate(mv, G633v, G633p),
        0x0AAA or 0x0ABA => HidUtil.Interpolate(mv, ProXv, ProXp),
        0x0AA7 or 0x0AFB => HidUtil.Interpolate(mv, GProv, G533p),
        _ => HidUtil.Interpolate(mv, Defv, G533p),
    };

    public static DeviceKind KindFromType(byte t) => t switch
    {
        0x00 or 0x02 => DeviceKind.Keyboard,
        0x03 or 0x04 or 0x05 => DeviceKind.Mouse,
        0x08 => DeviceKind.Headset,
        0x0B or 0x0C => DeviceKind.Gamepad,
        _ => DeviceKind.Other,
    };

    public enum Reply { None, Ok, Error }

    /// <summary>Message HID++ → réponse à la requête (idx, b2, b3) : charge (octets 4…) ou code d'erreur.</summary>
    public static (Reply Result, byte[] Payload, byte Error) ParseReply(byte[] r, byte idx, byte b2, byte b3)
    {
        if (r.Length < 7 || (r[0] != 0x10 && r[0] != 0x11) || r[1] != idx) return (Reply.None, Array.Empty<byte>(), 0);
        if (r[2] == b2 && r[3] == b3) return (Reply.Ok, r[4..], 0);
        if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == b2 && r[4] == b3) return (Reply.Error, Array.Empty<byte>(), r[5]);
        return (Reply.None, Array.Empty<byte>(), 0);
    }

    /// <summary>ADC_MEASUREMENT (0x1F20) : [mV hi][mV lo][état] → (millivolts, en charge).</summary>
    public static (int Millivolts, bool Charging)? ParseAdc(byte[] p) => p.Length < 3 ? null : ((p[0] << 8) | p[1], p[2] == 0x03);

    /// <summary>UNIFIED_BATTERY (0x1004) : [% ][niveau][état] → (pourcentage ou -1, en charge).</summary>
    public static (int Percent, bool Charging)? ParseUnified(byte[] p)
    {
        if (p.Length < 3) return null;
        int soc = p[0], level = p[1], st = p[2];
        int pct = soc is > 0 and <= 100 ? soc : level != 0 ? ((level & 8) != 0 ? 100 : (level & 4) != 0 ? 60 : (level & 2) != 0 ? 20 : 5) : -1;
        return (pct, st is 1 or 2 or 3);
    }

    /// <summary>BATTERY_STATUS (0x1000) : [%][suivant][état].</summary>
    public static (int Percent, bool Charging)? ParseLevelStatus(byte[] p)
    {
        if (p.Length < 3) return null;
        int st = p[2], pct = p[0] <= 100 ? p[0] : -1;
        if (pct == 0 && st == 3) pct = 100;
        return (pct, st is >= 1 and <= 4);
    }

    static readonly int[] LiV = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 };
    static readonly int[] LiP = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 };

    /// <summary>BATTERY_VOLTAGE (0x1001) : [mV hi][mV lo][drapeaux] → pourcentage estimé (courbe Li-ion typique), en charge si bit 7.</summary>
    public static (int Percent, bool Charging, int Millivolts)? ParseVoltage(byte[] p)
    {
        if (p.Length < 3) return null;
        int mv = (p[0] << 8) | p[1];
        return (mv != 0 ? HidUtil.Interpolate(mv, LiV, LiP) : -1, (p[2] & 0x80) != 0, mv);
    }

    /// <summary>HID++ 1.0 registre 0x0D : [%][..][état] ; null si hors plage.</summary>
    public static (int Percent, bool Charging)? ParseRegister0D(byte[] p) =>
        p.Length >= 3 && p[0] != 0 && p[0] <= 100 ? (p[0], p[2] is >= 0x21 and <= 0x26) : null;

    static readonly int[] Level10 = { 0, 5, 10, 20, 35, 50, 75, 100 };

    /// <summary>HID++ 1.0 registre 0x07 : [niveau 1..7][état] ; null si le niveau est 0.</summary>
    public static (int Percent, bool Charging)? ParseRegister07(byte[] p) =>
        p.Length >= 2 && p[0] != 0 ? (Level10[Math.Min(p[0], (byte)7)], p[1] is >= 0x21 and <= 0x26) : null;

    static readonly Regex ColRx = new(@"&col\d\d", RegexOptions.Compiled);

    /// <summary>Identifie l'appareil physique d'une collection (deux récepteurs de même PID restent distincts) ; "" pour un chemin d'une autre forme.</summary>
    public static string PhysicalKey(string path)
    {
        var p = path.ToLowerInvariant();
        int a = p.IndexOf('#'); int b = a < 0 ? -1 : p.IndexOf('#', a + 1); int e = b < 0 ? -1 : p.IndexOf('#', b + 1);
        if (e < 0) return "";
        string hw = ColRx.Replace(p.Substring(a + 1, b - a - 1), ""), inst = p.Substring(b + 1, e - b - 1);
        int amp = inst.LastIndexOf('&'); if (amp >= 0) inst = inst[..amp];
        return hw + "#" + inst;
    }

    // ------------------------------------------------------------------ échanges

    sealed class Hidpp : IDisposable
    {
        readonly HidDev _s = new(), _l = new();
        bool _hasShort, _hasLong;
        public bool Open(HidColl? s, HidColl? l)
        {
            if (s != null) _hasShort = _s.Open(s);
            if (l != null) _hasLong = _l.Open(l);
            return _hasShort || _hasLong;
        }

        public (Reply, byte[], byte) Request(byte idx, byte b2, byte b3, byte[]? prm = null, bool forceShort = false, int timeoutMs = 500)
        {
            bool useLong = _hasLong && !forceShort;
            if (!useLong && !_hasShort) return (Reply.None, Array.Empty<byte>(), 0);
            var msg = new byte[useLong ? 20 : 7];
            msg[0] = (byte)(useLong ? 0x11 : 0x10); msg[1] = idx; msg[2] = b2; msg[3] = b3;
            if (prm != null) for (int i = 0; i < prm.Length && 4 + i < msg.Length; i++) msg[4 + i] = prm[i];
            if (!(useLong ? _l : _s).Write(msg)) return (Reply.None, Array.Empty<byte>(), 0);
            long end = Environment.TickCount64 + timeoutMs;
            while (end - Environment.TickCount64 > 0)
                foreach (var d in new[] { _l, _s })
                {
                    if (!d.Ok) continue;
                    var r = d.Read(15);
                    if (r == null) continue;
                    var p = ParseReply(r, idx, b2, b3);
                    if (p.Result != Reply.None) return p;
                }
            return (Reply.None, Array.Empty<byte>(), 0);
        }

        public byte[]? Call(byte idx, byte feature, int fn, byte[]? prm = null)
        {
            var (res, p, _) = Request(idx, feature, (byte)((fn << 4) | SwId), prm);
            return res == Reply.Ok ? p : null;
        }

        public byte FeatureIndex(byte idx, ushort id) => Call(idx, 0x00, 0, new[] { (byte)(id >> 8), (byte)id }) is { Length: > 0 } p ? p[0] : (byte)0;
        public void Dispose() { _s.Dispose(); _l.Dispose(); }
    }

    static string? ReadName(Hidpp h, byte idx, out DeviceKind kind)
    {
        kind = DeviceKind.Other;
        byte f = h.FeatureIndex(idx, 0x0005);
        if (f == 0) return null;
        var sb = new StringBuilder();
        if (h.Call(idx, f, 0) is { Length: > 0 } c)
        {
            int len = c[0];
            while (sb.Length < len && sb.Length < 64)
            {
                if (h.Call(idx, f, 1, new[] { (byte)sb.Length }) is not { Length: > 0 } p) break;
                int before = sb.Length;
                foreach (var ch in p) { if (sb.Length >= len || ch == 0) break; sb.Append((char)ch); }
                if (sb.Length == before) break;
            }
        }
        if (h.Call(idx, f, 2) is { Length: > 0 } t) kind = KindFromType(t[0]);
        if (sb.Length == 0) return null;
        string n = sb.ToString();
        return n.StartsWith("Logitech", StringComparison.Ordinal) || n.StartsWith("G ", StringComparison.Ordinal) || n.StartsWith("MX", StringComparison.Ordinal) ? n : "Logitech " + n;
    }

    /// <summary>Lit la batterie d'un appareil HID++ 2.0. allFeatures = false : on ne lit que 0x1F20 (le reste appartient à LogitechHidppProvider).</summary>
    static bool ReadBattery20(Hidpp h, byte idx, ushort pid, bool allFeatures, DeviceStatus s)
    {
        if (allFeatures)
        {
            byte f;
            if ((f = h.FeatureIndex(idx, 0x1004)) != 0 && h.Call(idx, f, 1) is { } a && ParseUnified(a) is { } u) { s.Percent = u.Percent; s.Charging = u.Charging; return true; }
            if ((f = h.FeatureIndex(idx, 0x1000)) != 0 && h.Call(idx, f, 0) is { } b && ParseLevelStatus(b) is { } l) { s.Percent = l.Percent; s.Charging = l.Charging; return true; }
            if ((f = h.FeatureIndex(idx, 0x1001)) != 0 && h.Call(idx, f, 0) is { } c && ParseVoltage(c) is { } v) { s.Percent = v.Percent; s.Charging = v.Charging; return true; }
        }
        else if (h.FeatureIndex(idx, 0x1004) != 0 || h.FeatureIndex(idx, 0x1000) != 0 || h.FeatureIndex(idx, 0x1001) != 0) return false;
        byte g = h.FeatureIndex(idx, 0x1F20);
        if (g != 0 && h.Call(idx, g, 0) is { } d && ParseAdc(d) is { } adc)
        {
            s.Charging = adc.Charging;
            if (adc.Millivolts != 0) s.Percent = HeadsetPercent(pid, adc.Millivolts);
            return true;
        }
        return false;
    }

    static bool ReadBattery10(Hidpp h, byte idx, DeviceStatus s)
    {
        var (r1, p1, _) = h.Request(idx, 0x81, 0x0D, forceShort: true);
        if (r1 == Reply.Ok && ParseRegister0D(p1) is { } a) { s.Percent = a.Percent; s.Charging = a.Charging; return true; }
        var (r2, p2, _) = h.Request(idx, 0x81, 0x07, forceShort: true);
        if (r2 == Reply.Ok && ParseRegister07(p2) is { } b) { s.Percent = b.Percent; s.Charging = b.Charging; return true; }
        return false;
    }

    static bool PollDevice(PollContext ctx, ushort pid, List<HidColl> shorts, List<HidColl> longs, string suffix)
    {
        if (shorts.Count == 0 && longs.Count == 0) return false;
        using var h = new Hidpp();
        if (!h.Open(shorts.Count > 0 ? shorts[0] : null, longs.Count > 0 ? longs[0] : null)) return false;
        var first = longs.Count > 0 ? longs[0] : shorts[0];
        string product = first.Product;
        bool isReceiver = product.Contains("Receiver", StringComparison.OrdinalIgnoreCase);
        bool passthrough = pid == PassthroughDongle;
        string path = first.Path.ToLowerInvariant();
        bool bluetooth = path.Contains("{00001812-") || path.Contains("{00001124-"); // un appareil Bluetooth est toujours en liaison directe

        byte[] indices;
        if (passthrough) indices = new byte[] { 1 };
        else if (bluetooth) indices = new byte[] { 0xFF };
        else
        {
            var (res, p, _) = isReceiver ? (Reply.None, Array.Empty<byte>(), (byte)0) : h.Request(0xFF, 0x00, 0x10 | SwId, new byte[] { 0, 0, 0x5A }, false, 400);
            indices = res == Reply.Ok && p.Length >= 1 && p[0] >= 2 ? new byte[] { 0xFF } : new byte[] { 1, 2, 3, 4, 5, 6 };
        }

        bool any = false;
        foreach (var idx in indices)
        {
            var s = new DeviceStatus
            {
                Key = "logix:" + HidUtil.Hex4(pid) + suffix + ":" + idx,
                Name = idx == 0xFF && product.Length > 0 ? product : "Logitech wireless device",
                Via = idx == 0xFF ? "USB / Bluetooth" : "Logitech receiver",
                Kind = passthrough ? DeviceKind.Mouse : DeviceKind.Other,
            };
            var (r, p, err) = h.Request(idx, 0x00, 0x10 | SwId, new byte[] { 0, 0, 0x5A }, false, 400);
            bool found = false;
            if (r == Reply.Ok && p.Length >= 1 && p[0] >= 2)
            {
                var name = ReadName(h, idx, out var kind);
                found = ReadBattery20(h, idx, pid, passthrough, s);
                if (found) { if (name != null) s.Name = name; if (kind != DeviceKind.Other) s.Kind = kind; }
            }
            else if (r == Reply.Error && err == 0x01) found = ReadBattery10(h, idx, s); // « sous-id invalide » : appareil HID++ 1.0
            if (!found) continue;
            if (s.Kind == DeviceKind.Other) s.Kind = s.Name.Contains("headset", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Headset : s.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard : DeviceKind.Mouse;
            s.Vid = Vid;
            ctx.Out.Add(s);
            any = true;
        }
        return any;
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            bool pt = pid == PassthroughDongle;
            var allShorts = ctx.Find(vid, pid, c => c.OutLen == 7 && ((c.Page == 0xFF00 && c.Usage == 0x0001) || (pt && c.Page == 0xFF43 && c.Usage == 0x0301)));
            var allLongs = ctx.Find(vid, pid, c => c.OutLen == 20 && ((c.Page == 0xFF00 && c.Usage == 0x0002) || (c.Page == 0xFF43 && (c.Usage == 0x0202 || (pt && c.Usage == 0x0302)))));
            var groups = allShorts.Concat(allLongs).Select(c => PhysicalKey(c.Path)).Distinct().ToList();
            bool any = false;
            for (int g = 0; g < groups.Count; g++)
            {
                var shorts = allShorts.Where(c => PhysicalKey(c.Path) == groups[g]).ToList();
                var longs = allLongs.Where(c => PhysicalKey(c.Path) == groups[g]).ToList();
                if (PollDevice(ctx, pid, shorts, longs, g > 0 ? "#" + (g + 1) : "")) any = true;
            }
            if (any) ctx.Claim(vid, pid);
        }
    }
}
