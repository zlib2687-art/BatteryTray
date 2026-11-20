using Xunit;

namespace BatteryTray.Tests;

public class NotifierTests : IDisposable
{
    sealed class FakeSink : IToastSink
    {
        public readonly List<(string Id, string Title, string Text, bool Warning)> Sent = new();
        public void Notify(string deviceId, string title, string text, bool warning) => Sent.Add((deviceId, title, text, warning));
    }

    readonly string _savedDir = AppSettings.DataDir;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-notif-" + Guid.NewGuid().ToString("N"));
    readonly string _savedLang = Loc.Current;

    public NotifierTests()
    {
        AppSettings.DataDir = _dir; // les tests n'écrivent jamais dans les vrais réglages
        Loc.Set("fr");
    }

    public void Dispose()
    {
        AppSettings.DataDir = _savedDir;
        Loc.Set(_savedLang);
        try { Directory.Delete(_dir, true); } catch { }
    }

    static BatteryReading R(int percent, string? charge = null, string id = "dev") =>
        new(id, "Casque", DeviceKind.Headset, percent, charge, null, DateTime.Now);

    (Notifier, FakeSink, AppSettings) Make(AppSettings? s = null)
    {
        s ??= new AppSettings();
        var sink = new FakeSink();
        return (new Notifier(s, sink), sink, s);
    }

    [Fact]
    public void Low_battery_is_notified_once_per_crossing()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(25) });
        Assert.Empty(sink.Sent);

        n.Evaluate(new[] { R(20) });  // seuil par défaut : 20 %
        n.Evaluate(new[] { R(19) });
        n.Evaluate(new[] { R(18) });
        Assert.Single(sink.Sent);
        Assert.True(sink.Sent[0].Warning);
        Assert.Contains("Casque", sink.Sent[0].Text);
        Assert.Contains("20", sink.Sent[0].Text);
    }

    [Fact]
    public void Low_battery_rearms_after_hysteresis_and_notifies_again()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(15) });
        n.Evaluate(new[] { R(22) });  // au-dessus du seuil mais pas assez (seuil + 5 = 25) : pas de réarmement
        n.Evaluate(new[] { R(18) });
        Assert.Single(sink.Sent);

        n.Evaluate(new[] { R(30) });  // réarmé
        n.Evaluate(new[] { R(19) });
        Assert.Equal(2, sink.Sent.Count);
    }

    [Fact]
    public void Charging_device_does_not_trigger_low_battery_and_rearms()
    {
        var (n, sink, s) = Make();
        n.Evaluate(new[] { R(10, ChargeState.Charging) });
        Assert.Empty(sink.Sent);

        n.Evaluate(new[] { R(10) });
        Assert.Single(sink.Sent);

        n.Evaluate(new[] { R(12, ChargeState.Charging) });
        Assert.DoesNotContain("dev", s.LowNotified); // réarmé par la charge
    }

    [Fact]
    public void Charge_complete_is_notified_once_when_plugged_device_reaches_100()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(80, ChargeState.Charging) });
        Assert.Empty(sink.Sent);

        n.Evaluate(new[] { R(100, ChargeState.Charging) });
        n.Evaluate(new[] { R(100, ChargeState.Charging) });
        Assert.Single(sink.Sent);
        Assert.False(sink.Sent[0].Warning);
    }

    [Fact]
    public void A_full_device_that_is_not_plugged_does_not_notify()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(100) });
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void Charge_complete_rearms_after_unplugging()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(100, ChargeState.Charging) });
        n.Evaluate(new[] { R(98) });                       // débranché
        n.Evaluate(new[] { R(100, ChargeState.Charging) }); // rebranché et plein
        Assert.Equal(2, sink.Sent.Count);
    }

    [Fact]
    public void Disabled_notifications_send_nothing_but_still_record_state()
    {
        var s = new AppSettings { NotifyLow = false, NotifyFull = false };
        var (n, sink, _) = Make(s);

        n.Evaluate(new[] { R(10) });
        n.Evaluate(new[] { R(100, ChargeState.Charging, "other") });

        Assert.Empty(sink.Sent);
        Assert.Contains("dev", s.LowNotified);   // pas de rafale si on réactive plus tard
        Assert.Contains("other", s.FullNotified);
    }

    [Fact]
    public void Per_device_threshold_overrides_the_default()
    {
        var s = new AppSettings();
        s.Devices["dev"] = new DevicePref { LowThreshold = 50 };
        var (n, sink, _) = Make(s);

        n.Evaluate(new[] { R(45) });
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void Devices_without_a_percentage_are_ignored()
    {
        var (n, sink, _) = Make();
        n.Evaluate(new[] { new BatteryReading("g502", "G502", DeviceKind.Mouse, null, null, "4209 mV", DateTime.Now) });
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void Notified_state_is_persisted_so_a_restart_does_not_repeat_the_toast()
    {
        var (n, sink, s) = Make();
        n.Evaluate(new[] { R(10) });
        Assert.Single(sink.Sent);

        var reloaded = AppSettings.Load();
        var sink2 = new FakeSink();
        new Notifier(reloaded, sink2).Evaluate(new[] { R(10) });
        Assert.Empty(sink2.Sent);
    }

    [Fact]
    public void Notification_text_follows_the_interface_language()
    {
        Loc.Set("en");
        var (n, sink, _) = Make();
        n.Evaluate(new[] { R(10) });
        Assert.Equal("Low battery", sink.Sent[0].Title);
    }
}
