using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class ArbiterDriverTests
{
    [Fact]
    public void RepliesAreDecodedWithOrWithoutTheReportId()
    {
        var withId = new byte[65]; withId[1] = 0xA1; withId[4] = 2; withId[6] = 0x83; withId[7] = 1; withId[8] = 77;
        Assert.Equal((ArbiterDriver.Kind.Battery, 77, true), ArbiterDriver.Decode(withId));
        var without = new byte[64]; without[0] = 0xA2; without[3] = 2; without[5] = 0x83; without[7] = 55;
        Assert.Equal((ArbiterDriver.Kind.Battery, 55, false), ArbiterDriver.Decode(without));
        withId[1] = 0xA0; Assert.Equal(ArbiterDriver.Kind.Asleep, ArbiterDriver.Decode(withId).Result);
        withId[1] = 0x00; Assert.Equal(ArbiterDriver.Kind.None, ArbiterDriver.Decode(withId).Result);
        withId[1] = 0xA1; withId[8] = 101; Assert.Equal(ArbiterDriver.Kind.None, ArbiterDriver.Decode(withId).Result);
        Assert.Equal(ArbiterDriver.Kind.None, ArbiterDriver.Decode(new byte[5]).Result);
        var q = ArbiterDriver.BuildRequest(); Assert.Equal(65, q.Length); Assert.Equal(0x83, q[6]);
        Assert.Equal(6, ArbiterDriver.Models.Length); Assert.True(ArbiterDriver.Models.Take(3).All(m => !m.Receiver)); // câbles d'abord
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(141);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); var d = ArbiterDriver.Decode(r); if (d.Result == ArbiterDriver.Kind.Battery) Assert.InRange(d.Percent, 0, 100); }
    }
}

public class AresonDriverTests
{
    [Fact]
    public void RequestAndReplyChecksums()
    {
        var q = AresonDriver.BuildGetPower(); Assert.Equal(17, q.Length); Assert.Equal(0x55, q.Sum(b => b) & 0xFF);
        var r = new byte[17]; r[0] = 9; r[1] = 4; r[6] = 8; r[7] = 1;
        int sum = 0; for (int i = 0; i < 16; i++) sum += r[i]; r[16] = (byte)(0x55 - sum);
        Assert.Equal((80, true), AresonDriver.ParseReply(r)!.Value);
        r[6] = 11; sum = 0; for (int i = 0; i < 16; i++) sum += r[i]; r[16] = (byte)(0x55 - sum); Assert.Null(AresonDriver.ParseReply(r)); // niveau > 10
        r[6] = 8; Assert.Null(AresonDriver.ParseReply(r));                                                                                  // somme fausse
        Assert.Null(AresonDriver.ParseReply(new byte[10]));
    }
}

public class ChimeraDriverTests
{
    static byte[] Frame(byte cmd, byte status, byte pct, byte chg, byte mvHi, byte mvLo)
    {
        var r = new byte[17]; r[0] = 8; r[1] = cmd; r[5] = status; r[6] = pct; r[7] = chg; r[8] = mvHi; r[9] = mvLo;
        int s = 0; for (int i = 0; i < 16; i++) s += r[i]; r[16] = (byte)(0x55 - s); return r;
    }

    [Fact]
    public void FramesAreValidatedAndDecoded()
    {
        Assert.True(ChimeraDriver.IsValidReply(ChimeraDriver.BuildQuery(4), 4));
        var b = Frame(4, 2, 81, 1, 0x0F, 0xA0);
        Assert.True(ChimeraDriver.IsValidReply(b, 4)); Assert.False(ChimeraDriver.IsValidReply(b, 3));
        Assert.Equal((81, true, 4000), ChimeraDriver.ParseBattery(b)!.Value);
        Assert.Null(ChimeraDriver.ParseBattery(Frame(4, 1, 81, 0, 0, 0))); Assert.Null(ChimeraDriver.ParseBattery(Frame(4, 2, 0, 0, 0, 0)));
        Assert.True(ChimeraDriver.IsOnline(Frame(3, 1, 0, 0, 0, 0))); Assert.False(ChimeraDriver.IsOnline(Frame(3, 0, 0, 0, 0, 0)));
        b[3] ^= 1; Assert.False(ChimeraDriver.IsValidReply(b, 4));
    }
}

public class DarkrimDriverTests
{
    static byte[] Reply(byte seq, byte value)
    {
        var body = new byte[] { 0xFE, seq, 0x10, 0, 2, 0, 0, 3, 0, 1, 1, value };
        ushort crc = DarkrimDriver.Crc(body, body.Length);
        return body.Concat(new[] { (byte)(crc >> 8), (byte)crc }).ToArray();
    }

    [Fact]
    public void RequestCrcAndReplyAreHandled()
    {
        var q = DarkrimDriver.BatteryRequest(3);
        Assert.Equal(0, q[0]); Assert.Equal(0xFE, q[1]); Assert.Equal(3, q[2]);
        Assert.Equal(0x29B1, DarkrimDriver.Crc(System.Text.Encoding.ASCII.GetBytes("123456789"), 9)); // valeur de contrôle CRC-16/CCITT-FALSE
        Assert.Equal((60, true), DarkrimDriver.ParseReply(Reply(3, 0x80 | 60), 3)!.Value);
        Assert.Equal((100, false), DarkrimDriver.ParseReply(Reply(3, 100), 3)!.Value);
        Assert.Null(DarkrimDriver.ParseReply(Reply(3, 0x80 | 101), 3));            // niveau impossible
        Assert.Null(DarkrimDriver.ParseReply(Reply(4, 50), 3));                    // autre séquence
        var bad = Reply(3, 50); bad[^1] ^= 1; Assert.Null(DarkrimDriver.ParseReply(bad, 3)); // CRC faux
        Assert.Null(DarkrimDriver.ParseReply(new byte[5], 3));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(142);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 8 && i % 2 == 0) { r[0] = 0xFE; r[1] = 3; r[2] = 0x10; r[3] = 0; r[4] = 2; r[5] = 0; r[6] = 0; r[7] &= 0x20; r[8] = 0; } DarkrimDriver.ParseReply(r, 3); }
    }
}

public class BeitongDriverTests
{
    [Fact]
    public void CipherRoundTripsAndMatchesTheWireRequest()
    {
        for (int x = 0; x < 256; x++) Assert.Equal((byte)x, BeitongDriver.Dec(BeitongDriver.Enc((byte)x)));
        Assert.Equal(0xAD, BeitongDriver.Enc(0x15)); Assert.Equal(new byte[] { 2, 0xAD }, BeitongDriver.BuildRequest());
        Assert.Equal("BEITONG KP20", BeitongDriver.ReceiverName(0x5128)); Assert.Null(BeitongDriver.ReceiverName(1));
    }

    [Fact]
    public void RepliesAreDecoded()
    {
        byte[] Reply(byte level, byte charge) => new byte[] { 2, BeitongDriver.Enc(0x15), BeitongDriver.Enc(level), BeitongDriver.Enc(0), BeitongDriver.Enc(charge), 0 };
        Assert.Equal((64, true), BeitongDriver.ParseReply(Reply(64, 1), 0x5128)!.Value);
        Assert.Equal((64, false), BeitongDriver.ParseReply(Reply(64, 0x11), 0x5128)!.Value);   // KP20 : octet entier
        Assert.Equal((64, true), BeitongDriver.ParseReply(Reply(64, 0x11), 0x515C)!.Value);    // KP40D : quartet bas
        Assert.Null(BeitongDriver.ParseReply(Reply(120, 0), 0x5128));
        var other = Reply(50, 0); other[1] = BeitongDriver.Enc(0x25); Assert.Null(BeitongDriver.ParseReply(other, 0x5128));
        Assert.Null(BeitongDriver.ParseReply(new byte[3], 0x5128));
    }
}
