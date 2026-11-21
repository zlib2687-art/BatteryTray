using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class EndgameDriverTests
{
    [Fact]
    public void CommandsAndRepliesAreRead()
    {
        var c = EndgameDriver.BuildCommand(0xB4, 0);
        Assert.Equal(64, c.Length); Assert.Equal(0xA1, c[0]); Assert.Equal(0xB4, c[1]);
        var r = new byte[64]; r[0] = 0xA1; r[1] = 1; r[16] = 88;
        Assert.Equal(1, EndgameDriver.ReplyStatus(r)); Assert.Equal(88, EndgameDriver.Level(r));
        r[16] = 150; Assert.Equal(-1, EndgameDriver.Level(r));
        Assert.Null(EndgameDriver.ReplyStatus(new byte[10]));
        r[0] = 0; Assert.Null(EndgameDriver.ReplyStatus(r));
    }

    [Fact]
    public void MouseNameComesFromTheInfoReply()
    {
        var info = new byte[64]; info[16] = 0x67; info[17] = 0x33; info[18] = 0x84; info[19] = 0x19;
        Assert.Equal("Endgame Gear OP1w 4k v2", EndgameDriver.NameFromInfo(info));
        info[17] = 0; Assert.Null(EndgameDriver.NameFromInfo(info));
        Assert.Null(EndgameDriver.MouseName(0x0001));
    }
}

public class EndorfyDriverTests
{
    static byte[] Report(params byte[] race) { var r = new byte[62]; r[0] = 7; r[1] = (byte)race.Length; r[2] = 0x80; race.CopyTo(r, 3); return r; }

    [Fact]
    public void BatteryIndicationIsFoundAlsoAfterTheAcknowledgement()
    {
        var ack = new byte[] { 0x05, 0x5B, 0x03, 0x00, 0xD6, 0x0C, 0x00 };
        var ind = new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00, 0x42 };
        Assert.Equal(0x42, EndorfyDriver.BatteryPercent(Report(ind)));
        Assert.Equal(0x42, EndorfyDriver.BatteryPercent(Report(ack.Concat(ind).ToArray())));
        Assert.Equal(-1, EndorfyDriver.BatteryPercent(Report(ack)));
    }

    [Fact]
    public void BadFramesAreRejected()
    {
        var ind = new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x01, 0x00, 0x42 };   // état non nul
        Assert.Equal(-1, EndorfyDriver.BatteryPercent(Report(ind)));
        ind[6] = 0; ind[8] = 101; Assert.Equal(-1, EndorfyDriver.BatteryPercent(Report(ind)));
        Assert.Equal(-1, EndorfyDriver.BatteryPercent(new byte[5]));
        var other = Report(new byte[] { 0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00, 0x42 }); other[0] = 6;
        Assert.Equal(-1, EndorfyDriver.BatteryPercent(other));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(91);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 0 && i % 2 == 0) r[0] = 7; Assert.InRange(EndorfyDriver.BatteryPercent(r), -1, 100); }
    }
}

public class EweadnDriverTests
{
    [Fact]
    public void RequestChecksumIsTheSumOfTheFollowingBytes()
    {
        var q = EweadnDriver.BuildRequest(0x4F, 0x81);
        int sum = 0; for (int i = 2; i < q.Length; i++) sum += q[i];
        Assert.Equal((byte)(sum & 0xFF), q[1]); Assert.Equal(3, q[0]); Assert.Equal(0x50, q[2]);
    }

    [Fact]
    public void RepliesAndBasicsAreDecoded()
    {
        var r = new byte[64]; r[0] = 3; r[1] = 0x50; r[3] = 1; r[6] = 0x80 | 64;
        var data = EweadnDriver.ParseReply(r)!;
        Assert.Equal((64, true), EweadnDriver.ParseBasics(data[0]));
        Assert.Equal((100, false), EweadnDriver.ParseBasics(100));
        Assert.Equal(-1, EweadnDriver.ParseBasics(0x7F).Percent);
        r[2] = 1; Assert.Null(EweadnDriver.ParseReply(r));        // ack non nul
        r[2] = 0; r[3] = 0; Assert.Null(EweadnDriver.ParseReply(r)); // longueur nulle
        r[3] = 60; Assert.Null(EweadnDriver.ParseReply(r));       // déborde du rapport
        Assert.Null(EweadnDriver.ParseReply(new byte[5]));
    }
}

public class FantechDriverTests
{
    [Fact]
    public void PacketChecksumMakesTheSum0x55()
    {
        var p = FantechDriver.MakePacket(0x04);
        Assert.Equal(17, p.Length); Assert.Equal(0x55, p.Sum(b => b) & 0xFF);
    }

    [Fact]
    public void BatteryReplyIsParsedWithOrWithoutTheReportId()
    {
        var withId = new byte[17]; withId[0] = 8; withId[1] = 4; withId[6] = 73; withId[7] = 1;
        var a = FantechDriver.ParseBattery(withId)!.Value;
        Assert.Equal(73, a.Percent); Assert.True(a.Charging);
        var withoutId = new byte[17]; withoutId[0] = 4; withoutId[5] = 40; withoutId[6] = 0;
        Assert.Equal(40, FantechDriver.ParseBattery(withoutId)!.Value.Percent);
        withId[6] = 0; Assert.Null(FantechDriver.ParseBattery(withId));      // niveau 0 impossible
        withId[6] = 50; withId[7] = 2; Assert.Null(FantechDriver.ParseBattery(withId)); // charge invalide
        Assert.Null(FantechDriver.ParseBattery(new byte[4]));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(92);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 40)]; rnd.NextBytes(r);
            if (r.Length > 1 && i % 2 == 0) r[i % 4 == 0 ? 0 : 1] = 4;
            var p = FantechDriver.ParseBattery(r); if (p != null) Assert.InRange(p.Value.Percent, 1, 100);
        }
    }
}
