namespace BatteryTray;

/// <summary>Appareils et historique d'exemple (aperçus, mode <c>--demo</c>) : aucun matériel nécessaire.</summary>
static class DemoData
{
    public static List<BatteryReading> Readings(DateTime now) => new()
    {
        new("a", "Razer DeathAdder V4 Pro", DeviceKind.Mouse, 100, null, null, now) { AutonomyHours = 26.7, AutonomyLowerBound = true },
        new("b", "Logitech PRO X 2", DeviceKind.Headset, 72, null, null, now) { AutonomyHours = 19.2 },
        new("c", "Manette 1", DeviceKind.Gamepad, 35, ChargeState.Charging, null, now) { ChargeEtaHours = 1.1 },
        new("r", "Razer DeathAdder V4 Pro", DeviceKind.Mouse, 100, ChargeState.Charging, null, now),
        new("d", "Écouteurs", DeviceKind.Headset, 75, null, null, now)
        {
            Parts = new[] { new BatteryPart(BatteryParts.Left, 80), new BatteryPart(BatteryParts.Right, 75) },
        },
        new("e", "Micro DJI", DeviceKind.Microphone, 64, null, null, now.AddHours(-3)) { Offline = true },
    };

    /// <summary>80 % → 72 % en 2 h : de quoi montrer le mini-graphique.</summary>
    public static List<AutonomyEstimator.Sample> History(DateTime now) =>
        Enumerable.Range(0, 13)
            .Select(i => new AutonomyEstimator.Sample(now.AddMinutes(-120 + i * 10), 80 - (int)Math.Round(i * 8 / 12.0)))
            .ToList();
}
