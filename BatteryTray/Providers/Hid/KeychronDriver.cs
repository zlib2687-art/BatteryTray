// Appareils sans fil Keychron (VID 3434) : souris (M1…M7, M4 / M3 / M6 4K, M5 8K, G6 HE 8K…), trackball Nape Pro, claviers sur récepteur Keychron Link.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/keychron.cpp, porté en C# — voir NOTICE.md.
// Sources : le configurateur web de Keychron (launcher.keychron.com, main.js), nvalis/nape_pro, gtun/KeychronNapeBatteryWidget, ky0209/NapeBar,
// ldkv/qmk_firmware#1 (patch KC_GET_BATTERY). Tout est en lecture seule.
//  * Souris « classiques » (M4…) : rapport de sortie de 64 octets sans identifiant : [01][00][81][01][0…][somme], somme (octet 63) = 0xA1 − somme(octets 0..62).
//    La réponse [01][..][..][01][état][cadence][vid lo][vid hi][pid lo][pid hi][charge][batterie %] porte l'identifiant fabricant de la souris, vérifié (3434).
//  * Souris 8K sur récepteur TurboLink : même requête avec le bit 6 du premier octet (41 00 81 01…) ; réponse [41]…[01]…[état][batterie %][profil], pas de vid ; état 1 = en charge.
//  * Souris 4K sur récepteur Keychron 4K Link (FF0A:0001) : protocole « 4k » ; 40 00 81 00 renvoie le PID de la souris appairée (octets 4/5, pour son nom).
//  * Claviers sur récepteur Keychron Link (3434:D030) ou en direct : interface brute FF60 / usage 61, rapports de 32 octets : B2 = état du récepteur [B2][emplacements] puis par
//    emplacement [vid lo][vid hi][pid lo][pid hi][connecté] ; micrologiciel patché : A4 → [A4][%][mV lo][mV hi][0 batterie / 1 en charge / 2 pleine][transport][modèle].
//  * Trackball Nape Pro (ZMK) sur récepteur Link-KM (D026) ou câble (0440) : B2 (3 emplacements aux octets 2/7/12 : vid BE16, pid BE16, état) puis A7 31 → [A7][31][%][charge].
//  * Anciennes souris (M1, M2, M3, M3 mini, M6, M7…) sur le récepteur 1K D030 / D031 : collection page 0x8C avec rapport de fonctionnalité 0x51 : SetFeature [51][06], GetFeature 51
//    → [51][06][01][00][vid][pid]…[batterie %][charge].
//  * Ultra-Link 8K (D028) : collection FFC1:0001, rapport B5 [03] = emplacements, B3 [06] = infos de la souris dont l'octet 19 (bits 0-6 = %, bit 7 = en charge).
namespace BatteryTray.Providers.Hid;

public static class KeychronDriver
{
    const ushort Vid = 0x3434, LinkKm = 0xD026, NapeUsb = 0x0440, NapeInSlot = 0x4004, LegacyA = 0xD030, LegacyC = 0xD031, UltraLink8k = 0xD028;
    const byte LegacyReport = 0x51, LegacyStatus = 0x06;

    // ------------------------------------------------------------------ logique pure (testée)

    /// <summary>Requête 64 octets [cmd][00][81][sub]… avec somme de contrôle (octet 63 = 0xA1 − somme des octets 0..62).</summary>
    public static byte[] BuildMouseQuery(byte cmd, byte sub = 0x01)
    {
        var p = new byte[64]; p[0] = cmd; p[2] = 0x81; p[3] = sub;
        int sum = 0; for (int i = 0; i < 63; i++) sum += p[i];
        p[63] = (byte)(0xA1 - sum);
        return p;
    }

    public static string CleanName(string n) => n.Length == 0 || n.Contains("Link", StringComparison.Ordinal) || n.Contains("Receiver", StringComparison.Ordinal) ? "Keychron mouse" : n;

    public static string? Name4k(ushort productId) => productId switch
    {
        0x0620 or 0x0622 => "Keychron M3 mini 4K", 0x0621 => "Keychron M4 4K", 0x0624 => "Keychron M6 4K", 0x07A0 => "Keychron M3 4K", _ => null,
    };

    public static string LegacyName(ushort pid) => pid switch
    {
        0xD032 or 0xD033 => "Keychron M3", 0xD035 => "Keychron M1", 0xD036 or 0xD03E => "Keychron M3 mini", 0xD03B => "Keychron M2",
        0xD03D => "Keychron M2 mini", 0xD03F => "Keychron M6", 0xD044 => "Keychron M7", _ => "Keychron mouse",
    };

    /// <summary>Réponse « classique » (requête 01) : (%, en charge) si le vid est 3434 et le niveau plausible ; le vid lu est aussi renvoyé.</summary>
    public static (int Percent, bool Charging)? ParseClassic(byte[] r1) =>
        r1.Length >= 13 && (r1[6] | (r1[7] << 8)) == 0x3434 && r1[11] <= 100 ? (r1[11], r1[10] != 0) : null;

    /// <summary>Réponse 8K à la requête 41 : état ≤ 3, batterie ≤ 100 ; état 1 = en charge.</summary>
    public static (int Percent, bool Charging)? Parse8k(byte[] r) => r.Length >= 13 && r[10] <= 3 && r[11] <= 100 ? (r[11], r[10] == 1) : null;

    /// <summary>Souris 8K sur câble (réponse à 01 sans bit 6) : niveau 1..100, état ≤ 3, octet 5 = cadence 1..8.</summary>
    public static (int Percent, bool Charging)? Parse8kCable(byte[] r1) =>
        r1.Length >= 13 && r1[10] <= 3 && r1[11] is >= 1 and <= 100 && r1[5] is >= 1 and <= 8 ? (r1[11], r1[10] == 1) : null;

    /// <summary>Réponse 4K à la requête 41 : (%, en charge) ; batterie en [6], état en [5].</summary>
    public static (int Percent, bool Charging)? Parse4k(byte[] r) => r.Length >= 7 && r[6] is >= 1 and <= 100 ? (r[6], r[5] != 0) : null;

    /// <summary>Anciennes souris : réponse 51 06 → (pid souris, %, en charge) ; vid 3434 et niveau ≤ 100 exigés.</summary>
    public static (ushort Pid, int Percent, bool Charging)? ParseLegacy(byte[] r)
    {
        if (r.Length < 13 || r[0] != LegacyReport || r[1] != LegacyStatus || r[2] != 0x01 || (r[4] | (r[5] << 8)) != 0x3434 || r[11] > 100) return null;
        return ((ushort)(r[6] | (r[7] << 8)), r[11], r[12] == 0x01);
    }

    /// <summary>Réponse B2 du récepteur Link : PID du premier clavier connecté (vid 3434), ou 0.</summary>
    public static ushort FirstConnectedKeyboardPid(byte[] r)
    {
        int slots = r.Length > 1 ? Math.Min((int)r[1], 5) : 0;
        for (int i = 0; i < slots && 2 + 5 * i + 4 < r.Length; i++)
        {
            int o = 2 + 5 * i;
            if ((r[o] | (r[o + 1] << 8)) == 0x3434 && r[o + 4] != 0) return (ushort)(r[o + 2] | (r[o + 3] << 8));
        }
        return 0;
    }

    /// <summary>Réponse A4 d'un clavier patché : (%, mV, en charge) ; null si le niveau dépasse 100.</summary>
    public static (int Percent, int Millivolts, bool Charging)? ParseKeyboardBattery(byte[] r) =>
        r.Length < 5 || r[1] > 100 ? null : (r[1], r[2] | (r[3] << 8), r[4] == 1);

    public static string KeyboardName(ushort kbPid) => kbPid is >= 0x0A50 and <= 0x0A58 ? "Keychron K5 Max" : "Keychron keyboard";

    /// <summary>Réponse B2 du Link-KM : vrai si un Nape Pro est connecté dans un des emplacements (octets 2/7/12 : vid BE16, pid BE16, état).</summary>
    public static bool NapeConnected(byte[] r)
    {
        if (r.Length < 17) return false;
        bool connected = false;
        foreach (int off in new[] { 2, 7, 12 })
            if (((r[off + 2] << 8) | r[off + 3]) == NapeInSlot) connected = r[off + 4] == 1;
        return connected;
    }

    /// <summary>Réponse A7 31 du Nape Pro : (%, en charge) ; null si niveau &gt; 100.</summary>
    public static (int Percent, bool Charging)? ParseNape(byte[] r) => r.Length < 4 || r[2] > 100 ? null : (r[2], r[3] != 0);

    /// <summary>Réponse B5 03 de l'Ultra-Link : vrai si un emplacement porte un vid/pid non nul et un état non nul (décalages 3 / 8 / 13, identifiant de rapport compris).</summary>
    public static bool UltraConnected(byte[] r)
    {
        bool c = false;
        foreach (int off in new[] { 3, 8, 13 }) c |= off + 4 < r.Length && (r[off] | r[off + 1]) != 0 && r[off + 4] != 0;
        return c;
    }

    /// <summary>Réponse B3 06 de l'Ultra-Link : octet de puissance [20] → (%, en charge) ; null si hors 1..100.</summary>
    public static (int Percent, bool Charging)? ParseUltraPower(byte[] r)
    {
        if (r.Length < 21) return null;
        int pct = r[20] & 0x7F;
        return pct is >= 1 and <= 100 ? (pct, (r[20] & 0x80) != 0) : null;
    }

    // ------------------------------------------------------------------ échanges

    static byte[]? MouseQuery(HidDev d, byte cmd, byte sub = 0x01)
    {
        var req = new byte[65]; BuildMouseQuery(cmd, sub).CopyTo(req, 1); // identifiant de rapport 0
        d.Flush();
        if (!d.Write(req)) return null;
        long end = Environment.TickCount64 + 600;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 13) return null;
            if (r[0] == cmd && r[3] == sub) return r;
        }
        return null;
    }

    static byte[]? RawQuery(HidDev d, byte[] cmd, int timeoutMs = 600)
    {
        var req = new byte[33]; cmd.CopyTo(req, 1);
        d.Flush();
        if (!d.Write(req)) return null;
        long end = Environment.TickCount64 + timeoutMs;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) return null;
            if (r.Length < cmd.Length + 2) continue;
            bool match = true;
            for (int i = 0; i < cmd.Length; i++) match &= r[i] == cmd[i];
            if (match) return r;
        }
        return null;
    }

    static bool IsRaw(HidColl c) => c.Page == 0xFF60 && c.Usage == 0x61 && c.OutLen == 33;

    static void PollLinkKeyboard(PollContext ctx, ushort vid, ushort pid)
    {
        if (pid == LinkKm || pid == NapeUsb) return; // récepteur / câble du Nape Pro : traités par PollNape
        foreach (var c in ctx.Find(vid, pid, IsRaw))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            ushort kbPid; byte[]? r;
            var b2 = RawQuery(d, new byte[] { 0xB2 }, 300);
            bool viaLink = b2 != null && b2.Length >= 8;
            if (viaLink)
            {
                kbPid = FirstConnectedKeyboardPid(b2!);
                if (kbPid == 0) return; // clavier éteint, en veille ou sur câble : l'appli garde sa dernière valeur
                // Après une période d'inactivité, la première requête se perd pendant le réveil ; la seconde est servie en ~60 ms.
                r = RawQuery(d, new byte[] { 0xA4 }, 400) ?? RawQuery(d, new byte[] { 0xA4 }, 400);
                if (r == null) return; // micrologiciel d'origine
            }
            else
            {
                kbPid = pid; // pas un récepteur (un clavier répond à B2 par le « non géré » FF de VIA) : on l'interroge directement
                r = RawQuery(d, new byte[] { 0xA4 }, 400);
                if (r == null) continue;
            }
            if (ParseKeyboardBattery(r) is not { } b) return;
            ctx.Add(new DeviceStatus
            {
                Key = "keychron-kb:" + HidUtil.Hex4(kbPid), Name = KeyboardName(kbPid), Kind = DeviceKind.Keyboard, // même clé sur récepteur et câble
                Via = viaLink ? "2.4 GHz receiver" : "USB cable", Percent = b.Percent, Charging = b.Charging,
            }, vid, pid);
            return;
        }
    }

    static void PollNape(PollContext ctx)
    {
        foreach (ushort pid in new[] { LinkKm, NapeUsb })
            foreach (var c in ctx.Find(Vid, pid, IsRaw))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                if (pid == LinkKm && (RawQuery(d, new byte[] { 0xB2 }) is not { } b2 || !NapeConnected(b2))) continue; // on ne demande que si un Nape Pro est connecté
                if (RawQuery(d, new byte[] { 0xA7, 0x31 }) is not { } r || ParseNape(r) is not { } n) continue;
                ctx.Add(new DeviceStatus { Key = "keychron:nape", Name = "Keychron Nape Pro", Kind = DeviceKind.Mouse, Via = pid == LinkKm ? "2.4 GHz receiver" : "USB cable", Percent = n.Percent, Charging = n.Charging }, Vid, pid);
                return;
            }
    }

    static void PollLegacy(PollContext ctx)
    {
        foreach (ushort rpid in new[] { LegacyA, LegacyC })
            foreach (var c in ctx.Find(Vid, rpid, c => c.Page == 0x8C && c.FeatLen >= 16))
            {
                using var d = new HidDev();
                if (!d.Open(c) && !d.OpenFeatureOnly(c)) continue;
                if (!d.SetFeature(new byte[] { LegacyReport, LegacyStatus })) continue;
                byte[]? r = null;
                for (int i = 0; i < 5 && r == null; i++) // la réponse est en général immédiate ; la souris dispose d'environ 200 ms
                {
                    Thread.Sleep(40);
                    if (d.GetFeature(LegacyReport, out var f) && ParseLegacy(f) != null) r = f;
                }
                if (r == null || ParseLegacy(r) is not { } l) continue;
                ctx.Add(new DeviceStatus { Key = "keychron:" + HidUtil.Hex4(l.Pid), Name = LegacyName(l.Pid), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = l.Percent, Charging = l.Charging }, Vid, rpid);
                break;
            }
    }

    static byte[]? UltraQuery(HidDev d, byte id, byte cmd)
    {
        d.Flush();
        if (!d.Write(id, cmd)) return null;
        long end = Environment.TickCount64 + 600;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null) return null;
            if (r.Length >= 21 && r[1] == cmd) return r;
        }
        return null;
    }

    static void PollUltraLink(PollContext ctx)
    {
        foreach (var c in ctx.Find(Vid, UltraLink8k, c => c.Page == 0xFFC1 && c.Usage == 0x01 && c.OutLen >= 21 && c.InLen >= 21))
        {
            using var d = new HidDev();
            if (!d.Open(c)) continue;
            if (UltraQuery(d, 0xB5, 0x03) is not { } slots || !UltraConnected(slots)) continue;
            if (UltraQuery(d, 0xB3, 0x06) is not { } info || ParseUltraPower(info) is not { } p) continue;
            ctx.Add(new DeviceStatus { Key = "keychron:" + HidUtil.Hex4(UltraLink8k), Name = CleanName(c.Product), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = p.Percent, Charging = p.Charging }, Vid, UltraLink8k);
            return;
        }
    }

    public static void Poll(PollContext ctx)
    {
        PollNape(ctx);
        PollLegacy(ctx);
        PollUltraLink(ctx);
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            foreach (var c in ctx.Find(vid, pid, c => c.Page >= 0xFF00 && c.OutLen == 65 && c.InLen == 65))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                int percent = -1; bool charging = false, wireless = true;
                var r1 = MouseQuery(d, 0x01);
                int tag = r1 != null && r1.Length >= 8 ? r1[6] | (r1[7] << 8) : 0;
                if (r1 != null && c.Page == 0xFF0A && c.Usage == 0x01 && tag != 0x3434 && tag != 0x362D)
                {
                    // Souris 4K sur récepteur 4K Link (protocole « 4k ») ; la disposition 8K ne s'applique pas.
                    if (MouseQuery(d, 0x41) is not { } r || Parse4k(r) is not { } k) continue;
                    var s4 = new DeviceStatus { Key = "keychron:" + HidUtil.Hex4(pid), Name = CleanName(c.Product), Kind = DeviceKind.Mouse, Via = "2.4 GHz receiver", Percent = k.Percent, Charging = k.Charging };
                    if (MouseQuery(d, 0x40, 0x00) is { Length: >= 6 } st && Name4k((ushort)(st[4] | (st[5] << 8))) is { } nm) s4.Name = nm;
                    ctx.Add(s4, vid, pid);
                    break;
                }
                if (r1 != null && tag == 0x3434 && ParseClassic(r1) is { } cl) { percent = cl.Percent; charging = cl.Charging; }
                else if (MouseQuery(d, 0x41) is { } r8 && Parse8k(r8) is { } e8) { percent = e8.Percent; charging = e8.Charging; }
                else if (r1 != null && Parse8kCable(r1) is { } ec) { percent = ec.Percent; charging = ec.Charging; wireless = false; } // souris 8K sur câble (sans bit 6)
                if (percent < 0) continue;
                ctx.Add(new DeviceStatus { Key = "keychron:" + HidUtil.Hex4(pid), Name = CleanName(c.Product), Kind = DeviceKind.Mouse, Via = wireless ? "2.4 GHz receiver" : "USB cable", Percent = percent, Charging = charging }, vid, pid);
                break;
            }
            PollLinkKeyboard(ctx, vid, pid);
        }
    }
}
