// Casques JBL Quantum (VID 0ECB) : 350 / 360 / 610 (protocole « HIDV1 », rapports de fonctionnalité) et 520 / 650 / 850 / 950 et variantes X
// (protocole « HIDV3 », collection vendeur page FF12, rapports de 64 octets : entrée 86, sortie 87, fonctionnalité 88).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/jbl.cpp, porté en C# — voir NOTICE.md.
// HIDV1 : GET 68 (liaison, 01 = casque connecté) et GET 49 (batterie 0-100), un octet de charge chacun. Le micrologiciel répond à un
//   identifiant non implémenté par le précédent : le premier octet de la réponse doit être l'identifiant demandé.
// HIDV3 : requête (fonctionnalité 88) [88][00 DD][01 00][01][00][02 00][fonction lo][fonction hi] ; réponse (entrée 86)
//   [86][00 DD][01 00][01][00][long lo][long hi][état lo][état hi][fonction lo][fonction hi][taille lo][taille hi][valeur…] ;
//   état bit 15 = erreur. Batterie = fonction 000D, un octet : bit 7 en charge, bits 0..6 pourcentage.
// Casque éteint : le récepteur ne répond pas du tout, un délai dépassé signifie « absent ». Lecture seule (GET uniquement).
namespace BatteryTray.Providers.Hid;

public static class JblDriver
{
    const ushort Vid = 0x0ECB, BatteryFeature = 0x000D;

    public sealed record Model(ushort Pid, string Name, bool Dongle);
    public static readonly Model[] Hidv3Models =
    {
        new(0x2125, "JBL Quantum 650 Wireless", true), new(0x2126, "JBL Quantum 650 Wireless", false),
        new(0x213B, "JBL Quantum 650X Wireless", true), new(0x213D, "JBL Quantum 650X Wireless", false),
        new(0x2127, "JBL Quantum 950 Wireless", true), new(0x2128, "JBL Quantum 950 Wireless", false),
        new(0x213E, "JBL Quantum 950X Wireless", true), new(0x2140, "JBL Quantum 950X Wireless", false),
        new(0x218D, "JBL Quantum 520 Wireless", true), new(0x218E, "JBL Quantum 520 Wireless", false),
        new(0x21A7, "JBL Quantum 520X Wireless", true), new(0x21A9, "JBL Quantum 520X Wireless", false),
        new(0x219A, "JBL Quantum 850 Wireless", true), new(0x219B, "JBL Quantum 850 Wireless", false),
    };

    /// <summary>page 0 = n'importe quelle collection.</summary>
    public sealed record Hidv1Model(ushort Pid, int Iface, ushort Page, string Name);
    public static readonly Hidv1Model[] Hidv1Models =
    {
        new(0x206B, 3, 0x0001, "JBL Quantum 350 Wireless"),
        new(0x205C, 5, 0, "JBL Quantum 610 Wireless"),
        new(0x2115, 5, 0x0001, "JBL Quantum 360 Wireless"),
    };

    public static byte[] BuildBatteryRequest() =>
        new byte[] { 0x88, 0x00, 0xDD, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, (byte)BatteryFeature, (byte)(BatteryFeature >> 8) };

    public enum Hidv3Result { NotThisReply, Error, Battery }

    /// <summary>Analyse un rapport d'entrée HIDV3 : l'octet de batterie [bit 7 charge | pourcentage], ou « pas cette réponse » / « erreur ».</summary>
    public static (Hidv3Result Result, int Percent, bool Charging) ParseHidv3(byte[] r)
    {
        if (r.Length < 16) return (Hidv3Result.NotThisReply, -1, false);
        if (r[0] != 0x86 || r[1] != 0x00 || r[2] != 0xDD || r[3] != 0x01 || r[4] != 0x00 || r[5] != 0x01) return (Hidv3Result.NotThisReply, -1, false);
        if ((r[10] & 0x80) != 0) return (Hidv3Result.Error, -1, false);
        if (r[11] != (byte)BatteryFeature || r[12] != (byte)(BatteryFeature >> 8)) return (Hidv3Result.NotThisReply, -1, false);
        if (r[13] == 0 && r[14] == 0) return (Hidv3Result.NotThisReply, -1, false);
        int pct = r[15] & 0x7F;
        return pct <= 100 ? (Hidv3Result.Battery, pct, (r[15] & 0x80) != 0) : (Hidv3Result.Error, -1, false);
    }

    /// <summary>Octet de charge d'un rapport de fonctionnalité HIDV1 ; null si l'identifiant de la réponse n'est pas celui demandé.</summary>
    public static int? FeatureByte(byte id, byte[] data) => data.Length >= 2 && data[0] == id ? data[1] : null;

    static bool GetBatteryHidv3(HidDev d, out int percent, out bool charging)
    {
        percent = -1; charging = false;
        d.Flush();
        if (!d.SetFeature(BuildBatteryRequest())) return false;
        long end = Environment.TickCount64 + 800;
        for (; ; )
        {
            int left = (int)(end - Environment.TickCount64);
            if (left <= 0) return false;
            var r = d.Read(left);
            if (r == null || r.Length < 16) return false;
            var p = ParseHidv3(r);
            if (p.Result == Hidv3Result.NotThisReply) continue;
            if (p.Result == Hidv3Result.Error) return false;
            percent = p.Percent; charging = p.Charging;
            return true;
        }
    }

    static int? GetByte(HidDev d, byte id) => d.GetFeature(id, out var b) ? FeatureByte(id, b) : null;

    public static void Poll(PollContext ctx)
    {
        foreach (var m in Hidv1Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c => c.Iface == m.Iface && c.FeatLen >= 2 && (m.Page == 0 || (c.Page == m.Page && c.Usage == 0))))
            {
                using var d = new HidDev();
                if (!d.OpenFeatureOnly(c)) continue;
                if (GetByte(d, 0x68) != 1) continue;
                int? pct = GetByte(d, 0x49);
                if (pct is null or > 100) continue;
                ctx.Add(new DeviceStatus { Key = "jbl:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Headset, Via = "2.4 GHz receiver", Percent = pct.Value }, Vid, m.Pid);
                break;
            }
        }
        foreach (var m in Hidv3Models)
        {
            if (ctx.IsClaimed(Vid, m.Pid)) continue;
            foreach (var c in ctx.Find(Vid, m.Pid, c => c.Page == 0xFF12 && c.FeatLen >= 11 && c.InLen >= 16))
            {
                using var d = new HidDev();
                if (!d.Open(c)) continue;
                if (!GetBatteryHidv3(d, out int pct, out bool chg)) continue;
                ctx.Add(new DeviceStatus
                {
                    Key = "jbl:" + HidUtil.Hex4(m.Pid), Name = m.Name, Kind = DeviceKind.Headset,
                    Via = m.Dongle ? "2.4 GHz receiver" : "USB cable", Percent = pct, Charging = chg,
                }, Vid, m.Pid);
                break;
            }
        }
    }
}
