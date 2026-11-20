using Xunit;

namespace BatteryTray.Tests;

// ---------------------------------------------------------------- batteries multiples (écouteurs)
public class BatteryPartsTests
{
    [Theory]
    [InlineData(0x000E, "left")]
    [InlineData(0x000F, "right")]
    [InlineData(0x0000, "other")]
    [InlineData(0x0007, "other")]
    [InlineData(0xFFFF, "other")]
    public void Gatt_description_maps_to_left_right_or_other(int description, string expected)
    {
        Assert.Equal(expected, BatteryParts.LabelFromDescription((ushort)description));
    }

    [Fact]
    public void Aggregate_uses_the_weakest_earbud_not_the_full_case()
    {
        var parts = new[]
        {
            new BatteryPart(BatteryParts.Left, 80), new BatteryPart(BatteryParts.Right, 40), new BatteryPart(BatteryParts.Other, 100),
        };
        Assert.Equal(40, BatteryParts.Aggregate(parts));
    }

    [Fact]
    public void Aggregate_falls_back_to_the_lowest_battery_when_nothing_is_labelled()
    {
        var parts = new[] { new BatteryPart(BatteryParts.Other, 90), new BatteryPart(BatteryParts.Other, 55) };
        Assert.Equal(55, BatteryParts.Aggregate(parts));
        Assert.Null(BatteryParts.Aggregate(Array.Empty<BatteryPart>()));
    }

    [Fact]
    public void Parts_text_lists_each_battery_in_the_current_language()
    {
        string saved = Loc.Current;
        try
        {
            var r = new BatteryReading("e", "Buds", DeviceKind.Headset, 75, null, null, DateTime.Now)
            {
                Parts = new[] { new BatteryPart(BatteryParts.Left, 80), new BatteryPart(BatteryParts.Right, 75) },
            };
            Loc.Set("fr");
            Assert.Equal("gauche 80 % · droite 75 %", Loc.PartsText(r));
            Loc.Set("en");
            Assert.Equal("left 80 % · right 75 %", Loc.PartsText(r));

            Assert.Null(Loc.PartsText(r with { Parts = null }));
            Assert.Null(Loc.PartsText(r with { Parts = new[] { new BatteryPart(BatteryParts.Left, 80) } })); // une seule : rien à détailler
        }
        finally { Loc.Set(saved); }
    }
}

// ---------------------------------------------------------------- mini-graphique
public class ChartMathTests
{
    static AutonomyEstimator.Sample S(int minute, int p) => new(new DateTime(2026, 1, 1, 12, 0, 0).AddMinutes(minute), p);
    static readonly RectangleF Area = new(10, 20, 200, 100);

    [Fact]
    public void Points_span_the_whole_area_in_time_and_level()
    {
        var pts = ChartMath.Map(new[] { S(0, 100), S(30, 50), S(60, 0) }, Area);

        Assert.Equal(10, pts[0].X, 3);
        Assert.Equal(210, pts[2].X, 3);
        Assert.Equal(110, pts[1].X, 3);          // au milieu du temps
        Assert.Equal(20, pts[0].Y, 3);           // 100 % en haut
        Assert.Equal(70, pts[1].Y, 3);           // 50 % au milieu
        Assert.Equal(120, pts[2].Y, 3);          // 0 % en bas
    }

    [Fact]
    public void Single_sample_is_centred_and_empty_input_gives_no_points()
    {
        var one = ChartMath.Map(new[] { S(0, 60) }, Area);
        Assert.Single(one);
        Assert.Equal(110, one[0].X, 3);
        Assert.Empty(ChartMath.Map(Array.Empty<AutonomyEstimator.Sample>(), Area));
    }

    [Fact]
    public void Custom_range_zooms_on_the_measured_levels()
    {
        var pts = ChartMath.Map(new[] { S(0, 80), S(60, 60) }, Area, lo: 60, hi: 90);
        Assert.Equal(120, pts[1].Y, 3);                // 60 % = bas de l'échelle zoomée
        Assert.Equal(20 + 100 * (1 - 20f / 30), pts[0].Y, 2);
    }

    [Fact]
    public void Range_is_rounded_to_tens_at_least_twenty_wide_and_within_0_100()
    {
        var (lo, hi) = ChartMath.Range(new[] { S(0, 80), S(60, 72) });
        Assert.Equal((60, 90), (lo, hi));
        Assert.True(lo <= 72 && hi >= 80);

        var flat = ChartMath.Range(new[] { S(0, 50), S(60, 50) });
        Assert.True(flat.Hi - flat.Lo >= 20);

        var top = ChartMath.Range(new[] { S(0, 100), S(60, 99) });
        Assert.Equal(100, top.Hi);
        Assert.True(top.Hi - top.Lo >= 20);

        var bottom = ChartMath.Range(new[] { S(0, 3), S(60, 1) });
        Assert.Equal(0, bottom.Lo);
        Assert.True(bottom.Hi - bottom.Lo >= 20);
    }

    [Fact]
    public void Flyout_with_an_open_chart_is_taller_by_a_fixed_amount()
    {
        var closed = FlyoutForm.SizeFor(3, 100);
        var open = FlyoutForm.SizeFor(3, 100, expanded: true);
        Assert.True(open.Height > closed.Height);
        Assert.Equal(closed.Width, open.Width);
        Assert.Equal(66, open.Height - closed.Height);                          // 66 px à 100 %
        Assert.Equal(99, FlyoutForm.SizeFor(3, 150, true).Height - FlyoutForm.SizeFor(3, 150).Height); // 66 × 1,5
    }
}

// ---------------------------------------------------------------- détection immédiate
public class RefreshPolicyTests
{
    [Fact]
    public void A_device_event_long_after_the_last_read_triggers_a_quick_refresh()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.Equal(RefreshPolicy.DebounceMs, RefreshPolicy.DelayMs(last.AddMinutes(5), last));
        Assert.Equal(RefreshPolicy.DebounceMs, RefreshPolicy.DelayMs(last.AddMinutes(5), DateTime.MinValue));
    }

    [Fact]
    public void An_event_right_after_a_read_waits_so_our_own_reads_cannot_loop()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0);
        int delay = RefreshPolicy.DelayMs(last.AddSeconds(3), last);
        Assert.InRange(delay, 16000, RefreshPolicy.MinSpacingMs);
    }

    [Fact]
    public void Delay_is_never_below_the_debounce()
    {
        var last = new DateTime(2026, 1, 1, 12, 0, 0);
        foreach (int s in new[] { 0, 5, 15, 18, 19, 20, 21, 60, 3600 })
            Assert.True(RefreshPolicy.DelayMs(last.AddSeconds(s), last) >= RefreshPolicy.DebounceMs);
    }
}

// ---------------------------------------------------------------- appareils hors ligne
public class OfflineDeviceTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    static BatteryReading R(string id, int percent, DateTime at, DeviceKind kind = DeviceKind.Headset) =>
        new(id, "Casque", kind, percent, null, null, at);

    [Fact]
    public void A_device_that_disappears_stays_visible_in_grey_with_its_last_value()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 64, T0) }, s, out _, T0);

        var view = DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddHours(3));
        var offline = Assert.Single(view);
        Assert.True(offline.Offline);
        Assert.Equal(64, offline.Percent);
        Assert.Equal(T0, offline.At);                // l'heure de la dernière lecture, pas l'heure actuelle
        Assert.Null(offline.AutonomyHours);
    }

    [Fact]
    public void An_offline_device_goes_back_to_normal_when_it_reappears()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 64, T0) }, s, out _, T0);
        DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddHours(1));

        var view = DeviceView.Prepare(new[] { R("a", 60, T0.AddHours(2)) }, s, out _, T0.AddHours(2));
        var back = Assert.Single(view);
        Assert.False(back.Offline);
        Assert.Equal(60, back.Percent);
    }

    [Fact]
    public void An_offline_device_disappears_after_seven_days()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 64, T0) }, s, out _, T0);

        Assert.Single(DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddDays(6.9)));
        Assert.Empty(DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddDays(7.1)));
    }

    [Fact]
    public void A_hidden_device_is_not_shown_offline_either()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 64, T0) }, s, out _, T0);
        s.Devices["a"] = new DevicePref { Hidden = true };

        Assert.Empty(DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddHours(1)));
    }

    [Fact]
    public void Offline_device_keeps_its_custom_name_and_kind()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 100, T0, DeviceKind.Mouse) }, s, out _, T0);
        s.Devices["a"] = new DevicePref { Name = "Ma souris" };

        var offline = DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddHours(1))[0];
        Assert.Equal("Ma souris", offline.Name);
        Assert.Equal(DeviceKind.Mouse, offline.Kind);
        Assert.Equal("100 %", offline.PercentText);
    }

    [Fact]
    public void A_device_that_never_had_a_percentage_is_never_shown_offline()
    {
        var s = new AppSettings();
        var noLevel = new BatteryReading("g", "G", DeviceKind.Mouse, null, null, "4209 mV", T0);
        DeviceView.Prepare(new[] { noLevel }, s, out _, T0);
        s.Devices["g"].Hidden = false;

        Assert.Empty(DeviceView.Prepare(Array.Empty<BatteryReading>(), s, out _, T0.AddHours(1)));
    }

    [Fact]
    public void Last_values_are_saved_when_they_change_but_not_on_every_identical_read()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", 64, T0) }, s, out bool first, T0);
        DeviceView.Prepare(new[] { R("a", 64, T0.AddMinutes(2)) }, s, out bool same, T0.AddMinutes(2));
        DeviceView.Prepare(new[] { R("a", 63, T0.AddMinutes(4)) }, s, out bool changed, T0.AddMinutes(4));
        DeviceView.Prepare(new[] { R("a", 63, T0.AddMinutes(20)) }, s, out bool stale, T0.AddMinutes(20));

        Assert.True(first);
        Assert.False(same);      // même valeur, lecture récente : pas de réécriture du fichier
        Assert.True(changed);
        Assert.True(stale);      // plus de 10 min depuis la dernière sauvegarde : on rafraîchit l'heure
    }

    [Fact]
    public void Offline_readings_never_trigger_notifications()
    {
        var sink = new FakeSink();
        var s = new AppSettings();
        new Notifier(s, sink).Evaluate(new[] { R("a", 5, T0) with { Offline = true } });
        Assert.Empty(sink.Sent);
        Assert.DoesNotContain("a", s.LowNotified);
    }

    sealed class FakeSink : IToastSink
    {
        public readonly List<string> Sent = new();
        public void Notify(string deviceId, string title, string text, bool warning) => Sent.Add(deviceId);
    }
}

// ---------------------------------------------------------------- temps de charge
public class ChargeEtaTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-charge-" + Guid.NewGuid().ToString("N"));
    readonly DateTime _t0 = new(2026, 11, 20, 10, 0, 0);
    readonly string _savedDir = AppSettings.DataDir;

    public ChargeEtaTests() => AppSettings.DataDir = _dir;

    public void Dispose()
    {
        AppSettings.DataDir = _savedDir;
        try { Directory.Delete(_dir, true); } catch { }
    }

    AutonomyEstimator New() => new(Path.Combine(_dir, "historique.json"));

    static BatteryReading Charging(string id, int p, DateTime at) =>
        new(id, "Test", DeviceKind.Gamepad, p, ChargeState.Charging, null, at);

    BatteryReading SimulateCharge(AutonomyEstimator e, int start, double risePerHour, int minutes)
    {
        BatteryReading last = null!;
        for (int m = 0; m <= minutes; m += 2)
            last = e.Apply(new[] { Charging("dev", (int)Math.Round(start + risePerHour * m / 60.0), _t0.AddMinutes(m)) })[0];
        return last;
    }

    [Fact]
    public void Steady_charge_gives_an_eta_close_to_the_truth()
    {
        // +20 points/h depuis 30 % pendant 30 min -> 40 % : il reste 60 points, soit ~3 h
        var r = SimulateCharge(New(), 30, 20, 30);
        Assert.NotNull(r.ChargeEtaHours);
        Assert.InRange(r.ChargeEtaHours!.Value, 2.4, 3.8);
        Assert.False(r.AutonomyPending);
        Assert.Null(r.AutonomyHours);
    }

    [Fact]
    public void Too_little_charging_data_stays_pending_and_shows_no_eta()
    {
        var r = SimulateCharge(New(), 30, 20, 4);
        Assert.True(r.ChargeEtaPending);
        Assert.Null(r.ChargeEtaHours);
    }

    [Fact]
    public void A_full_device_has_nothing_to_wait_for()
    {
        var r = New().Apply(new[] { Charging("dev", 100, _t0) })[0];
        Assert.Null(r.ChargeEtaHours);
        Assert.False(r.ChargeEtaPending);
    }

    [Fact]
    public void Unplugging_resets_the_charge_measurement()
    {
        var e = New();
        SimulateCharge(e, 30, 20, 30);
        e.Apply(new[] { new BatteryReading("dev", "Test", DeviceKind.Gamepad, 60, null, null, _t0.AddMinutes(40)) });

        var again = e.Apply(new[] { Charging("dev", 60, _t0.AddMinutes(60)) })[0];
        Assert.True(again.ChargeEtaPending || again.ChargeEtaHours != null);
        Assert.Null(again.AutonomyHours);
    }

    [Fact]
    public void Learned_charge_speed_survives_a_restart()
    {
        var first = New();
        SimulateCharge(first, 10, 25, 90); // > 30 min et > 3 points : vitesse apprise
        first.Flush();                      // l'application écrit l'historique à la fermeture

        var second = New();
        var r = second.Apply(new[] { Charging("dev", 50, _t0.AddDays(1)) })[0];
        Assert.NotNull(r.ChargeEtaHours);
        Assert.InRange(r.ChargeEtaHours!.Value, 1.2, 3.5); // 50 points restants à ~25 points/h
    }

    [Fact]
    public void Charge_eta_text_follows_the_interface_language()
    {
        string saved = Loc.Current;
        try
        {
            var r = Charging("dev", 40, _t0) with { ChargeEtaHours = 1.1 };
            Loc.Set("fr");
            Assert.Equal("plein dans ≈ 1 h 6 min", Loc.ChargeEtaText(r));
            Loc.Set("en");
            Assert.Equal("full in ≈ 1 h 6 min", Loc.ChargeEtaText(r));

            Assert.Null(Loc.ChargeEtaText(r with { Charge = null }));           // pas en charge
            Assert.Null(Loc.ChargeEtaText(r with { Percent = 100 }));           // plein
            Assert.Null(Loc.ChargeEtaText(r with { ChargeEtaHours = null }));   // pas encore de vitesse
        }
        finally { Loc.Set(saved); }
    }

    [Fact]
    public void The_chart_history_is_the_current_discharge_session_only()
    {
        var e = New();
        for (int m = 0; m <= 20; m += 2)
            e.Apply(new[] { new BatteryReading("dev", "Test", DeviceKind.Headset, 80 - m / 10, null, null, _t0.AddMinutes(m)) });

        var samples = e.SamplesFor("dev");
        Assert.True(samples.Count >= 10);
        Assert.True(samples[0].T < samples[^1].T);

        e.Apply(new[] { Charging("dev", 78, _t0.AddMinutes(30)) });          // la charge repart de zéro
        Assert.True(e.SamplesFor("dev").Count <= 1);
        Assert.Empty(e.SamplesFor("inconnu"));
    }
}
