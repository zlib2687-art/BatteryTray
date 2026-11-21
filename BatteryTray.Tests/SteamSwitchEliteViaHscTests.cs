using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class SteamSwitchDriversTests
{
    [Fact]
    public void SteamReportsAreDecoded()
    {
        var legacy = new byte[64]; legacy[0] = 1; legacy[2] = 4; legacy[3] = 11; legacy[14] = 80;
        Assert.Equal((80, false), SteamController.Parse(legacy, true)!.Value); legacy[14] = 101; Assert.Null(SteamController.Parse(legacy, true));
        var nw = new byte[64]; nw[0] = 0x43; nw[1] = 2; nw[2] = 55; Assert.Equal((55, true), SteamController.Parse(nw, false)!.Value); nw[0] = 0x44; Assert.Null(SteamController.Parse(nw, false));
        Assert.Equal("Wireless adapter", SteamController.Transport(0x1304)); Assert.Null(SteamController.Transport(0x0001)); Assert.Null(SteamController.Parse(new byte[10], false));
    }

    [Fact]
    public void SwitchProLevelIsInQuartersAndNotChargingWhenFull()
    {
        var r = new byte[64]; r[0] = 0x30; r[2] = (byte)(3 << 5 | 0x10);
        Assert.Equal((75, true), SwitchProDriver.Parse(r)!.Value);
        r[2] = (byte)(4 << 5 | 0x10); Assert.Equal((100, false), SwitchProDriver.Parse(r)!.Value);
        r[2] = (byte)(5 << 5); Assert.Null(SwitchProDriver.Parse(r)); r[0] = 0x3F; r[2] = 0; Assert.Null(SwitchProDriver.Parse(r)); Assert.Null(SwitchProDriver.Parse(new byte[5]));
    }
}

public class SteelSeriesEliteDriverTests
{
    [Fact]
    public void DirectReplyAndPushedFramesAreAbsorbed()
    {
        var st = new SteelSeriesEliteDriver.State();
        var direct = new byte[64]; direct[0] = 1; direct[1] = 0xB0; direct[6] = 70; direct[14] = 2; direct[15] = 2;
        SteelSeriesEliteDriver.Absorb(direct, st); Assert.Equal((70, 2, true), (st.Level, st.Power, st.Charging));
        SteelSeriesEliteDriver.Absorb(new byte[] { 7, 0xB7, 55, 0, 0 }, st); Assert.Equal((55, false), (st.Level, st.Charging));
        SteelSeriesEliteDriver.Absorb(new byte[] { 7, 0xB5, 0, 0, 1 }, st); Assert.Equal(1, st.Power);
        SteelSeriesEliteDriver.Absorb(new byte[] { 7, 0xB7, 101, 0, 0 }, st); Assert.Equal(55, st.Level);
        Assert.Equal(0xB0, SteelSeriesEliteDriver.BuildRequest()[1]);
    }
}

public class ViaDongleDriverTests
{
    [Fact]
    public void PmoAndViaFramesAreHandled()
    {
        var f = ViaDongleDriver.PmoFrame(32, 0x10, 0x18); Assert.Equal(33, f.Length); Assert.Equal(0xAA, f[1]); Assert.Equal(0x18, f[5]);
        var bat = new byte[33]; bat[7] = 0x55; bat[8] = 78; Assert.Equal(78, ViaDongleDriver.PmoLevel(bat)); bat[7] = 0x0F; Assert.Equal(-1, ViaDongleDriver.PmoLevel(bat));
        var start = new byte[33]; start[7] = 0x55; start[8] = 0; start[9] = 0xB0; start[10] = 0x36; start[11] = 0x09; start[12] = 0x30;
        Assert.Equal(0x3009, ViaDongleDriver.PairedPid(start)); start[10] = 0; Assert.Equal(0, ViaDongleDriver.PairedPid(start));
        Assert.Equal(new byte[] { 0, 8, 0, 7 }, ViaDongleDriver.ViaRequest(33)[..4]);
        Assert.Equal(72, ViaDongleDriver.ViaLevel(new byte[] { 8, 0, 7, 72 })); Assert.Equal(-1, ViaDongleDriver.ViaLevel(new byte[] { 8, 0, 7, 0 })); Assert.Equal(-1, ViaDongleDriver.ViaLevel(new byte[3]));
        Assert.Equal("EVO80", ViaDongleDriver.ViaName("EVO80 2.4G")); Assert.Equal("URX Core75", ViaDongleDriver.ViaName("URX Core75 Wireless")); Assert.Equal("Wireless keyboard", ViaDongleDriver.ViaName("Wireless 2.4G Dongle"));
        Assert.Equal("WAVE 75", ViaDongleDriver.PmoName(0x3009)); Assert.Null(ViaDongleDriver.PmoName(1));
        Assert.True(ViaDongleDriver.IsRawHid(new HidColl { Iface = 1, Page = 0xFF60, Usage = 0x61, InLen = 33, OutLen = 33 })); Assert.False(ViaDongleDriver.IsRawHid(new HidColl { Iface = 1, Page = 0xFF60, Usage = 0x61, InLen = 33, OutLen = 65 }));
    }
}

public class HeadsetControlDriverTests
{
    [Fact]
    public void JsonOutputIsParsedAndNoiseIsIgnored()
    {
        var json = "warning: x\n{\"devices\":[{\"device\":\"Arctis 7\",\"id_vendor\":\"0x1038\",\"id_product\":\"0x12ad\",\"battery\":{\"status\":\"BATTERY_CHARGING\",\"level\":64}}," +
                   "{\"device\":\"Off\",\"id_vendor\":\"0x1038\",\"id_product\":\"0x1\",\"battery\":{\"status\":\"BATTERY_UNAVAILABLE\",\"level\":-1}},{\"no\":1}]}";
        var l = HeadsetControlDriver.Parse(json);
        Assert.Equal(2, l.Count);
        Assert.Equal((0x1038, 0x12AD, "Arctis 7", 64, true, true), (l[0].Vid, l[0].Pid, l[0].Status.Name, l[0].Status.Percent, l[0].Status.Charging, l[0].Status.Connected));
        Assert.False(l[1].Status.Connected); Assert.Equal(-1, l[1].Status.Percent);
        Assert.Empty(HeadsetControlDriver.Parse("not json")); Assert.Empty(HeadsetControlDriver.Parse("{\"devices\":")); Assert.Empty(HeadsetControlDriver.Parse("{\"devices\":5}"));
    }

    [Fact]
    public void DeeplyNestedOrHugeInputDoesNotCrash()
    {
        Assert.Empty(HeadsetControlDriver.Parse("{\"a\":" + new string('[', 5000) + new string(']', 5000) + "}"));
        var rnd = new Random(211); for (int i = 0; i < 2000; i++) { var s = new string(Enumerable.Range(0, rnd.Next(0, 80)).Select(_ => "{}[]\":,0a "[rnd.Next(10)]).ToArray()); HeadsetControlDriver.Parse(s); }
    }
}
