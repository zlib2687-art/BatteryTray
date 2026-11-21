using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class RedragonEvisionDriverTests
{
    [Fact]
    public void M999FrameDecodes97PercentAndEchoesTheSequence()
    {
        var q = RedragonEvisionDriver.BuildRequest(0x0102); Assert.Equal(new byte[] { 4, 2, 1, 0x1A, 2 }, q[..5]);
        var r = new byte[32]; r[0] = 4; r[1] = 2; r[2] = 1; r[3] = 0x1A; r[8] = 0x61; r[9] = 0;
        Assert.Equal((97, false), RedragonEvisionDriver.ParseReply(r, 0x0102)!.Value);
        r[9] = 2; Assert.True(RedragonEvisionDriver.ParseReply(r, 0x0102)!.Value.Charging);
        r[9] = 3; Assert.Equal(-1, RedragonEvisionDriver.ParseReply(r, 0x0102)!.Value.Percent);
        r[9] = 0; Assert.Null(RedragonEvisionDriver.ParseReply(r, 0x0103)); r[5] = 1; Assert.Null(RedragonEvisionDriver.ParseReply(r, 0x0102));
    }
}

public class AtkKeyboardDriverTests
{
    [Fact]
    public void ModelsNamesAndRepliesAreHandled()
    {
        Assert.True(AtkKeyboardDriver.FindModel("VGN S99 2.4G Dongle")!.Vgn); Assert.False(AtkKeyboardDriver.FindModel("atk v75x ")!.Vgn); Assert.Null(AtkKeyboardDriver.FindModel("Other"));
        Assert.Equal("VGN S99", AtkKeyboardDriver.KeyboardName("VGN S99 2.4G Dongle")); Assert.Equal("ATK V75X", AtkKeyboardDriver.KeyboardName("ATK V75X"));
        Assert.Equal(6, AtkKeyboardDriver.BuildRequest(1, true)[4]); Assert.Equal(0, AtkKeyboardDriver.BuildRequest(1, false)[4]);
        var r = new byte[64]; r[0] = 4; r[1] = 7; r[3] = 0x1A; r[8] = 93; r[9] = 1;
        Assert.Equal((93, true), AtkKeyboardDriver.ParseReply(r, 7)!.Value); Assert.Null(AtkKeyboardDriver.ParseReply(r, 8));
        Assert.Equal((-2, false), AtkKeyboardDriver.ParseZ87((byte[])new byte[] { 4, 7, 0, 0xFF, 0, 0, 0, 0, 0, 0 }, 7, 0x373B)!.Value);   // clavier endormi
        Assert.Null(AtkKeyboardDriver.ParseZ87(new byte[] { 4, 9, 0, 0xFF }, 7, 0x373B));
    }
}

public class AtkBitmouseDriverTests
{
    [Fact]
    public void QueriesAndRepliesFollowTheDocumentedFraming()
    {
        var q = AtkBitmouseDriver.BuildQuery(0x07);
        int sum = 0; for (int i = 2; i < 64; i++) sum += q[i]; Assert.Equal((byte)(sum & 0xFF), q[1]);
        var z = AtkBitmouseDriver.BuildQuery(0x07, AtkBitmouseDriver.Link.ZeroCable); Assert.Equal(0x7D, z[1]); Assert.Equal(0, z[5]);
        Assert.Equal(1, AtkBitmouseDriver.BuildQuery(0x07, AtkBitmouseDriver.Link.ZeroReceiver)[5]);
        var r = new byte[64]; r[0] = 8; r[1] = 0x72; r[2] = 0; r[5] = 7; r[6] = 1; r[7] = 77;
        Assert.Equal(77, AtkBitmouseDriver.ParseReply(r, 7, AtkBitmouseDriver.Link.NearLink)); Assert.Equal(77, AtkBitmouseDriver.ParseReply(r, 7, AtkBitmouseDriver.Link.ZeroCable));
        r[2] = 0xFF; Assert.Equal(-1, AtkBitmouseDriver.ParseReply(r, 7, AtkBitmouseDriver.Link.NearLink)); Assert.Equal(-1, AtkBitmouseDriver.ParseReply(r, 7, AtkBitmouseDriver.Link.ZeroCable));
        Assert.Null(AtkBitmouseDriver.ParseReply(r, 8, AtkBitmouseDriver.Link.NearLink));
    }

    [Fact]
    public void Nano8kConfigReplyIsDecoded()
    {
        var r = new byte[11]; r[0] = 0x82; r[8] = 0x80 | 55; r[9] = 1;
        Assert.Equal((55, true), AtkBitmouseDriver.ParseNano8k(r)!.Value);
        r[9] = 0; Assert.Null(AtkBitmouseDriver.ParseNano8k(r)); r[9] = 1; r[8] = 0; Assert.Null(AtkBitmouseDriver.ParseNano8k(r)); r[0] = 0x12; r[8] = 50; Assert.Null(AtkBitmouseDriver.ParseNano8k(r));
    }
}

public class AtkSerialDriverTests
{
    [Fact]
    public void FramesAreExtractedAndSkippedWhenUnsolicited()
    {
        Assert.Equal(new byte[] { 2, 0, 4 }, AtkSerialDriver.BuildFrame(0, 4)[..3]);
        var pushed = new byte[64]; pushed[1] = 9; pushed[2] = 9;
        var reply = new byte[64]; reply[1] = 0; reply[2] = 3; reply[3 + 1] = 0x80 | 64;
        var buf = new List<byte>(pushed); buf.AddRange(reply);
        var data = AtkSerialDriver.FindReply(buf, 0, 3)!;
        Assert.Equal(61, data.Length); Assert.Equal((true, 64, true), AtkSerialDriver.Decode(new byte[] { 0, 1 }, data));
        Assert.Null(AtkSerialDriver.FindReply(new List<byte>(new byte[30]), 0, 3));
        Assert.Equal((false, -1, false), AtkSerialDriver.Decode(new byte[] { 0, 0 }, data));
    }
}
