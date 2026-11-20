using HidSharp;

namespace BatteryTray.Providers;

/// <summary>
/// Logitech PRO X 2 LIGHTSPEED (USB 046D:0AF7). Requête de batterie sur le rapport 0x51
/// de la collection vendeur 0xFFA0. Protocole décrit par la communauté (patch noyau Linux
/// citant HeadsetControl), pas par Logitech. Lecture seule.
/// </summary>
public sealed class ProX2Provider : IBatteryProvider
{
    public string Name => "Logitech PRO X 2";

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var dev = DeviceList.Local.GetHidDevices(0x046D, 0x0AF7)
            .FirstOrDefault(d => d.DevicePath.Contains("col03", StringComparison.OrdinalIgnoreCase));
        if (dev == null || !dev.TryOpen(out HidStream st)) return Array.Empty<BatteryReading>();

        using (st)
        {
            st.ReadTimeout = 500;
            st.WriteTimeout = 1000; // sans délai, un dongle qui ne répond plus bloquerait la lecture pour toujours
            var request = new byte[64];
            new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }.CopyTo(request, 0);
            st.Write(request);

            var buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 64)];
            var end = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < end)
            {
                int n;
                try { n = st.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }
                // trame batterie : id 0x51, type 0x0B, étiquette 0x04, niveau <= 100
                if (n >= 13 && buf[0] == 0x51 && buf[1] == 0x0B && buf[8] == 0x04 && buf[10] <= 100)
                {
                    string? charge = buf[12] == 0x02 ? ChargeState.Charging : null; // seule valeur d'état connue
                    return new[] { new BatteryReading("prox2", "Logitech PRO X 2", DeviceKind.Headset, buf[10], charge, null, DateTime.Now) };
                }
            }
        }
        return Array.Empty<BatteryReading>();
    }
}
