using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class MoondropDriverTests
{
    [Fact]
    public void RepliesAndPathsAreRecognised()
    {
        Assert.Equal(new byte[] { 0x4B, 0x80, 0x27, 0 }, MoondropDriver.BuildRead(0x27));
        var b = new byte[64]; b[0] = 0x4B; b[1] = 0x80; b[2] = 0x27; b[3] = 6; b[8] = 1; b[9] = 50;
        Assert.True(MoondropDriver.IsReply(b, 0x27)); Assert.Equal(50, MoondropDriver.ParseBattery(b));
        b[8] = 0; Assert.Null(MoondropDriver.ParseBattery(b)); b[8] = 1; b[9] = 101; Assert.Null(MoondropDriver.ParseBattery(b));
        var link = new byte[] { 0x4B, 0x80, 0x31, 1, 0 }; Assert.True(MoondropDriver.IsUnlinked(link)); link[4] = 1; Assert.False(MoondropDriver.IsUnlinked(link));
        Assert.True(MoondropDriver.IsSingleCollection(@"\\?\hid#vid_35d8&pid_0133&mi_03#7&abc#{g}"));
        Assert.False(MoondropDriver.IsSingleCollection(@"\\?\hid#vid_35d8&pid_0133&mi_03&col02#7&abc#{g}"));
    }
}

public class NiceboyDriverTests
{
    [Fact]
    public void DocumentedExampleIs90Percent()
    {
        var r = Convert.FromHexString("0549FA48EA0300");
        Assert.True(NiceboyDriver.IsBatteryReply(r));
        var p = NiceboyDriver.Parse(r)!.Value;
        Assert.Equal(4133, p.Millivolts); Assert.Equal(90, p.Percent); Assert.False(p.Charging);
        Assert.Null(NiceboyDriver.Parse(Convert.FromHexString("0549FA48000000")));         // non liée
        Assert.Equal(10, NiceboyDriver.PercentFromMillivolts(3000)); Assert.Equal(100, NiceboyDriver.PercentFromMillivolts(4300)); Assert.Equal(20, NiceboyDriver.PercentFromMillivolts(3200));
        Assert.False(NiceboyDriver.IsBatteryReply(Convert.FromHexString("0549FA49EA0300")));
    }
}

public class NinjutsoDriverTests
{
    [Fact]
    public void TenAndSoraRepliesAreParsed()
    {
        var q = NinjutsoDriver.BuildTenQuery(0x12); Assert.Equal(16, q.Length); Assert.Equal(0x12, q[1]);
        var r = new byte[16]; r[0] = 6; r[1] = 0x12; r[8] = 70; Assert.Equal((byte)70, NinjutsoDriver.ParseTen(r, 0x12)); Assert.Null(NinjutsoDriver.ParseTen(r, 0x13)); Assert.Null(NinjutsoDriver.ParseTen(new byte[15], 0x12));
        var s = new byte[32]; s[0] = 5; s[1] = 0x15; s[9] = 0; s[10] = 1; s[12] = 1;
        Assert.Equal((true, 100, true), NinjutsoDriver.ParseSora(s, false)!.Value);          // 0 = 100 %
        s[12] = 0; Assert.Equal((false, -1, false), NinjutsoDriver.ParseSora(s, false)!.Value); // hors ligne par récepteur
        Assert.Equal((true, 100, true), NinjutsoDriver.ParseSora(s, true)!.Value);           // câble : connectée
        s[9] = 101; Assert.Null(NinjutsoDriver.ParseSora(s, true)); Assert.Null(NinjutsoDriver.ParseSora(new byte[8], false));
        Assert.Equal(32, NinjutsoDriver.BuildSoraQuery().Length);
    }
}

public class SprimeDriverTests
{
    [Fact]
    public void FullBatteryIsNotCharging()
    {
        var r = new byte[32]; r[0] = 5; r[1] = 0x15; r[9] = 88; r[10] = 1; r[11] = 1; r[12] = 1;
        Assert.Equal((true, 100, false), SprimeDriver.Parse(r, false)!.Value);
        r[11] = 0; Assert.Equal((true, 88, true), SprimeDriver.Parse(r, false)!.Value);
        r[12] = 0; Assert.Equal((false, -1, false), SprimeDriver.Parse(r, false)!.Value); Assert.True(SprimeDriver.Parse(r, true)!.Value.Connected);
        r[9] = 101; Assert.Null(SprimeDriver.Parse(r, true));
        Assert.True(SprimeDriver.IsCable(0xAC12)); Assert.True(SprimeDriver.IsReceiver(0xAD8C)); Assert.False(SprimeDriver.IsCable(0xAC0B)); Assert.False(SprimeDriver.IsReceiver(0xAC0B));
    }
}
