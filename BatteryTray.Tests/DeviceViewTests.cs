using Xunit;

namespace BatteryTray.Tests;

public class DeviceViewTests
{
    static BatteryReading R(string id, string name, int? percent, DeviceKind kind = DeviceKind.Mouse) =>
        new(id, name, kind, percent, null, null, DateTime.Now);

    [Fact]
    public void New_devices_are_remembered_and_appended_to_the_order()
    {
        var s = new AppSettings();
        var view = DeviceView.Prepare(new[] { R("a", "Alpha", 50), R("b", "Beta", 60) }, s, out bool changed);

        Assert.True(changed);
        Assert.Equal(2, view.Count);
        Assert.Equal("Alpha", s.Known["a"].Name);
        Assert.Equal(new[] { "a", "b" }, s.Order);
    }

    [Fact]
    public void Device_without_percentage_is_hidden_by_default_but_stays_known()
    {
        var s = new AppSettings();
        var view = DeviceView.Prepare(new[] { R("g502", "G502", null), R("a", "Alpha", 50) }, s, out _);

        Assert.Single(view);
        Assert.Equal("a", view[0].Id);
        Assert.True(s.Devices["g502"].Hidden);
        Assert.True(s.Known.ContainsKey("g502"));
    }

    [Fact]
    public void User_can_unhide_a_device_without_percentage()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("g502", "G502", null) }, s, out _);
        s.Devices["g502"].Hidden = false;

        var view = DeviceView.Prepare(new[] { R("g502", "G502", null) }, s, out _);
        Assert.Single(view);
        Assert.Null(view[0].Percent); // jamais de pourcentage inventé
    }

    [Fact]
    public void Hidden_devices_are_not_shown()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", "Alpha", 50) }, s, out _);
        s.Devices["a"] = new DevicePref { Hidden = true };

        Assert.Empty(DeviceView.Prepare(new[] { R("a", "Alpha", 50) }, s, out _));
    }

    [Fact]
    public void Custom_name_replaces_the_original_but_the_original_is_kept()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", "Razer DeathAdder V4 Pro", 50) }, s, out _);
        s.Devices["a"] = new DevicePref { Name = "  Ma souris  " };

        var view = DeviceView.Prepare(new[] { R("a", "Razer DeathAdder V4 Pro", 50) }, s, out _);
        Assert.Equal("Ma souris", view[0].Name);
        Assert.Equal("Razer DeathAdder V4 Pro", s.Known["a"].Name);
    }

    [Fact]
    public void Order_chosen_by_the_user_is_respected()
    {
        var s = new AppSettings();
        var readings = new[] { R("a", "Alpha", 50), R("b", "Beta", 50), R("c", "Gamma", 50) };
        DeviceView.Prepare(readings, s, out _);
        s.Order = new List<string> { "c", "a", "b" };

        var ids = DeviceView.Prepare(readings, s, out _).Select(v => v.Id).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, ids);
    }

    [Fact]
    public void Rated_autonomy_is_used_only_while_no_measurement_exists()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", "Alpha", 50) }, s, out _);
        s.Devices["a"] = new DevicePref { RatedHours = 50 };

        var pending = R("a", "Alpha", 50) with { AutonomyPending = true };
        var v1 = DeviceView.Prepare(new[] { pending }, s, out _)[0];
        Assert.Equal(25, v1.AutonomyHours); // 50 % de 50 h
        Assert.True(v1.AutonomyFromRating);
        Assert.False(v1.AutonomyPending);

        var measured = R("a", "Alpha", 50) with { AutonomyHours = 7 };
        var v2 = DeviceView.Prepare(new[] { measured }, s, out _)[0];
        Assert.Equal(7, v2.AutonomyHours); // la mesure l'emporte
        Assert.False(v2.AutonomyFromRating);
    }

    [Fact]
    public void Known_model_gets_its_reference_autonomy_until_a_measurement_exists()
    {
        var s = new AppSettings();
        var pending = R("p", "Logitech PRO X 2", 73, DeviceKind.Headset) with { AutonomyPending = true };

        var v = DeviceView.Prepare(new[] { pending }, s, out _)[0];
        Assert.Equal(36.5, v.AutonomyHours!.Value, 6);        // 73 % de 50 h : la valeur que G HUB affichait (~37 h)
        Assert.True(v.AutonomyFromRating);

        var measured = pending with { AutonomyPending = false, AutonomyHours = 33 };
        Assert.Equal(33, DeviceView.Prepare(new[] { measured }, s, out _)[0].AutonomyHours); // la mesure l'emporte
    }

    [Fact]
    public void User_value_beats_the_built_in_reference_and_unknown_models_have_none()
    {
        var s = new AppSettings();
        var pending = R("p", "Logitech PRO X 2", 50, DeviceKind.Headset) with { AutonomyPending = true };
        DeviceView.Prepare(new[] { pending }, s, out _);
        s.Devices["p"] = new DevicePref { RatedHours = 40 };
        Assert.Equal(20, DeviceView.Prepare(new[] { pending }, s, out _)[0].AutonomyHours!.Value, 6);

        var unknown = R("u", "Souris inconnue", 50) with { AutonomyPending = true };
        var u = DeviceView.Prepare(new[] { unknown }, s, out _).Single(v => v.Id == "u"); // « p » est maintenant hors ligne
        Assert.Null(u.AutonomyHours);
        Assert.True(u.AutonomyPending);
        Assert.Null(DeviceDefaults.RatedHoursFor("Razer DeathAdder V4 Pro")); // pas de valeur inventée : 150 h dépend du taux de rapport
    }

    [Fact]
    public void Rated_autonomy_never_applies_without_a_percentage()
    {
        var s = new AppSettings();
        DeviceView.Prepare(new[] { R("a", "Alpha", null) }, s, out _);
        s.Devices["a"] = new DevicePref { RatedHours = 50, Hidden = false };

        var v = DeviceView.Prepare(new[] { R("a", "Alpha", null) with { AutonomyPending = true } }, s, out _)[0];
        Assert.Null(v.AutonomyHours);
    }

    [Fact]
    public void Nothing_changes_when_the_same_devices_are_seen_again()
    {
        var s = new AppSettings();
        var readings = new[] { R("a", "Alpha", 50) };
        DeviceView.Prepare(readings, s, out _);

        DeviceView.Prepare(readings, s, out bool changed);
        Assert.False(changed);
    }
}
