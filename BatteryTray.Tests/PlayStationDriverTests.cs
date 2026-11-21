using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class PlayStationDriverTests
{
    static byte[] Report(int len, int id, int at, int value) { var r = new byte[len]; r[0] = (byte)id; r[at] = (byte)value; return r; }

    [Theory]
    [InlineData(0x05C4, 0x01, 30, 0x05, false, 55, false)]   // USB, sur batterie : niveau 5 → 55 %
    [InlineData(0x05C4, 0x01, 30, 0x15, false, 50, true)]    // câble branché : niveau 5 → 50 %, en charge
    [InlineData(0x05C4, 0x01, 30, 0x1B, false, 100, false)]  // câble, niveau 11 : plein
    [InlineData(0x05C4, 0x11, 32, 0x08, true, 85, false)]    // Bluetooth, rapport 11
    [InlineData(0x0CE6, 0x01, 53, 0x07, false, 75, false)]   // DualSense USB niveau 7
    [InlineData(0x0CE6, 0x31, 54, 0x15, true, 55, true)]     // DualSense Bluetooth, état 1 = en charge
    [InlineData(0x0DF2, 0x01, 53, 0x2A, false, 100, true)]   // état 2 = chargée
    public void LevelAndChargingAreRead(int pid, int id, int at, int value, bool bt, int percent, bool charging)
    {
        var (res, pct, chg) = PlayStationDriver.Parse((ushort)pid, Report(80, id, at, value), bt);
        Assert.Equal(PlayStationDriver.Outcome.Found, res);
        Assert.Equal(percent, pct); Assert.Equal(charging, chg);
    }

    [Fact]
    public void ReducedBluetoothReportsAndShortOrUnknownReportsAreSkipped()
    {
        Assert.Equal(PlayStationDriver.Outcome.NotThisReport, PlayStationDriver.Parse(0x05C4, Report(80, 0x01, 30, 5), true).Result);  // rapport réduit en BT
        Assert.Equal(PlayStationDriver.Outcome.NotThisReport, PlayStationDriver.Parse(0x0CE6, Report(80, 0x01, 53, 5), true).Result);
        Assert.Equal(PlayStationDriver.Outcome.NotThisReport, PlayStationDriver.Parse(0x05C4, Report(80, 0x07, 30, 5), false).Result);
        Assert.Equal(PlayStationDriver.Outcome.NotThisReport, PlayStationDriver.Parse(0x05C4, new byte[5], false).Result);
        Assert.Equal(PlayStationDriver.Outcome.NotThisReport, PlayStationDriver.Parse(0x05C4, Report(30, 0x01, 29, 5), false).Result); // trop court pour contenir l'octet de batterie
    }

    [Fact]
    public void WirelessAdapterWithoutControllerIsDetected() =>
        Assert.Equal(PlayStationDriver.Outcome.AdapterWithoutController, PlayStationDriver.Parse(0x0BA0, Report(80, 0x01, 30, 0), false).Result);

    [Fact]
    public void ModelsAreNamedAndHashesDistinguishTwoControllers()
    {
        Assert.Equal("DualShock 4", PlayStationDriver.Name(0x09CC)); Assert.Equal("DualSense", PlayStationDriver.Name(0x0CE6)); Assert.Equal("DualSense Edge", PlayStationDriver.Name(0x0DF2));
        Assert.NotEqual(PlayStationDriver.PathHash(@"\\?\hid#a"), PlayStationDriver.PathHash(@"\\?\hid#b"));
        Assert.Equal(PlayStationDriver.PathHash("x"), PlayStationDriver.PathHash("x"));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(54);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 100)]; rnd.NextBytes(r);
            if (r.Length > 0 && i % 2 == 0) r[0] = (byte)new[] { 0x01, 0x11, 0x31 }[rnd.Next(3)];
            var p = PlayStationDriver.Parse((ushort)new[] { 0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2, 0x1234 }[rnd.Next(6)], r, i % 3 == 0);
            if (p.Result == PlayStationDriver.Outcome.Found) Assert.InRange(p.Percent, 0, 100);
        }
    }
}
