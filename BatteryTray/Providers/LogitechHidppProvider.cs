using System.Text;
using HidSharp;

namespace BatteryTray.Providers;

/// <summary>
/// Appareils Logitech via récepteur (HID++ 2.0, rapport long 0x11). Features de batterie :
/// 0x1004 (Unified Battery) et 0x1000 (Battery Level Status) donnent un pourcentage ;
/// 0x1001 (Battery Voltage) ne donne QU'UNE TENSION : on la rapporte telle quelle, sans
/// la convertir en pourcentage (ce serait une estimation). Spécification HID++ issue de la
/// communauté (Solaar, libratbag). Lecture seule.
/// </summary>
public sealed class LogitechHidppProvider : IBatteryProvider
{
    public string Name => "Logitech HID++";
    const byte SwId = 0x0A;
    static readonly VoltageCurve.Smoother Smoother = new(5);

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x046D).Where(SupportsLong))
        {
            if (!dev.TryOpen(out HidStream s)) continue;
            using (s)
            {
                s.ReadTimeout = 400;
                s.WriteTimeout = 1000; // sans délai, un récepteur qui ne répond plus bloquerait la lecture pour toujours
                foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var pong = Request(s, idx, 0x00, 1, new byte[] { 0, 0, SwId });
                    if (pong == null || pong[2] is 0x8F or 0xFF) continue;
                    string name = ReadName(s, idx) ?? $"Logitech {dev.ProductID:X4}";
                    var r = ReadBattery(s, idx, name, dev.ProductID);
                    if (r != null) list.Add(r);
                }
            }
        }
        return list;
    }

    /// <summary>« G502 LIGHTSPEED Wireless Gaming Mouse » → « G502 LIGHTSPEED » : le nom complet était tronqué dans le flyout.</summary>
    public static string ShortName(string name)
    {
        foreach (string suffix in new[] { " Wireless Gaming Mouse", " Wireless Gaming Headset", " Wireless Gaming Keyboard", " Gaming Mouse", " Wireless Mouse" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string shorter = name[..^suffix.Length].Trim();
            // un nom qui n'est QUE le suffixe (« Wireless Gaming Mouse ») ne doit pas devenir « Wireless »
            return shorter.Length == 0 || shorter.Equals("Wireless", StringComparison.OrdinalIgnoreCase) ? name : shorter;
        }
        return name;
    }

    static BatteryReading? ReadBattery(HidStream s, byte idx, string name, int pid)
    {
        name = ShortName(name);
        string id = $"logi-{pid:X4}-{idx}";
        var kind = name.Contains("headset", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Headset
                 : name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard
                 : DeviceKind.Mouse;

        int f = FeatureIndex(s, idx, 0x1004);
        if (f > 0 && Request(s, idx, (byte)f, 1, Array.Empty<byte>()) is { } a && a[2] == f)
            return new BatteryReading(id, name, kind, a[4], Charge(a[6]), null, DateTime.Now);

        f = FeatureIndex(s, idx, 0x1000);
        if (f > 0 && Request(s, idx, (byte)f, 0, Array.Empty<byte>()) is { } b && b[2] == f)
            return new BatteryReading(id, name, kind, b[4], Charge(b[6]), null, DateTime.Now);

        // Tension seulement : la souris ne donne pas de pourcentage. Il est ESTIMÉ d'après une courbe Li-ion typique,
        // sur la médiane des dernières tensions (lissage), et toujours affiché avec « ~ ».
        // Octet d'état : le bit 7 (0x80) indique une alimentation externe (câble branché) d'après la documentation
        // communautaire HID++ ; on l'utilise pour afficher « en charge ».
        f = FeatureIndex(s, idx, 0x1001);
        if (f > 0 && Request(s, idx, (byte)f, 0, Array.Empty<byte>()) is { } c && c[2] == f)
        {
            int mv = Smoother.Add(id, (c[4] << 8) | c[5]);
            bool externalPower = (c[6] & 0x80) != 0;
            return new BatteryReading(id, name, kind, VoltageCurve.Estimate(mv), externalPower ? ChargeState.Charging : null,
                $"{mv} mV", DateTime.Now)
            {
                PercentEstimated = true,
            };
        }

        return null;
    }

    static string? Charge(byte st) => st switch { 1 or 4 => ChargeState.Charging, 3 => ChargeState.Full, _ => null };

    static bool SupportsLong(HidDevice d)
    {
        try { return d.GetReportDescriptor().OutputReports.Any(r => r.ReportID == 0x11); } catch { return false; }
    }

    static int FeatureIndex(HidStream s, byte idx, ushort featureId)
    {
        var r = Request(s, idx, 0x00, 0, new[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) });
        return r == null || r[2] != 0x00 ? -1 : r[4];
    }

    static string? ReadName(HidStream s, byte idx)
    {
        int f = FeatureIndex(s, idx, 0x0005);
        if (f <= 0) return null;
        var c = Request(s, idx, (byte)f, 0, Array.Empty<byte>());
        if (c == null || c[2] != f) return null;
        int len = c[4];
        var sb = new StringBuilder();
        while (sb.Length < len && sb.Length < 64)
        {
            var r = Request(s, idx, (byte)f, 1, new[] { (byte)sb.Length });
            if (r == null || r[2] != f) break;
            int before = sb.Length;
            for (int i = 4; i < r.Length && sb.Length < len; i++) { if (r[i] == 0) break; sb.Append((char)r[i]); }
            if (sb.Length == before) break;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static byte[]? Request(HidStream s, byte devIdx, byte featIdx, int fn, byte[] parms)
    {
        var buf = new byte[20];
        buf[0] = 0x11; buf[1] = devIdx; buf[2] = featIdx; buf[3] = (byte)((fn << 4) | SwId);
        Array.Copy(parms, 0, buf, 4, Math.Min(parms.Length, 16));
        try { s.Write(buf, 0, buf.Length); } catch { return null; }

        var end = DateTime.UtcNow.AddMilliseconds(500);
        var rbuf = new byte[Math.Max(s.Device.GetMaxInputReportLength(), 20)];
        while (DateTime.UtcNow < end)
        {
            int n;
            try { n = s.Read(rbuf, 0, rbuf.Length); } catch { return null; }
            if (n < 7 || (rbuf[0] != 0x11 && rbuf[0] != 0x10) || rbuf[1] != devIdx) continue;
            var copy = rbuf.AsSpan(0, Math.Min(n, 20)).ToArray();
            if (rbuf[2] is 0xFF or 0x8F) return copy;
            if (rbuf[2] == featIdx && (rbuf[3] & 0x0F) == SwId) return copy;
        }
        return null;
    }
}
