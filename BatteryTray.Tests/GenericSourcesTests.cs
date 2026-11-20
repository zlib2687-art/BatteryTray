using BatteryTray.Providers;
using Xunit;

namespace BatteryTray.Tests;

public class ReadingMergeTests
{
    static BatteryReading R(string id, string name, int p = 80) =>
        new(id, name, DeviceKind.Headset, p, null, null, DateTime.Now);

    [Fact]
    public void The_same_device_seen_by_two_sources_is_kept_once_from_the_first_source()
    {
        var merged = ReadingMerge.Deduplicate(new[]
        {
            R("ble-1111", "Sony WH-1000XM5", 80),   // source prioritaire
            R("pnp-2222", "Sony WH-1000XM5", 79),   // même appareil vu par le balayage Windows
        });
        var only = Assert.Single(merged);
        Assert.Equal("ble-1111", only.Id);
        Assert.Equal(80, only.Percent);
    }

    [Fact]
    public void Names_are_compared_ignoring_case_spaces_and_punctuation()
    {
        Assert.Equal("logimxkeys", ReadingMerge.Normalize("Logi MX-Keys"));
        Assert.Single(ReadingMerge.Deduplicate(new[] { R("bt-1", "Logi MX-Keys"), R("pnp-2", "logi mx keys") }));
    }

    [Fact]
    public void A_short_name_contained_in_a_long_one_is_the_same_device()
    {
        var merged = ReadingMerge.Deduplicate(new[]
        {
            R("logi-C539-1", "G502 LIGHTSPEED"),
            R("pnp-9", "G502 LIGHTSPEED Wireless Gaming Mouse"),
        });
        Assert.Equal("logi-C539-1", Assert.Single(merged).Id);
    }

    [Fact]
    public void Different_devices_are_never_merged()
    {
        var merged = ReadingMerge.Deduplicate(new[] { R("ble-1", "Casque A"), R("pnp-2", "Casque B"), R("win-3", "Clavier") });
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Two_identical_devices_from_the_same_source_stay_separate()
    {
        // deux souris Bluetooth du même modèle, lues par la même source
        var merged = ReadingMerge.Deduplicate(new[] { R("ble-1", "MX Master 3", 70), R("ble-2", "MX Master 3", 40) });
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Very_short_names_do_not_swallow_other_devices()
    {
        var merged = ReadingMerge.Deduplicate(new[] { R("ble-1", "Logitech Pebble Mouse"), R("pnp-2", "Mouse") });
        Assert.Equal(2, merged.Count);   // « mouse » (5 lettres) est trop court pour fusionner
    }

    [Theory]
    [InlineData("logi-C539-1", "logi")]
    [InlineData("prox2", "prox2")]
    [InlineData("razer-00BF", "razer")]
    [InlineData("pnp-ABCD1234", "pnp")]
    public void The_source_is_the_start_of_the_identifier(string id, string expected) =>
        Assert.Equal(expected, ReadingMerge.SourceOf(id));
}

public class GenericSourceMathTests
{
    [Theory]
    [InlineData(50, 100, 50)]
    [InlineData(3500, 5000, 70)]
    [InlineData(120, 100, 100)]     // borné à 100
    [InlineData(0, 100, 0)]
    public void Percent_is_remaining_over_full_charge(int remaining, int full, int expected) =>
        Assert.Equal(expected, BatteryMath.Percent(remaining, full));

    [Fact]
    public void Missing_or_invalid_capacities_give_no_percentage()
    {
        Assert.Null(BatteryMath.Percent(null, 100));
        Assert.Null(BatteryMath.Percent(50, null));
        Assert.Null(BatteryMath.Percent(50, 0));
        Assert.Null(BatteryMath.Percent(-1, 100));
    }

    [Theory]
    [InlineData(0b0011_0000, "charging")]   // bits 4-5 = 3 : en charge
    [InlineData(0b1011_1100, "charging")]   // les autres bits n'y changent rien
    [InlineData(0b0010_0000, null)]         // 2 : ne charge pas
    [InlineData(0b0001_0000, null)]         // 1 : non rechargeable
    [InlineData(0b0000_0000, null)]         // 0 : inconnu
    public void Bluetooth_battery_power_state_gives_the_charging_status(int powerState, string? expected) =>
        Assert.Equal(expected, BatteryParts.ChargeFromPowerState((byte)powerState));

    [Theory]
    [InlineData("Logitech MX Master 3", DeviceKind.Mouse)]
    [InlineData("Souris sans fil", DeviceKind.Mouse)]
    [InlineData("Беспроводная мышь", DeviceKind.Mouse)]
    [InlineData("Teclado Bluetooth", DeviceKind.Keyboard)]
    [InlineData("Auriculares Bluetooth", DeviceKind.Headset)]
    [InlineData("Fone de ouvido", DeviceKind.Headset)]
    [InlineData("Геймпад беспроводной", DeviceKind.Gamepad)]
    [InlineData("蓝牙键盘", DeviceKind.Keyboard)]
    [InlineData("无线鼠标", DeviceKind.Mouse)]
    [InlineData("", DeviceKind.Other)]
    [InlineData(null, DeviceKind.Other)]
    public void Device_kind_is_guessed_in_several_languages(string? name, DeviceKind expected) =>
        Assert.Equal(expected, DeviceNames.Guess(name));
}
