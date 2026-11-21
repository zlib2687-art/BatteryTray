using Xunit;

namespace BatteryTray.Tests;

/// <summary>Bug « l'autonomie remonte » sur les appareils Razer : le niveau oscille de 1 point autour du vrai niveau.</summary>
public class AutonomyJitterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-jitter-" + Guid.NewGuid().ToString("N"));
    readonly DateTime _t0 = new(2026, 11, 21, 9, 0, 0);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static BatteryReading R(string id, int p, DateTime at, string name = "Razer Viper V3 Pro", string? charge = null) =>
        new(id, name, DeviceKind.Mouse, p, charge, null, at);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OscillationOfOnePointDoesNotRaiseTheDuration(int amplitude)
    {
        var est = new AutonomyEstimator(Path.Combine(_dir, "h.json"));
        var ratchet = new AutonomyRatchet();
        var rnd = new Random(7);
        double? previous = null; int previousPercent = 100;
        for (int m = 0; m <= 8 * 60; m += 1)
        {
            int truth = (int)Math.Round(100 - 3.0 * m / 60.0);             // 3 points par heure
            int shown = Math.Clamp(truth + rnd.Next(0, amplitude + 1), 0, 100); // bruit vers le haut seulement (Razer : valeur brute arrondie)
            var r = ratchet.Apply(est.Apply(new[] { R("razer-0000", shown, _t0.AddMinutes(m)) }))[0];
            if (r.AutonomyHours is double h && !r.AutonomyLowerBound)
            {
                if (previous is double prev && m > 90) Assert.True(h <= prev + 0.02, $"à {m} min : {h:0.00} h > {prev:0.00} h");
                previous = h;
            }
            previousPercent = shown;
        }
        Assert.NotNull(previous);
        Assert.InRange(previous!.Value, 1, 40);
    }

    [Fact]
    public void ARealRechargeStillResetsTheEstimate()
    {
        var est = new AutonomyEstimator(Path.Combine(_dir, "c.json"));
        for (int m = 0; m <= 240; m += 2) est.Apply(new[] { R("razer-0001", 100 - m / 20, _t0.AddMinutes(m)) });
        Assert.NotEmpty(est.SamplesFor("razer-0001"));
        est.Apply(new[] { R("razer-0001", 95, _t0.AddMinutes(244)) });        // +8 points : recharge, pas du bruit
        Assert.Single(est.SamplesFor("razer-0001"));
    }

    [Fact]
    public void RatchetKeepsGuardingThroughSmallRisesButNotThroughRealOnes()
    {
        var ratchet = new AutonomyRatchet();
        BatteryReading Q(int p, double hours, int minutes) =>
            new BatteryReading("razer-1", "Razer BlackShark", DeviceKind.Headset, p, null, null, _t0.AddMinutes(minutes)) { AutonomyHours = hours };
        var a = ratchet.Apply(new[] { Q(60, 20, 0) })[0];
        var b = ratchet.Apply(new[] { Q(61, 25, 10) })[0];            // +1 point : bruit, la durée ne remonte pas
        Assert.True(b.AutonomyHours <= a.AutonomyHours!.Value);
        var c = ratchet.Apply(new[] { Q(62, 30, 20) })[0];            // 62 reste dans la tolérance du niveau mémorisé le plus bas (60)
        Assert.True(c.AutonomyHours <= b.AutonomyHours!.Value);
        var d = ratchet.Apply(new[] { Q(75, 40, 30) })[0];            // +15 : recharge, on accepte la nouvelle estimation
        Assert.Equal(40, d.AutonomyHours);
    }
}
