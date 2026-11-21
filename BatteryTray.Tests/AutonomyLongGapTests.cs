using Xunit;

namespace BatteryTray.Tests;

public class AutonomyLongGapTests
{
    static BatteryReading R(int p, double h, DateTime at) =>
        new("d", "Appareil", DeviceKind.Mouse, p, null, null, at) { AutonomyHours = h };

    [Fact]
    public void DurationDoesNotRiseWhenPercentDropsAfterALongGap()
    {
        var ratchet = new AutonomyRatchet();
        var t = new DateTime(2026, 1, 1, 8, 0, 0);
        double first = ratchet.Apply(new[] { R(90, 60, t) })[0].AutonomyHours!.Value;
        double later = ratchet.Apply(new[] { R(89, 100, t.AddHours(5)) })[0].AutonomyHours!.Value; // 5 h sans lecture, estimation plus haute
        Assert.True(later <= first * 89 / 90 + 1e-9, $"{later} > {first}");
    }

    [Fact]
    public void ChargingStillResetsAfterALongGap()
    {
        var ratchet = new AutonomyRatchet();
        var t = new DateTime(2026, 1, 1, 8, 0, 0);
        ratchet.Apply(new[] { R(30, 20, t) });
        Assert.Equal(70, ratchet.Apply(new[] { R(90, 70, t.AddHours(5)) })[0].AutonomyHours!.Value, 6);
    }
}
