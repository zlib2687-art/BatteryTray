using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class AkgDriverTests
{
    [Fact]
    public void BatteryFrameInTheValidPartIsRead()
    {
        var r = new byte[62]; r[0] = 7; r[1] = 12; r[2] = 0x80;
        new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00, 0x32 }.CopyTo(r, 3);
        Assert.Equal(50, AkgDriver.FindLevel(r));
    }

    [Fact]
    public void StaleFramesAfterTheValidLengthAreIgnored()
    {
        var r = new byte[62]; r[0] = 7; r[1] = 3; r[2] = 0x80;
        new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00, 0x32 }.CopyTo(r, 3); // plus long que la partie valide
        Assert.Equal(-1, AkgDriver.FindLevel(r));
        Assert.Equal(-1, AkgDriver.FindLevel(new byte[3]));
        r[1] = 12; r[11] = 101; Assert.Equal(-1, AkgDriver.FindLevel(r)); // niveau impossible
    }

    [Fact]
    public void FindLevelSurvivesRandomBuffers()
    {
        var rnd = new Random(81);
        for (int i = 0; i < 20_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); Assert.InRange(AkgDriver.FindLevel(r), -1, 100); }
    }
}

public class BarracudaProDriverTests
{
    static byte[] Reply(byte cmd, byte kind, byte len, byte data) { var r = new byte[64]; r[0] = 1; r[13] = cmd; r[14] = kind; r[15] = len; r[16] = data; return r; }

    [Fact]
    public void RepliesAreMatchedToTheCommand()
    {
        Assert.Equal(64, BarracudaProDriver.ParseReply(Reply(0x21, 1, 1, 64), 0x21));
        Assert.Equal(1, BarracudaProDriver.ParseReply(Reply(0x2A, 2, 1, 1), 0x2A));
        Assert.Null(BarracudaProDriver.ParseReply(Reply(0x21, 1, 1, 64), 0x2A));   // autre commande
        Assert.Null(BarracudaProDriver.ParseReply(Reply(0x21, 3, 1, 64), 0x21));   // type inconnu
        Assert.Null(BarracudaProDriver.ParseReply(Reply(0x21, 1, 0, 64), 0x21));   // longueur nulle
        Assert.Null(BarracudaProDriver.ParseReply(Reply(0x21, 1, 33, 64), 0x21));  // longueur trop grande
        Assert.Null(BarracudaProDriver.ParseReply(new byte[10], 0x21));
    }

    [Fact]
    public void FramesAreBuilt()
    {
        var q = BarracudaProDriver.BuildQuery(0x21);
        Assert.Equal(new byte[] { 1, 0x80, 8, 0x50, 0x41, 8, 8, 3, 0x21 }, q);
        Assert.Equal(1, BarracudaProDriver.BuildRemote(true)[9]); Assert.Equal(0, BarracudaProDriver.BuildRemote(false)[9]);
    }
}

public class AjazzDriverTests
{
    [Fact]
    public void ReplyEchoesTheRequestId()
    {
        ushort seq = 0xA1B2;
        var q = AjazzDriver.BuildRequest(seq);
        Assert.Equal(new byte[] { 4, 0xB2, 0xA1, 0x1A }, q);
        var r = new byte[64]; r[0] = 4; r[1] = 0xB2; r[2] = 0xA1; r[3] = 0x1A; r[8] = 77; r[9] = 1;
        Assert.Equal((77, true), AjazzDriver.ParseReply(r, seq)!.Value);
        Assert.Null(AjazzDriver.ParseReply(r, 0x0001));            // autre identifiant
        r[9] = 3; Assert.Null(AjazzDriver.ParseReply(r, seq));     // état de charge impossible
        r[9] = 0; r[8] = 101; Assert.Null(AjazzDriver.ParseReply(r, seq));
        Assert.Null(AjazzDriver.ParseReply(new byte[9], seq));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(82);
        for (int i = 0; i < 20_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); var p = AjazzDriver.ParseReply(r, (ushort)rnd.Next(65536)); if (p != null) Assert.InRange(p.Value.Percent, 0, 100); }
    }
}

public class CherryDriverTests
{
    [Fact]
    public void KeyboardReplyIsFiltered()
    {
        var r = new byte[64]; r[0] = 4; r[3] = 0x1A;
        Assert.True(CherryDriver.IsKeyboardReply(r));
        r[3] = 0x1B; Assert.False(CherryDriver.IsKeyboardReply(r));
        Assert.False(CherryDriver.IsKeyboardReply(new byte[9]));
        Assert.Equal(64, CherryDriver.BuildKeyboardQuery().Length);
    }

    [Fact]
    public void CapturedXtrfyFrameIsValid()
    {
        var f = Convert.FromHexString("080400000002" + "5f000ff4000000000000e5");
        Assert.True(CherryDriver.XtrfyChecksumOk(f));
        Assert.True(CherryDriver.IsXtrfyReply(f, 0x04)); Assert.False(CherryDriver.IsXtrfyReply(f, 0x03));
        var (pct, chg, mv) = CherryDriver.ParseXtrfyPower(f);
        Assert.Equal((95, false, 4084), (pct, chg, mv));
        f[3] ^= 1; Assert.False(CherryDriver.XtrfyChecksumOk(f));
    }

    [Fact]
    public void XtrfyQueryHasAValidChecksumAndOddLevelsAreUnknown()
    {
        var q = CherryDriver.BuildXtrfyQuery(0x03);
        Assert.Equal(17, q.Length); Assert.True(CherryDriver.XtrfyChecksumOk(q));
        var f = new byte[17]; f[6] = 150; Assert.Equal(-1, CherryDriver.ParseXtrfyPower(f).Percent);
        Assert.False(CherryDriver.XtrfyChecksumOk(new byte[16]));
    }
}
