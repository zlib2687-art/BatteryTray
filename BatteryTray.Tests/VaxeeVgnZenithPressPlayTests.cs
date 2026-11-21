using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class VaxeeDriverTests
{
    [Fact]
    public void ReadOnlyQueriesAndRepliesAreHandled()
    {
        var q = VaxeeDriver.BuildQuery(0x0B); Assert.Equal(64, q.Length); Assert.Equal(1, q[3]); // 01 = lecture, jamais 02
        var r = new byte[64]; r[0] = 0x0E; r[1] = 0xA5; r[2] = 0x0B; r[4] = 1; r[5] = 15;
        Assert.Equal((byte)15, VaxeeDriver.ParseReply(r, 0x0B)); Assert.Null(VaxeeDriver.ParseReply(r, 0x10));
        r[4] = 0; Assert.Null(VaxeeDriver.ParseReply(r, 0x0B)); Assert.Null(VaxeeDriver.ParseReply(new byte[4], 0x0B));
        Assert.True(VaxeeDriver.Supported(0x2002)); Assert.False(VaxeeDriver.Supported(0x1234));
        Assert.Equal("ZYGEN NP-01S V2 Wireless", VaxeeDriver.ModelName(0x1011)); Assert.Equal("VAXEE 8K Wireless", VaxeeDriver.ModelName(0x2002));
    }
}

public class VgnDriverTests
{
    [Fact]
    public void NumberedAndUnnumberedRepliesAreRead()
    {
        Assert.Equal(new byte[] { 0x55, 0x65, 1 }, VgnDriver.BuildRequest());
        Assert.Equal(80, VgnDriver.ParseBattery(new byte[] { 0x55, 0x65, 80, 0 }));
        Assert.Equal(45, VgnDriver.ParseBattery(new byte[] { 0x65, 45 }));
        Assert.Null(VgnDriver.ParseBattery(new byte[] { 0x55, 0x65, 101 })); Assert.Null(VgnDriver.ParseBattery(new byte[] { 0x55, 0x66, 50 })); Assert.Null(VgnDriver.ParseBattery(new byte[1]));
    }
}

public class ZenithDriverTests
{
    [Fact]
    public void ChecksummedReplyIsDecoded()
    {
        Assert.Equal(0x55, ZenithDriver.BuildRequest().Sum(b => b) & 0xFF);
        var r = new byte[17]; r[0] = 8; r[1] = 4; r[6] = 90; r[7] = 1; r[8] = 0x0F; r[9] = 0xA0;
        int s = 0; for (int i = 0; i < 16; i++) s += r[i]; r[16] = (byte)(0x55 - s);
        Assert.Equal((90, true, 4000), ZenithDriver.ParseReply(r)!.Value);
        r[16] ^= 1; Assert.Null(ZenithDriver.ParseReply(r)); Assert.Null(ZenithDriver.ParseReply(new byte[16]));
    }
}

public class PressPlayDriverTests
{
    [Fact]
    public void ReplyCarriesThePercentAtOffset13()
    {
        Assert.Equal(64, PressPlayDriver.BuildRequest(64).Length);
        var r = new byte[64]; r[0] = 6; r[1] = 5; r[2] = 0x26; r[13] = 0x5A;
        Assert.Equal(90, PressPlayDriver.ParseReply(r)); r[13] = 101; Assert.Null(PressPlayDriver.ParseReply(r)); r[13] = 50; r[2] = 0x27; Assert.Null(PressPlayDriver.ParseReply(r));
        Assert.Null(PressPlayDriver.ParseReply(new byte[10]));
    }
}
