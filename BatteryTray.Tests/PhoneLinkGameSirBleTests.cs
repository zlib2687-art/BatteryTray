using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class PhoneLinkDriverTests
{
    static string Card(string speak, string tooltip, string text) =>
        "{\"type\":\"AdaptiveCard\",\"speak\":\"" + speak + "\",\"body\":[{\"type\":\"Container\",\"items\":[{\"type\":\"TextBlock\",\"tooltip\":\"" + tooltip + "\",\"text\":\"" + text + "\"}]}]}";

    [Fact]
    public void EnglishAndJapaneseCardsAreRead()
    {
        var en = PhoneLinkDriver.Parse(Card("Pixel 8 - Connected to your PC", "Battery 82% remaining, charging", "82%"))!;
        Assert.Equal(("Pixel 8", 82, true), (en.Name, en.Percent, en.Charging));
        Assert.False(PhoneLinkDriver.Parse(Card("Pixel 8 - Connected to your PC", "Battery 40% remaining", "40%"))!.Charging);
        var ja = PhoneLinkDriver.Parse(Card("Xperia - PC に接続しました", "バッテリー 残量 55%", "55%"))!;
        Assert.Equal(("Xperia", 55, false), (ja.Name, ja.Percent, ja.Charging));
    }

    [Fact]
    public void MalformedOrAmbiguousCardsAreRejected()
    {
        Assert.Null(PhoneLinkDriver.Parse(Card("Pixel 8", "Battery 82% remaining", "82%")));                              // pas connecté
        Assert.Null(PhoneLinkDriver.Parse(Card("Pixel - Connected to your PC", "Battery 82% remaining", "50%")));         // texte ≠ infobulle
        Assert.Null(PhoneLinkDriver.Parse(Card("Pixel - Connected to your PC", "Battery 101% remaining", "101%")));
        Assert.Null(PhoneLinkDriver.Parse(Card("Pixel - Connected to your PC", "Battery 82% left", "82%")));
        Assert.Null(PhoneLinkDriver.Parse("{\"speak\":\"A - Connected to your PC\",\"speak\":\"B - Connected to your PC\"}")); // clé en double
        Assert.Null(PhoneLinkDriver.Parse("not json")); Assert.Null(PhoneLinkDriver.Parse("[]"));
        var two = "{\"speak\":\"P - Connected to your PC\",\"body\":[{\"tooltip\":\"Battery 80% remaining\"},{\"tooltip\":\"Battery 70% remaining\"}]}";
        Assert.Null(PhoneLinkDriver.Parse(two));                                                                              // deux niveaux différents : ambigu
        var other = "{\"speak\":\"P - Connected to your PC\",\"actions\":[{\"tooltip\":\"Battery 80% remaining\"}]}"; Assert.Null(PhoneLinkDriver.Parse(other)); // champs hors disposition ignorés
    }

    [Fact]
    public void TooltipAndNameHelpers()
    {
        Assert.Equal(60, PhoneLinkDriver.BatteryFromTooltip("Battery 60% remaining", out bool c)); Assert.False(c);
        Assert.Equal(-1, PhoneLinkDriver.BatteryFromTooltip("Battery %", out _)); Assert.Equal(-1, PhoneLinkDriver.BatteryFromTooltip("Charge 60%", out _));
        Assert.Equal("Moto", PhoneLinkDriver.ConnectedPhoneName("Moto - Connected to your PC")); Assert.Null(PhoneLinkDriver.ConnectedPhoneName(" - Connected to your PC"));
    }

    [Fact]
    public void DeeplyNestedInputIsRejected()
    {
        Assert.Null(PhoneLinkDriver.Parse("{\"speak\":\"P - Connected to your PC\",\"body\":" + new string('[', 5000) + new string(']', 5000) + "}"));
        var rnd = new Random(241); for (int i = 0; i < 3000; i++) PhoneLinkDriver.Parse(new string(Enumerable.Range(0, rnd.Next(0, 60)).Select(_ => "{}[]\":,a0 "[rnd.Next(10)]).ToArray()));
    }
}

public class GameSirBleDriverTests
{
    [Fact]
    public void BaseInfoPacketIsParsed()
    {
        var p = new byte[19]; p[18] = 64;
        Assert.Equal(new GameSirBleDriver.Reading(64, false), GameSirBleDriver.Parse(p));
        p[18] = 0xFF; Assert.Equal(new GameSirBleDriver.Reading(-1, true), GameSirBleDriver.Parse(p));
        p[18] = 101; Assert.Null(GameSirBleDriver.Parse(p));
        var prefixed = new byte[20]; prefixed[0] = 0xC9; prefixed[19] = 33; Assert.Equal(33, GameSirBleDriver.Parse(prefixed)!.Percent);
        prefixed[0] = 0x00; Assert.Null(GameSirBleDriver.Parse(prefixed)); Assert.Null(GameSirBleDriver.Parse(new byte[18]));
    }
}
