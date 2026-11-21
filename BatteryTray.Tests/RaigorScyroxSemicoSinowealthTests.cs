using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class RaigorDriverTests
{
    [Fact]
    public void DocumentedChunkAndHeartbeatAreRead()
    {
        var chunk = new byte[] { 0xB5, 0x05, 0x94, 0x04, 0x35, 0x09, 0x40, 0x38 };   // morceau 94 = 35 09 40 38 → 64 %
        Assert.Equal(64, RaigorDriver.ParseFrame(chunk));
        var beat = new byte[] { 0xB5, 0x67, 1, 2, 3, 4, 1, 80 }; Assert.Equal(80, RaigorDriver.ParseFrame(beat));
        chunk[6] = 101; Assert.Null(RaigorDriver.ParseFrame(chunk));
        Assert.Null(RaigorDriver.ParseFrame(new byte[] { 0xB5, 0x05, 0x90, 0x04, 0, 0, 50, 0 }));
        Assert.Null(RaigorDriver.ParseFrame(new byte[9])); Assert.Equal(8, RaigorDriver.BuildRequest().Length);
    }
}

public class ScyroxDriverTests
{
    static byte[] Frame(int pct, byte profile = 1)
    {
        var r = new byte[64]; r[0] = 8; r[1] = 1; r[3] = 5; r[4] = profile; r[5] = profile; r[7] = 3; r[20] = (byte)pct; return r;
    }

    [Fact]
    public void ActiveProfileFrameCarriesTheBattery()
    {
        Assert.Equal((ScyroxDriver.Kind.Battery, 66), ScyroxDriver.ParseFrame(Frame(66)));
        var other = Frame(66); other[5] = 2; Assert.Equal(ScyroxDriver.Kind.Skip, ScyroxDriver.ParseFrame(other).Result);      // pas le profil actif
        var err = Frame(66); err[62] = 1; Assert.Equal(ScyroxDriver.Kind.Skip, ScyroxDriver.ParseFrame(err).Result);
        var end = Frame(66); end[3] = 0; Assert.Equal(ScyroxDriver.Kind.End, ScyroxDriver.ParseFrame(end).Result);
        Assert.Equal(ScyroxDriver.Kind.End, ScyroxDriver.ParseFrame(Frame(101)).Result);
        Assert.Equal(ScyroxDriver.Kind.Skip, ScyroxDriver.ParseFrame(new byte[64]).Result);
        var q = ScyroxDriver.BuildRequest(); Assert.Equal(64, q.Length); Assert.Equal(9, q[63]);
    }
}

public class SemicoDriverTests
{
    [Fact]
    public void RepliesAndNamesAreHandled()
    {
        var r = new byte[64]; r[0] = 0xAA; r[1] = 0x55; r[2] = 0xCC; r[3] = 0x33; r[4] = 0x0E; r[5] = 96; r[6] = 1;
        Assert.Equal((SemicoDriver.Kind.Online, 96), SemicoDriver.ParseReply(r));
        r[6] = 0; Assert.Equal(SemicoDriver.Kind.Offline, SemicoDriver.ParseReply(r).Result);
        r[6] = 4; Assert.Equal(SemicoDriver.Kind.Invalid, SemicoDriver.ParseReply(r).Result);
        r[6] = 1; r[5] = 101; Assert.Equal(SemicoDriver.Kind.Invalid, SemicoDriver.ParseReply(r).Result);
        r[4] = 0x0F; Assert.Equal(SemicoDriver.Kind.NotThisReply, SemicoDriver.ParseReply(r).Result);
        Assert.Equal(65, SemicoDriver.BuildRequest().Length);
        Assert.Equal("Attack Shark X98", SemicoDriver.KeyboardName(0xA034, "", 0)); Assert.Equal("AJAZZ AK820 Max Plus", SemicoDriver.KeyboardName(0x8FFF, "", 0xA036));
        Assert.Equal("Wireless keyboard", SemicoDriver.KeyboardName(0x8FFF, "2.4 Wireless Device", 0)); Assert.Equal("Foo", SemicoDriver.KeyboardName(0x8FFF, "Foo", 0));
        Assert.True(SemicoDriver.IsCablePid(0xA034)); Assert.False(SemicoDriver.IsCablePid(0x8FFF)); Assert.True(SemicoDriver.IsKnownPid(0x8FFF));
    }
}

public class SinowealthReceiverDriverTests
{
    [Fact]
    public void LinkAndBatteryRepliesAreRead()
    {
        Assert.Equal(new byte[] { 5, 0x90, 0, 0, 0, 0, 0, 0 }, SinowealthReceiverDriver.BuildQuery(0x90));
        var link = new byte[8]; link[0] = 5; link[1] = 0x80; link[2] = 1;
        Assert.True(SinowealthReceiverDriver.IsReply(link, 0x80)); Assert.True(SinowealthReceiverDriver.IsLinked(link)); link[2] = 0; Assert.False(SinowealthReceiverDriver.IsLinked(link));
        var b = new byte[8]; b[0] = 5; b[1] = 0x90; b[2] = 0x11; b[3] = 72;
        Assert.Equal(72, SinowealthReceiverDriver.ParseBattery(b)); b[3] = 101; Assert.Null(SinowealthReceiverDriver.ParseBattery(b)); b[3] = 50; b[2] = 0x10; Assert.Null(SinowealthReceiverDriver.ParseBattery(b));
        Assert.False(SinowealthReceiverDriver.IsReply(new byte[9], 0x80));
    }
}
