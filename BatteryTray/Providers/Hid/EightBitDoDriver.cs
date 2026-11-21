// Manettes sans fil 8BitDo (VID 2DC8).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/eightbitdo.cpp, porté en C# — voir NOTICE.md.
// 1. Rapports d'entrée de manette D-input / Bluetooth (page 0001, usage 0005, ≥ 15 / 34 octets, rapport 01 ou 04, octet 14 : bits 0-6 = %, bit 7 = en charge) :
//    Ultimate 2 Wireless (6012, firmware 1.03+ : rapport de 34 octets), Ultimate 3-mode (202F / 202E), Ultimate 2C (301B / 301A / 310A), Pro 3 (6009),
//    Pro 2 (6003 / 6006), SN30 Pro (6001 / 6101), SF30 Pro (6000 / 6100). Disposition : SDL, SDL_hidapi_8bitdo.c (HandleStatePacket).
// 2. Canal vendeur de configuration / télémétrie (page FF7A ou FFA0, usage 0001, rapports de 64 octets, sortie 81, entrée 02) :
//    en mode X-input (2DC8:310B) le récepteur 2,4 GHz émule une manette Xbox 360 filaire, mais sa collection FF7A:0001 répond à des requêtes d'état en lecture seule :
//      [81 04 20 01…] → [02 04 04 00 20 01 01 00…], octet 18 = 1 si lié ; [81 04 21 01 00 00 06…] → [02 04 04 00 21 01 06 00…], octet 18 = batterie % (1..100) ;
//      [81 04 00 01…] (CMD_INFO) → octets 22..23 = PID interne (6013 = récepteur 2,4G, 6012 = manette sur câble USB).
//    Posée sur sa base de charge, l'Ultimate 2 Wireless coupe sa radio 2,4 GHz et le récepteur passe de 310B à 6013 (inactif) : la dernière valeur reste visible, « endormie ».
//    Référence : MiguelSanzBr/8bitdo-ultimate2-linux (docs/PROTOCOL.md).
namespace BatteryTray.Providers.Hid;

public static class EightBitDoDriver
{
    const ushort Vid = 0x2DC8, PidDongle = 0x6013, PidDongle24GIdle = 0x3109;
    const int ReportLen = 34; // l'Ultimate 2 en firmware 1.02 envoie des rapports de 12 octets sans batterie

    public sealed record Model(ushort Pid, string Name, bool NeedsFeature06);
    public static readonly Model[] Models =
    {
        new(0x310B, "8BitDo Ultimate 2 Wireless", false), new(0x6012, "8BitDo Ultimate 2", false), new(0x6013, "8BitDo Ultimate 2 Wireless", false),
        new(0x202F, "8BitDo Ultimate 3-mode", false), new(0x202E, "8BitDo Ultimate 3-mode", false),
        new(0x301B, "8BitDo Ultimate 2C Wireless", false), new(0x301A, "8BitDo Ultimate 2C Wireless", false), new(0x310A, "8BitDo Ultimate 2C Wireless", false),
        new(0x3012, "8BitDo Ultimate 2.4G", false), new(0x3013, "8BitDo Ultimate 2.4G", false), new(0x3109, "8BitDo Ultimate 2.4G", false),
        new(0x6007, "8BitDo Ultimate Bluetooth", false), new(0x6009, "8BitDo Pro 3", true),
        new(0x6003, "8BitDo Pro 2", true), new(0x6006, "8BitDo Pro 2", true),
        new(0x6001, "8BitDo SN30 Pro", true), new(0x6101, "8BitDo SN30 Pro", true),
        new(0x6000, "8BitDo SF30 Pro", true), new(0x6100, "8BitDo SF30 Pro", true),
    };

    public static Model? FindModel(ushort pid) => Models.FirstOrDefault(m => m.Pid == pid);
    public static bool IsUltimate2(ushort pid) => pid is 0x310B or 0x6012 or 0x6013;

    public static string ModelName(ushort pid, string product)
    {
        if (FindModel(pid) is { } m) return m.Name;
        if (product.Length > 0) return product.StartsWith("8BitDo", StringComparison.Ordinal) || product.StartsWith("8Bitdo", StringComparison.Ordinal) ? product : "8BitDo " + product;
        return "8BitDo Wireless Controller";
    }

    /// <summary>Rapport d'entrée D-input : (pourcentage, en charge) ; null si trop court, d'un autre type ou invalide.</summary>
    public static (int Percent, bool Charging)? ParseDInput(byte[] r, int minLen)
    {
        if (r.Length < minLen || r.Length < 15 || (r[0] != 0x01 && r[0] != 0x04) || (r[14] & 0x7F) > 100) return null;
        int pct = r[14] & 0x7F;
        return (pct, (r[14] & 0x80) != 0 && pct < 100);
    }

    public static byte[] LinkQuery() => new byte[] { 0x81, 0x04, 0x20, 0x01 };
    public static byte[] BatteryQuery() => new byte[] { 0x81, 0x04, 0x21, 0x01, 0x00, 0x00, 0x06 };
    public static byte[] InfoQuery() => new byte[] { 0x81, 0x04, 0x00, 0x01 };

    /// <summary>Réponse vendeur à la requête (cmdLo, cmdHi) : [02 04 .. .. cmdLo cmdHi …] d'au moins 19 octets.</summary>
    public static bool IsVendorReply(byte[] r, byte cmdLo, byte cmdHi) => r.Length >= 19 && r[0] == 0x02 && r[1] == 0x04 && r[4] == cmdLo && r[5] == cmdHi;

    /// <summary>Octet de batterie de la réponse 21 01 : (pourcentage, en charge) ; null si hors 1..100.</summary>
    public static (int Percent, bool Charging)? ParseVendorBattery(byte[] r)
    {
        int pct = r[18] & 0x7F;
        return pct is > 0 and <= 100 ? (pct, (r[18] & 0x80) != 0 && pct < 100) : null;
    }

    public static int? InternalPid(byte[] info) => info.Length >= 24 ? info[22] | (info[23] << 8) : null;

    static int _lastUltimate2Pct = -1;

    static byte[]? QueryVendor(HidDev d, byte[] req, byte cmdLo, byte cmdHi)
    {
        d.Flush();
        if (!d.Write(req)) return null;
        long end = Environment.TickCount64 + 300;
        while (end - Environment.TickCount64 > 0)
        {
            var r = d.Read((int)(end - Environment.TickCount64));
            if (r == null || r.Length < 19) break;
            if (IsVendorReply(r, cmdLo, cmdHi)) return r;
        }
        return null;
    }

    static DeviceStatus? PollVendorCollection(HidColl c)
    {
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        bool ult2 = IsUltimate2(c.Pid);
        var s = new DeviceStatus
        {
            Key = ult2 ? "8bitdo:ultimate2" : "8bitdo:" + HidUtil.Hex4(c.Pid), Name = ModelName(c.Pid, c.Product), Kind = DeviceKind.Gamepad, Via = "2.4 GHz dongle or USB",
        };
        bool linkedKnown = false, linked = true;
        if (QueryVendor(d, LinkQuery(), 0x20, 0x01) is { } lr) { linkedKnown = true; linked = lr[18] != 0; }

        if ((!linkedKnown || linked) && QueryVendor(d, BatteryQuery(), 0x21, 0x01) is { } br && ParseVendorBattery(br) is { } b)
        {
            s.Percent = b.Percent; s.Charging = b.Charging;
            if (ult2) _lastUltimate2Pct = b.Percent;
            return s;
        }
        if (QueryVendor(d, InfoQuery(), 0x00, 0x01) is { } info && InternalPid(info) is { } ip)
        {
            if (ip != PidDongle && ip != PidDongle24GIdle)
            {
                s.Via = "USB cable"; s.Charging = true;
                if (ult2 && _lastUltimate2Pct >= 0) s.Percent = _lastUltimate2Pct;
                return s;
            }
            if (ult2 && _lastUltimate2Pct >= 0) { s.Connected = false; return s; } // sur la base : endormie, la dernière valeur est conservée par l'interface
            s.Connected = false;
            return s;
        }
        if (c.Pid == PidDongle || c.Pid == PidDongle24GIdle) { s.Connected = false; return s; }
        return null;
    }

    static DeviceStatus? PollDInputGamepad(HidColl c)
    {
        var m = FindModel(c.Pid);
        using var d = new HidDev();
        if (!d.Open(c)) return null;
        if (m is { NeedsFeature06: true } && c.FeatLen > 0) d.GetFeature(0x06, out _); // fait passer la manette en rapports complets
        int minLen = c.Pid == 0x6012 ? ReportLen : 15;
        (int Percent, bool Charging)? found = null;
        long end = Environment.TickCount64 + 600;
        while (found == null && end - Environment.TickCount64 > 0)
        {
            var r = d.Read(100);
            if (r != null) found = ParseDInput(r, minLen);
        }
        if (found == null) return null;
        bool ult2 = IsUltimate2(c.Pid);
        if (ult2 && found.Value.Percent > 0) _lastUltimate2Pct = found.Value.Percent;
        return new DeviceStatus
        {
            Key = ult2 ? "8bitdo:ultimate2" : "8bitdo:" + HidUtil.Hex4(c.Pid), Name = ModelName(c.Pid, c.Product), Kind = DeviceKind.Gamepad,
            Via = c.Path.Contains("00001124", StringComparison.Ordinal) ? "Bluetooth" : "2.4 GHz dongle or USB",
            Percent = found.Value.Percent, Charging = found.Value.Charging,
        };
    }

    public static void Poll(PollContext ctx)
    {
        foreach (var (vid, pid) in ctx.Products(Vid))
        {
            foreach (var c in ctx.Find(vid, pid, c => (c.Page == 0xFF7A || c.Page == 0xFFA0) && c.Usage == 0x0001 && c.InLen >= 64 && c.OutLen >= 64))
                if (PollVendorCollection(c) is { } s) { ctx.Add(s, vid, pid); break; }
            if (ctx.IsClaimed(vid, pid) || FindModel(pid) == null) continue;
            foreach (var c in ctx.Find(vid, pid, c => c.Page == 0x01 && (c.Usage == 0x05 || c.Usage == 0x04) && c.InLen >= 15))
                if (PollDInputGamepad(c) is { } s) { ctx.Add(s, vid, pid); break; }
        }
    }
}
