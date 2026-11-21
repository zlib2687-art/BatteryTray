using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class DareuDriverTests
{
    [Fact]
    public void TogranRepliesAndListAreDecoded()
    {
        var q = DareuDriver.BuildTogranRequest(2, 4, 0x07, 0x80);
        Assert.Equal(65, q.Length); Assert.Equal(0x20, q[1]); Assert.Equal(0x80, q[4]);
        var b = new byte[65]; b[1] = 0x02; b[3] = 0x07; b[4] = 0x80; b[7] = 1; b[8] = 40; b[9] = 80;
        Assert.True(DareuDriver.IsTogranReply(b, 0x07, 0x80)); Assert.False(DareuDriver.IsTogranReply(b, 0x00, 0xA0));
        Assert.Equal((50, true), DareuDriver.ParseTogranBattery(b)!.Value);          // 40 / 80
        b[9] = 0; b[8] = 100; Assert.Equal(100, DareuDriver.ParseTogranBattery(b)!.Value.Percent); // max 0 = 100
        b[8] = 101; Assert.Null(DareuDriver.ParseTogranBattery(b));
        var l = new byte[65]; l[7] = 2; l[8] = 1; l[9] = 0x11; l[10] = 0x48; l[11] = 0; l[12] = 0x11; l[13] = 0x49;
        var list = DareuDriver.ParseTogranList(l);
        Assert.Single(list); Assert.Equal((1, (ushort)0x1148), list[0]);
    }

    [Fact]
    public void Em901xAndA84()
    {
        Assert.Equal(0x55, DareuDriver.BuildEm901xRequest().Sum(b => b) & 0xFF);
        var r = new byte[17]; r[0] = 9; r[1] = 4; r[6] = 66; r[7] = 1;
        Assert.Equal((66, true), DareuDriver.ParseEm901x(r)!.Value);
        r[6] = 101; Assert.Null(DareuDriver.ParseEm901x(r)); Assert.Null(DareuDriver.ParseEm901x(new byte[16]));
        var a = new byte[65]; a[5] = 77; a[15] = 1;
        Assert.Equal(77, DareuDriver.ParseA84(a)); a[15] = 0; Assert.Equal(-1, DareuDriver.ParseA84(a)); a[15] = 1; a[5] = 101; Assert.Null(DareuDriver.ParseA84(a));
        Assert.Equal(0xF6, DareuDriver.BuildA84Request()[2]);
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(151);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); DareuDriver.ParseTogranBattery(r); DareuDriver.ParseTogranList(r); DareuDriver.ParseEm901x(r); DareuDriver.ParseA84(r); DareuDriver.IsTogranReply(r, 7, 0x80); }
    }
}

public class DarmosharkDriverTests
{
    [Fact]
    public void BondAndIdentifyRepliesAreChecked()
    {
        var bond = new byte[33]; bond[0] = 0x51; bond[1] = 3; bond[3] = 0x8A; bond[4] = 0x24; bond[5] = 0x12; bond[6] = 0xFF; bond[7] = 1;
        Assert.True(DarmosharkDriver.IsBonded(bond, 0xFF12)); bond[7] = 0; Assert.False(DarmosharkDriver.IsBonded(bond, 0xFF12));
        var id = new byte[33]; id[0] = 0x51; id[1] = 6; id[2] = 1; id[4] = 0x8A; id[5] = 0x24; id[6] = 0x12; id[7] = 0xFF; id[11] = 73;
        Assert.Equal(73, DarmosharkDriver.ParseIdentify(id, 0xFF12)); id[11] = 101; Assert.Equal(-1, DarmosharkDriver.ParseIdentify(id, 0xFF12));
        Assert.True(DarmosharkDriver.IsGiveUp(new byte[] { 0x54, 0xE4, 2 })); Assert.False(DarmosharkDriver.IsGiveUp(new byte[] { 0x54, 0xE4, 1 }));
    }

    [Fact]
    public void NrfFrameChecksum()
    {
        var req = DarmosharkDriver.BuildNrfRequest();
        Assert.Equal(65, req.Length); Assert.Equal(0xA1, req[1..].Sum(b => b) & 0xFF);
        var r = new byte[64]; r[0] = 0x41; r[2] = 0x8C; r[3] = 1; r[6] = 55; r[63] = DarmosharkDriver.NrfChecksum(r);
        Assert.Equal(55, DarmosharkDriver.ParseNrf(r)); r[63] ^= 1; Assert.Equal(-1, DarmosharkDriver.ParseNrf(r));
    }
}

public class MachenikeDriverTests
{
    static byte[] Frame(byte mode, byte pct, byte chg)
    {
        var r = new byte[21]; r[0] = 0x51; r[1] = 6; r[4] = 0x8A; r[5] = 0x24; r[6] = 0x4E; r[7] = 0xFF; r[10] = mode; r[11] = pct; r[12] = chg; return r;
    }

    [Fact]
    public void StatusFramesAreDecoded()
    {
        Assert.Equal((true, 64, true), MachenikeDriver.DecodeStatus(Frame(1 | 8, 64, 1), false)!.Value);
        Assert.Equal((false, -1, false), MachenikeDriver.DecodeStatus(Frame(1, 64, 1), false)!.Value);   // lien radio absent : hors ligne
        Assert.Equal((true, 0, false), MachenikeDriver.DecodeStatus(Frame(0, 0, 0), true)!.Value);       // 0 % valide sur câble
        Assert.Null(MachenikeDriver.DecodeStatus(Frame(1 | 8, 64, 1), true));                            // mauvais mode
        Assert.Null(MachenikeDriver.DecodeStatus(Frame(1 | 8, 101, 0), false));
        Assert.Null(MachenikeDriver.DecodeStatus(new byte[20], false));
        Assert.Equal(21, MachenikeDriver.BuildRequest().Length);
    }
}

public class MaonoDriverTests
{
    [Fact]
    public void RequestMatchesTheDocumentedFrameAndRepliesAreChecked()
    {
        var q = MaonoDriver.BatteryRequest();
        Assert.Equal(new byte[] { 0xC4, 9, 0, 0, 4, 0x42, 0, 0xED, 0xFE }, q[..9]);
        var r = new byte[64]; r[0] = 0xC4; r[1] = 11; r[4] = 4; r[5] = 0x42; r[6] = 0; r[7] = 88; r[8] = 0;
        ushort cs = MaonoDriver.Checksum(r, 9); r[9] = (byte)cs; r[10] = (byte)(cs >> 8);
        Assert.Equal(88, MaonoDriver.ParseBattery(r));
        r[7] = 150; cs = MaonoDriver.Checksum(r, 9); r[9] = (byte)cs; r[10] = (byte)(cs >> 8); Assert.Null(MaonoDriver.ParseBattery(r));
        r[7] = 88; Assert.Null(MaonoDriver.ParseBattery(r));                      // somme périmée
        Assert.Null(MaonoDriver.ParseBattery(new byte[5]));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(152);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 4 && i % 2 == 0) { r[0] = 0xC4; r[2] = 0; r[3] = 0; r[4] = 4; r[1] = (byte)(11 + 4 * (i % 4)); } var p = MaonoDriver.ParseBattery(r); if (p != null) Assert.InRange(p.Value, 0, 100); }
    }
}
