using System.Text.Json;
using Xunit;

namespace BatteryTray.Tests;

public class AutonomyLearnedClampTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-clamp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void LearnedRateFarFromTheRatedAutonomyIsBounded()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "h.json");
        var store = new AutonomyEstimator.Store { Version = AutonomyEstimator.CurrentVersion };
        store.Rates["hs"] = 0.8; // 125 h pleine charge appris, alors que l'autonomie annoncée est de 50 h
        File.WriteAllText(path, JsonSerializer.Serialize(store));
        var est = new AutonomyEstimator(path);
        var r = est.Apply(new[] { new BatteryReading("hs", "Logitech PRO X 2", DeviceKind.Headset, 90, null, null, DateTime.Now) })[0];
        Assert.InRange(r.AutonomyHours!.Value, 40, 90 * 0.01 * 75 + 0.1);   // ≤ 90 % × 75 h, pas 112 h
    }

    [Fact]
    public void UnknownModelsKeepTheirLearnedRate()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "h2.json");
        var store = new AutonomyEstimator.Store { Version = AutonomyEstimator.CurrentVersion };
        store.Rates["x"] = 0.8;
        File.WriteAllText(path, JsonSerializer.Serialize(store));
        var r = new AutonomyEstimator(path).Apply(new[] { new BatteryReading("x", "Modèle inconnu", DeviceKind.Mouse, 90, null, null, DateTime.Now) })[0];
        Assert.InRange(r.AutonomyHours!.Value, 111, 113);
    }
}
