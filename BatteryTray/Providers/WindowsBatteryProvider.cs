using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.System.Power;

namespace BatteryTray.Providers;

/// <summary>
/// Source GÉNÉRIQUE (toutes marques) : les appareils que Windows expose comme « batteries » (API officielle
/// Windows.Devices.Power). Ce sont notamment les périphériques HID qui déclarent la page d'usage standard
/// « Battery System » (0x85) : certains claviers, souris, stylets, trackpads, onduleurs… Windows s'occupe de
/// lire le niveau ; la batterie du PC lui-même (portable) est ignorée.
/// </summary>
public sealed class WindowsBatteryProvider : IBatteryProvider
{
    public string Name => "Batteries Windows";

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        var infos = Wait(DeviceInformation.FindAllAsync(Battery.GetDeviceSelector()));
        foreach (var info in infos)
        {
            if (info.Id.Contains("ACPI", StringComparison.OrdinalIgnoreCase)) continue; // batterie du PC portable : pas un périphérique

            BatteryReport? report;
            try { report = Wait(Battery.FromIdAsync(info.Id))?.GetReport(); }
            catch { continue; }
            if (report == null || report.Status == BatteryStatus.NotPresent) continue;

            if (BatteryMath.Percent(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours) is not int pct) continue;

            string name = string.IsNullOrWhiteSpace(info.Name) ? "Batterie" : info.Name;
            string? charge = report.Status == BatteryStatus.Charging ? ChargeState.Charging : null;
            list.Add(new BatteryReading("win-" + Hash(info.Id), name, DeviceNames.Guess(name), pct, charge, null, DateTime.Now));
        }
        return list;
    }

    static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) => WinRt.Wait(op);
}

/// <summary>Calculs de pourcentage à partir de capacités (logique pure, testée).</summary>
public static class BatteryMath
{
    /// <summary>Restant ÷ pleine charge en %, borné à 0-100 ; null si l'une des valeurs manque ou si la pleine charge est nulle.</summary>
    public static int? Percent(int? remaining, int? full)
    {
        if (remaining is not int r || full is not int f || f <= 0 || r < 0) return null;
        return Math.Clamp((int)Math.Round(r * 100.0 / f), 0, 100);
    }
}
