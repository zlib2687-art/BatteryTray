using BatteryTray.Providers;
using Xunit;

namespace BatteryTray.Tests;

public class DeviceKindAndHidingTests
{
    [Theory]
    [InlineData("DJI Mic 2", DeviceKind.Microphone)]
    [InlineData("RODE Wireless GO II", DeviceKind.Microphone)]
    [InlineData("Wireless Microphone", DeviceKind.Microphone)]
    [InlineData("Micro-cravate Hollyland", DeviceKind.Microphone)]
    [InlineData("Microsoft Bluetooth Mouse", DeviceKind.Mouse)]      // « micro » dans « Microsoft » : pas un micro
    [InlineData("Logitech MX Master 3", DeviceKind.Mouse)]
    [InlineData("AirPods Pro", DeviceKind.Headset)]
    [InlineData("Headset Microphone", DeviceKind.Headset)]            // un casque avec micro reste un casque
    [InlineData("Logitech MX Keys", DeviceKind.Keyboard)]
    [InlineData("Xbox Wireless Controller", DeviceKind.Gamepad)]
    [InlineData("Logitech StreamCam", DeviceKind.Webcam)]
    [InlineData("Appareil inconnu", DeviceKind.Other)]
    public void Device_kind_is_guessed_from_the_bluetooth_name(string name, DeviceKind expected)
    {
        Assert.Equal(expected, BluetoothProvider.KindFromName(name));
    }

    [Theory]
    [InlineData("G502 LIGHTSPEED Wireless Gaming Mouse", "G502 LIGHTSPEED")]
    [InlineData("PRO X 2 LIGHTSPEED Wireless Gaming Headset", "PRO X 2 LIGHTSPEED")]
    [InlineData("MX Master 3", "MX Master 3")]            // rien à retirer
    [InlineData("Wireless Gaming Mouse", "Wireless Gaming Mouse")] // un nom qui n'est QUE le suffixe reste intact
    public void Long_logitech_names_are_shortened_for_the_flyout(string name, string expected)
    {
        Assert.Equal(expected, LogitechHidppProvider.ShortName(name));
        Assert.Contains("G502 LIGHTSPEED", LogitechHidppProvider.ShortName("G502 LIGHTSPEED Wireless Gaming Mouse"));
        Assert.Equal(25, DeviceDefaults.RatedHoursFor(LogitechHidppProvider.ShortName("G502 LIGHTSPEED Wireless Gaming Mouse")));
    }

    [Fact]
    public void Percent_text_marks_estimates_and_never_invents_a_missing_value()
    {
        var r = new BatteryReading("a", "A", DeviceKind.Mouse, 73, null, null, DateTime.Now);
        Assert.Equal("73 %", r.PercentText);
        Assert.Equal("~73 %", (r with { PercentEstimated = true }).PercentText);
        Assert.Equal("—", (r with { Percent = null }).PercentText);
    }

    // ---------------------------------------------------------------- courbe tension -> pourcentage (G502)
    [Fact]
    public void Voltages_measured_on_the_g502_map_to_a_full_battery()
    {
        Assert.Equal(100, VoltageCurve.Estimate(4209)); // câble branché : G HUB affichait 100 %
        Assert.Equal(100, VoltageCurve.Estimate(4204));
        Assert.InRange(VoltageCurve.Estimate(4172), 97, 100); // débranchée : ~99 %
    }

    [Fact]
    public void Voltage_curve_is_clamped_and_never_decreasing()
    {
        Assert.Equal(100, VoltageCurve.Estimate(5000));
        Assert.Equal(0, VoltageCurve.Estimate(3000));
        int previous = -1;
        for (int mv = 3400; mv <= 4300; mv += 5)
        {
            int p = VoltageCurve.Estimate(mv);
            Assert.InRange(p, 0, 100);
            Assert.True(p >= previous, $"{mv} mV : {p} % < {previous} %");
            previous = p;
        }
    }

    [Theory]
    [InlineData(3811, 50)]
    [InlineData(3922, 70)]
    [InlineData(3989, 80)]
    public void Voltage_curve_hits_its_reference_points(int mv, int expected) =>
        Assert.Equal(expected, VoltageCurve.Estimate(mv));

    [Fact]
    public void The_smoother_ignores_a_single_noisy_voltage_reading()
    {
        var smoother = new VoltageCurve.Smoother(5);
        foreach (int mv in new[] { 4172, 4170, 4171, 4173 }) smoother.Add("g502", mv);

        int withSpike = smoother.Add("g502", 4204); // un pic isolé (ex. câble touché)
        Assert.InRange(withSpike, 4170, 4173);       // la médiane ne bouge pas
        Assert.Equal(4172, new VoltageCurve.Smoother().Add("other", 4172)); // les appareils sont indépendants
    }

    [Fact]
    public void The_g502_gets_its_reference_autonomy_and_an_estimated_percentage_is_displayed()
    {
        var s = new AppSettings();
        var g502 = new BatteryReading("logi", "G502 LIGHTSPEED Wireless Gaming Mouse", DeviceKind.Mouse, 99, null, "4172 mV", DateTime.Now)
        {
            PercentEstimated = true,
            AutonomyPending = true,
        };

        var v = DeviceView.Prepare(new[] { g502 }, s, out _).Single();
        Assert.Equal("~99 %", v.PercentText);
        Assert.Equal(24.75, v.AutonomyHours!.Value, 6);   // 99 % de 25 h (valeur de G HUB)
        Assert.True(v.AutonomyFromRating);
    }

    [Fact]
    public void A_voltage_only_device_has_no_percentage_and_is_hidden_by_default()
    {
        // ex. Logitech G502 : la souris ne donne que sa tension, on n'en tire aucun pourcentage
        var s = new AppSettings();
        var voltageOnly = new BatteryReading("g502", "G502", DeviceKind.Mouse, null, null, "4204 mV", DateTime.Now);

        Assert.Empty(DeviceView.Prepare(new[] { voltageOnly }, s, out _));
        Assert.True(s.Devices["g502"].Hidden);
        Assert.True(s.Devices["g502"].AutoHidden);
    }

    [Fact]
    public void A_known_device_without_percentage_and_without_preference_is_hidden_too()
    {
        // cas réel : appareil déjà connu (ancien niveau mémorisé) mais sans réglage enregistré
        var s = new AppSettings();
        s.Known["g502"] = new KnownDevice { Name = "G502", Kind = DeviceKind.Mouse, LastPercent = 100, LastSeen = DateTime.Now };
        var noLevel = new BatteryReading("g502", "G502", DeviceKind.Mouse, null, null, "4172 mV", DateTime.Now);

        Assert.Empty(DeviceView.Prepare(new[] { noLevel }, s, out bool changed));
        Assert.True(changed);
        Assert.True(s.Devices["g502"].AutoHidden);
    }

    [Fact]
    public void A_device_hidden_for_lack_of_percentage_reappears_when_a_real_percentage_exists()
    {
        var s = new AppSettings();
        var noLevel = new BatteryReading("d", "Dev", DeviceKind.Mouse, null, null, null, DateTime.Now);
        Assert.Empty(DeviceView.Prepare(new[] { noLevel }, s, out _));

        var view = DeviceView.Prepare(new[] { noLevel with { Percent = 80 } }, s, out bool changed);
        Assert.True(changed);
        Assert.Single(view);
    }

    [Fact]
    public void A_device_hidden_by_the_user_stays_hidden_even_with_a_percentage()
    {
        var s = new AppSettings();
        var r = new BatteryReading("a", "A", DeviceKind.Mouse, 80, null, null, DateTime.Now);
        DeviceView.Prepare(new[] { r }, s, out _);
        s.Devices["a"] = new DevicePref { Hidden = true }; // choix de l'utilisateur (AutoHidden faux)

        Assert.Empty(DeviceView.Prepare(new[] { r }, s, out _));
    }
}
