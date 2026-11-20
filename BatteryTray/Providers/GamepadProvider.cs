using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryTray.Providers;

/// <summary>
/// Manettes reconnues par Windows.Gaming.Input (Xbox, et manettes compatibles). Le pourcentage
/// est calculé à partir des capacités réelles fournies par la manette (restante / pleine charge).
/// Une manette filaire ou sans batterie n'est pas affichée.
/// </summary>
public sealed class GamepadProvider : IBatteryProvider
{
    public string Name => "Manettes";

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        int i = 0;
        foreach (var pad in Gamepad.Gamepads)
        {
            i++;
            var report = pad.TryGetBatteryReport();
            if (report == null || report.Status == BatteryStatus.NotPresent) continue;
            if (BatteryMath.Percent(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours) is not int pct) continue;

            // Identifiant STABLE : l'objet .NET change à chaque lancement, ce qui créait une « nouvelle » manette
            // (et une ligne grise « hors ligne » en double) à chaque redémarrage de l'application.
            string? stable = null, display = null;
            try
            {
                var raw = RawGameController.FromGameController(pad);
                stable = raw?.NonRoamableId;
                display = raw?.DisplayName;
            }
            catch { /* identifiant indisponible : repli sur la position */ }

            string name = string.IsNullOrWhiteSpace(display) ? Loc.T("device.gamepad", i) : display!;
            string id = "pad-" + Short(string.IsNullOrEmpty(stable) ? $"index{i}" : stable);
            string? charge = report.Status == BatteryStatus.Charging ? ChargeState.Charging : null;
            list.Add(new BatteryReading(id, name, DeviceKind.Gamepad, pct, charge, null, DateTime.Now));
        }
        return list;
    }

    static string Short(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];
}
