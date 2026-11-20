using System.Drawing;
using Xunit;

namespace BatteryTray.Tests;

public class SettingsAndGlyphTests : IDisposable
{
    readonly string _savedDir = AppSettings.DataDir;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsAndGlyphTests() => AppSettings.DataDir = _dir;

    public void Dispose()
    {
        AppSettings.DataDir = _savedDir;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Settings_survive_a_save_and_load_round_trip()
    {
        var s = new AppSettings
        {
            Language = "ar-TN", LowThreshold = 30, OrangeBelow = 50, RedBelow = 10, PollMinutes = 5, NotifyFull = false,
            FlyoutScale = 125,
        };
        s.Devices["a"] = new DevicePref { Hidden = true, Name = "Ma souris", LowThreshold = 15, RatedHours = 90.5 };
        s.Known["a"] = new KnownDevice { Name = "Razer", Kind = DeviceKind.Mouse };
        s.Order = new List<string> { "b", "a" };
        s.LowNotified.Add("a");
        s.Save();

        var loaded = AppSettings.Load();
        Assert.Equal("ar-TN", loaded.Language);
        Assert.Equal(30, loaded.LowThreshold);
        Assert.Equal(50, loaded.OrangeBelow);
        Assert.Equal(10, loaded.RedBelow);
        Assert.Equal(5, loaded.PollMinutes);
        Assert.Equal(125, loaded.FlyoutScale);
        Assert.False(loaded.NotifyFull);
        Assert.True(loaded.Devices["a"].Hidden);
        Assert.Equal("Ma souris", loaded.Devices["a"].Name);
        Assert.Equal(15, loaded.Devices["a"].LowThreshold);
        Assert.Equal(90.5, loaded.Devices["a"].RatedHours);
        Assert.Equal(DeviceKind.Mouse, loaded.Known["a"].Kind);
        Assert.Equal(new[] { "b", "a" }, loaded.Order);
        Assert.Contains("a", loaded.LowNotified);
    }

    [Fact]
    public void Missing_or_corrupted_settings_fall_back_to_defaults()
    {
        Assert.Equal(20, AppSettings.Load().LowThreshold);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppSettings.FilePath, "{ ceci n'est pas du json");
        var loaded = AppSettings.Load();
        Assert.Equal(20, loaded.LowThreshold);
        Assert.True(loaded.NotifyLow);
    }

    [Fact]
    public void Defaults_match_the_specification()
    {
        var s = new AppSettings();
        Assert.Equal(20, s.LowThreshold);
        Assert.Equal(40, s.OrangeBelow); // orange sous 40 %
        Assert.Equal(15, s.RedBelow);    // rouge à 15 % et moins
        Assert.True(s.NotifyLow);
        Assert.True(s.NotifyFull);
        Assert.Equal(100, s.FlyoutScale);
    }

    [Fact]
    public void Flyout_size_grows_with_the_chosen_scale_and_with_the_number_of_devices()
    {
        var normal = FlyoutForm.SizeFor(2, 100);
        var small = FlyoutForm.SizeFor(2, 85);
        var large = FlyoutForm.SizeFor(2, 125);
        var xl = FlyoutForm.SizeFor(2, 150);

        Assert.True(small.Width < normal.Width && normal.Width < large.Width && large.Width < xl.Width);
        Assert.True(small.Height < normal.Height && normal.Height < large.Height && large.Height < xl.Height);
        Assert.Equal(312, normal.Width);                              // taille standard inchangée
        Assert.Equal(1.5, (double)xl.Width / normal.Width, 2);        // 150 % = une fois et demie
        Assert.True(FlyoutForm.SizeFor(3, 100).Height > FlyoutForm.SizeFor(2, 100).Height);
        Assert.Equal(FlyoutForm.SizeFor(1, 100), FlyoutForm.SizeFor(0, 100)); // aucune ligne : on garde la place du message
    }

    [Fact]
    public void Flyout_size_choices_are_the_four_offered_in_the_settings()
    {
        Assert.Equal(new[] { 85, 100, 125, 150 }, FlyoutForm.ScaleChoices);
        Assert.Contains(new AppSettings().FlyoutScale, FlyoutForm.ScaleChoices);
    }

    [Fact]
    public void Absurd_scale_values_are_clamped()
    {
        Assert.Equal(FlyoutForm.SizeFor(1, 70), FlyoutForm.SizeFor(1, -50));
        Assert.Equal(FlyoutForm.SizeFor(1, 200), FlyoutForm.SizeFor(1, 5000));
    }

    [Theory]
    [InlineData(DeviceKind.Headset)]
    [InlineData(DeviceKind.Mouse)]
    [InlineData(DeviceKind.Keyboard)]
    [InlineData(DeviceKind.Gamepad)]
    [InlineData(DeviceKind.Webcam)]
    [InlineData(DeviceKind.Microphone)]
    [InlineData(DeviceKind.Other)]
    public void Every_glyph_draws_something_visible(DeviceKind kind)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            Glyphs.Draw(g, kind, Color.White, 0, 0, 1f);
        }

        int visible = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 0) visible++;
        Assert.True(visible > 100, $"{kind}: seulement {visible} pixels dessinés");
    }

    [Fact]
    public void Glyphs_with_different_kinds_look_different()
    {
        static int Signature(DeviceKind kind)
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp)) Glyphs.Draw(g, kind, Color.White, 0, 0, 1f);
            int h = 17;
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) h = h * 31 + bmp.GetPixel(x, y).A;
            return h;
        }
        var kinds = new[] { DeviceKind.Headset, DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Gamepad, DeviceKind.Webcam, DeviceKind.Microphone };
        Assert.Equal(kinds.Length, kinds.Select(Signature).Distinct().Count());
    }

    [Fact]
    public void Charging_colour_is_a_green_distinct_from_every_level_colour_and_readable_on_both_themes()
    {
        var dark = Theme.ChargingColor(false);   // fond sombre : vert clair
        var light = Theme.ChargingColor(true);   // fond clair : vert plus foncé

        foreach (var c in new[] { dark, light })
            Assert.True(c.G > c.R && c.G > c.B, $"{c} n'est pas vert");   // la composante verte domine
        Assert.NotEqual(dark, light);
        Assert.True(dark.GetBrightness() > light.GetBrightness());        // plus clair sur fond sombre

        int savedOrange = TrayManager.OrangeBelow, savedRed = TrayManager.RedBelow;
        try
        {
            TrayManager.OrangeBelow = 40; TrayManager.RedBelow = 15;
            foreach (int level in new[] { 5, 30, 80 })
            {
                Assert.NotEqual(Theme.LevelColor(level, Color.White), dark);
                Assert.NotEqual(Theme.LevelColor(level, Color.Black), light);
            }
        }
        finally { TrayManager.OrangeBelow = savedOrange; TrayManager.RedBelow = savedRed; }
    }

    [Fact]
    public void Plugged_means_charging_or_charged_and_nothing_else()
    {
        var r = new BatteryReading("a", "A", DeviceKind.Mouse, 80, null, null, DateTime.Now);
        Assert.False(r.IsPlugged);
        Assert.True((r with { Charge = ChargeState.Charging }).IsPlugged);
        Assert.True((r with { Charge = ChargeState.Full }).IsPlugged);
        Assert.False((r with { Charge = "inconnu" }).IsPlugged);
    }

    [Fact]
    public void Level_colour_is_orange_then_red_as_the_battery_drops()
    {
        int savedOrange = TrayManager.OrangeBelow, savedRed = TrayManager.RedBelow;
        try
        {
            TrayManager.OrangeBelow = 40; TrayManager.RedBelow = 15;
            var normal = Color.White;
            Assert.Equal(normal, Theme.LevelColor(60, normal));
            Assert.Equal(normal, Theme.LevelColor(40, normal));
            Assert.NotEqual(normal, Theme.LevelColor(39, normal));
            Assert.NotEqual(Theme.LevelColor(39, normal), Theme.LevelColor(15, normal));
            Assert.Equal(normal, Theme.LevelColor(null, normal));
        }
        finally { TrayManager.OrangeBelow = savedOrange; TrayManager.RedBelow = savedRed; }
    }
}
