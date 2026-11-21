using System.Runtime.InteropServices;
using HidSharp;

namespace BatteryTray.Providers;

/// <summary>
/// Souris Razer : requête get_battery (classe 0x07, commande 0x80) en rapport de fonctionnalité
/// de 90 octets. Protocole rétro-ingénierié par la communauté (OpenRazer), pas par Razer.
/// L'interface souris étant interdite en lecture/écriture par Windows, on l'ouvre avec un accès
/// nul (requête de périphérique), comme le fait hidapi. Lecture seule.
/// </summary>
public sealed class RazerProvider : IBatteryProvider
{
    public string Name => "Razer";

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1532).Where(d => Safe(() => d.GetMaxFeatureReportLength()) >= 90))
        {
            var r = ReadOne(dev);
            if (r != null) list.Add(r);
        }
        return list;
    }

    static BatteryReading? ReadOne(HidDevice dev)
    {
        IntPtr h = CreateFile(dev.DevicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        try
        {
            // longueur annoncée par le périphérique : bornée, un pilote défectueux ne doit pas nous faire allouer des Mo
            int len = Math.Clamp(dev.GetMaxFeatureReportLength(), 91, 4096);
            foreach (byte tid in new byte[] { 0x1F, 0xFF, 0x3F, 0x9F })
            {
                var bat = Query(h, len, tid, 0x80);
                if (bat == null || bat[1] != 0x02) continue; // 0x02 = succès
                int pct = (int)Math.Round(bat[10] * 100.0 / 255.0);
                var chg = Query(h, len, tid, 0x84);
                string? charge = chg != null && chg[1] == 0x02 && chg[10] == 1 ? ChargeState.Charging : null;
                string raw = dev.GetProductName() ?? $"Razer {dev.ProductID:X4}";
                string name = raw.StartsWith("Razer", StringComparison.OrdinalIgnoreCase) ? raw : "Razer " + raw;
                return new BatteryReading($"razer-{dev.ProductID:X4}", name, DeviceKind.Mouse, Math.Clamp(pct, 0, 100), charge, null, DateTime.Now);
            }
        }
        finally { CloseHandle(h); }
        return null;
    }

    static byte[]? Query(IntPtr h, int len, byte tid, byte cmd)
    {
        var buf = new byte[len];
        buf[2] = tid; buf[6] = 2; buf[7] = 0x07; buf[8] = cmd;
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= buf[i];
        buf[89] = crc;
        if (!HidD_SetFeature(h, buf, (uint)len)) return null;
        Thread.Sleep(80);
        var resp = new byte[len];
        return HidD_GetFeature(h, resp, (uint)len) ? resp : null;
    }

    static int Safe(Func<int> f) { try { return f(); } catch { return 0; } }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, uint len);
}
