using Xunit;

namespace BatteryTray.Tests;

public class BrandsAndCliTests
{
    [Theory]
    [InlineData("Razer DeathAdder V4 Pro", "razer")]
    [InlineData("Razer BlackShark V2 Pro", "razer")]
    [InlineData("Logitech PRO X 2", "logitech")]
    [InlineData("G502 LIGHTSPEED", "logitech")]
    [InlineData("MX Master 3S", "logitech")]
    [InlineData("Arctis Nova 7", "steelseries")]
    [InlineData("Corsair Virtuoso RGB Wireless", "corsair")]
    [InlineData("HyperX Cloud III Wireless", "hyperx")]
    [InlineData("DualSense Wireless Controller", "playstation")]
    [InlineData("Xbox Wireless Controller", "xbox")]
    [InlineData("Galaxy Buds2 Pro", "samsung")]
    [InlineData("AirPods Pro", "apple")]
    [InlineData("Jabra Evolve2 65", "jabra")]
    [InlineData("JBL Quantum 810", "jbl")]
    [InlineData("Steam Controller", "steam")]
    public void KnownBrandsAreRecognizedByName(string name, string key) => Assert.Equal(key, Brands.Detect(name)?.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Souris sans nom")]
    [InlineData("Micro DJI")]
    [InlineData("Écouteurs")]
    public void UnknownOrEmptyNamesGiveNoBrand(string? name)
    {
        Assert.Null(Brands.Detect(name));
        Assert.Null(Brands.ColorFor(name, true));
    }

    [Fact]
    public void BrandColorIsDarkerOnLightBackgrounds()
    {
        var onDark = Brands.ColorFor("Razer DeathAdder", false)!.Value;
        var onLight = Brands.ColorFor("Razer DeathAdder", true)!.Value;
        Assert.True(onLight.GetBrightness() < onDark.GetBrightness());
    }

    [Fact]
    public void BrandDetectionSurvivesHostileText()
    {
        var r = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            var text = new string(Enumerable.Range(0, r.Next(0, 300)).Select(_ => (char)r.Next(0, 0x2FF)).ToArray());
            _ = Brands.Detect(text);
        }
        Assert.Null(Brands.Detect(new string('a', 100_000)));
    }

    [Fact]
    public void BrandColorsSettingIsOnByDefaultAndSurvivesSaving()
    {
        Assert.True(new AppSettings().BrandColors);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { BrandColors = false });
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.BrandColors);
    }

    [Fact]
    public void DumpListsHidDevicesWithoutSerialNumbersOrDevicePaths()
    {
        string dump = Cli.BuildDump();
        Assert.StartsWith("BatteryTray --dump", dump);
        Assert.DoesNotContain("HID#", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\?\", dump, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, dump, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DemoDataIsRichEnoughToShowEveryState()
    {
        var now = DateTime.Now;
        var items = DemoData.Readings(now);
        Assert.Contains(items, i => i.IsCharging);
        Assert.Contains(items, i => i.Offline);
        Assert.Contains(items, i => i.Parts is { Count: > 1 });
        Assert.Contains(items, i => i.AutonomyHours != null);
        Assert.True(DemoData.History(now).Count >= 2);
    }

    [Fact]
    public void OutputWritesTheRequestedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "bt-cli-" + Guid.NewGuid().ToString("N") + ".txt");
        try { Cli.Output("bonjour", path); Assert.Equal("bonjour", File.ReadAllText(path)); }
        finally { File.Delete(path); }
    }
}
