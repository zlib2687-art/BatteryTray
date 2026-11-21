using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class AlienwareDriverTests
{
    static byte[] Good() => Convert.FromHexString("07C00A09000" + "15F00001C105F0206");

    [Fact]
    public void DocumentedExampleDecodesTo95Percent()
    {
        var b = AlienwareDriver.DecodeBattery(Good());
        Assert.NotNull(b); Assert.Equal(95, b!.Value.Percent); Assert.False(b.Value.Charging);
    }

    [Fact]
    public void ChargingCriticalAndInvalidRepliesAreHandled()
    {
        static byte[] WithSum(byte[] r) { byte x = 0; for (int i = 3; i <= 12; i++) x ^= r[i]; r[13] = x; return r; }
        var chg = Good(); chg[7] = 1; Assert.True(AlienwareDriver.DecodeBattery(WithSum(chg))!.Value.Charging);
        var crit = Good(); crit[8] = 1; crit[11] = 7; Assert.Equal(7, AlienwareDriver.DecodeBattery(WithSum(crit))!.Value.Percent); // niveau critique lu en [11]
        var bad = Good(); bad[13] ^= 0xFF; Assert.Null(AlienwareDriver.DecodeBattery(bad));                                        // somme fausse
        var zero = Good(); zero[6] = 0; Assert.Null(AlienwareDriver.DecodeBattery(WithSum(zero)));                                  // [6] nul rejeté
        var over = Good(); over[6] = 120; Assert.Null(AlienwareDriver.DecodeBattery(WithSum(over)));
        Assert.Null(AlienwareDriver.DecodeBattery(new byte[] { 0x07, 0x00, 0x80 }));                                                // « pas encore de réponse »
        Assert.Null(AlienwareDriver.DecodeBattery(new byte[20]));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(71);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 64)]; rnd.NextBytes(r);
            if (r.Length >= 14 && i % 2 == 0) { r[0] = 7; r[1] = 0xC0; r[2] = 0x0A; r[3] = 9; byte x = 0; for (int k = 3; k <= 12; k++) x ^= r[k]; r[13] = x; }
            var b = AlienwareDriver.DecodeBattery(r); if (b != null) Assert.InRange(b.Value.Percent, 0, 100);
        }
    }
}

public class AudezeDriverTests
{
    [Fact]
    public void LevelIsFoundInARollingBuffer()
    {
        var buf = new byte[62]; buf[10] = 0xD6; buf[11] = 0x0C; buf[14] = 73;
        Assert.Equal(73, AudezeDriver.FindLevel(buf));
        Assert.Equal(-1, AudezeDriver.FindLevel(new byte[62]));
        var tooBig = (byte[])buf.Clone(); tooBig[14] = 101; Assert.Equal(-1, AudezeDriver.FindLevel(tooBig));
        var edge = new byte[5]; edge[0] = 0xD6; edge[1] = 0x0C; edge[4] = 100; Assert.Equal(100, AudezeDriver.FindLevel(edge));
        Assert.Equal(-1, AudezeDriver.FindLevel(new byte[] { 0xD6, 0x0C, 0, 0 }));
    }

    [Fact]
    public void UnlinkedDongleIsRecognisedAndCableNever()
    {
        var dongle = AudezeDriver.Models.First(m => !m.Cable); var cable = AudezeDriver.Models.First(m => m.Cable);
        Assert.True(AudezeDriver.IsUnlinkedDongle(dongle, "Audeze Maxwell Dongle"));
        Assert.False(AudezeDriver.IsUnlinkedDongle(dongle, "Audeze Maxwell"));
        Assert.False(AudezeDriver.IsUnlinkedDongle(cable, "Audeze Maxwell Dongle"));
        Assert.Equal(0x06, AudezeDriver.BuildRequest()[0]);
        Assert.Equal(0x4B1A, AudezeDriver.Models[0].Pid); // câbles en premier
    }

    [Fact]
    public void FindLevelSurvivesRandomBuffers()
    {
        var rnd = new Random(72);
        for (int i = 0; i < 20_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); Assert.InRange(AudezeDriver.FindLevel(r), -1, 100); }
    }
}

public class AkkoDriverTests
{
    [Fact]
    public void QueryHasAValidChecksum()
    {
        var q = AkkoDriver.BuildQuery();
        Assert.Equal(64, q.Length); Assert.Equal(4, q[0]); Assert.Equal(0x1A, q[3]);
        int sum = 0; for (int i = 3; i < q.Length; i++) sum += q[i];
        Assert.Equal(sum & 0xFFFF, q[1] | (q[2] << 8));
        Assert.Equal(0x1A + 3 + 0x55, sum);
    }

    [Fact]
    public void RepliesAreFiltered()
    {
        var r = new byte[64]; r[0] = 4; r[3] = 0x1A; r[8] = 88;
        Assert.True(AkkoDriver.IsStatusReply(r));
        r[8] = 101; Assert.False(AkkoDriver.IsStatusReply(r));
        r[8] = 50; r[3] = 0x1B; Assert.False(AkkoDriver.IsStatusReply(r));
        Assert.False(AkkoDriver.IsStatusReply(new byte[9]));
    }
}
