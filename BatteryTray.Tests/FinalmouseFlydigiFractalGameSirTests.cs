using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class FinalmouseDriverTests
{
    static byte[] Frame(byte inId, byte cmd, params byte[] payload)
    {
        var r = new byte[64]; r[0] = inId; r[1] = (byte)(2 + payload.Length); r[2] = cmd; r[3] = (byte)payload.Length; payload.CopyTo(r, 4); return r;
    }

    [Fact]
    public void StatusBurstIsDecoded()
    {
        var st = new FinalmouseDriver.Status();
        FinalmouseDriver.Decode(Frame(5, 5, 0x24, 0x10), 5, st);            // 4132 mV
        FinalmouseDriver.Decode(Frame(5, 38, 80, 0x24, 0x10), 5, st);       // 80 %, 4132 mV
        FinalmouseDriver.Decode(Frame(5, 37, 1), 5, st);
        FinalmouseDriver.Decode(Frame(5, 36, 0), 5, st);
        Assert.Equal((80, 4132, 1, 0), (st.Soc, st.Mv, st.Charging, st.Link));
        var other = new FinalmouseDriver.Status(); FinalmouseDriver.Decode(Frame(2, 38, 50, 0, 0), 5, other);
        Assert.Equal(-1, other.Soc);                                         // autre identifiant de rapport
        var bad = new FinalmouseDriver.Status(); FinalmouseDriver.Decode(Frame(5, 38, 101, 0x24, 0x10), 5, bad); Assert.Equal(-1, bad.Soc);
    }

    [Fact]
    public void VoltageCurveAndRequests()
    {
        Assert.Equal(0, FinalmouseDriver.PercentFromMillivolts(3000)); Assert.Equal(100, FinalmouseDriver.PercentFromMillivolts(4500));
        Assert.Equal(50, FinalmouseDriver.PercentFromMillivolts(3880)); Assert.Equal(10, FinalmouseDriver.PercentFromMillivolts(3660));
        Assert.Equal(new byte[] { 4, 2, 0x80 | 96, 0 }, FinalmouseDriver.BuildGet(4, 96));
        int last = -1; for (int mv = 2900; mv <= 4500; mv += 20) { int p = FinalmouseDriver.PercentFromMillivolts(mv); Assert.InRange(p, 0, 100); Assert.True(p >= last); last = p; }
    }

    [Fact]
    public void DecodeSurvivesRandomReports()
    {
        var rnd = new Random(111);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 0 && i % 2 == 0) r[0] = 5;
            var st = new FinalmouseDriver.Status(); FinalmouseDriver.Decode(r, 5, st);
            Assert.InRange(st.Soc, -1, 100);
        }
    }
}

public class FlydigiDriverTests
{
    static byte[] Info(byte id, byte conn, byte lvlState) { var r = new byte[32]; r[0] = 0x5A; r[1] = 0xA5; r[2] = 1; r[5] = id; r[6] = conn; r[11] = lvlState; return r; }

    [Fact]
    public void InfoIsReadForTheRightModel()
    {
        var vader = FlydigiDriver.Models[0]; var apex = FlydigiDriver.Models[1];
        Assert.Equal((60, false, true), FlydigiDriver.ParseInfo(vader, Info(130, 1, 0x03), true)!.Value);
        Assert.Equal((100, false, false), FlydigiDriver.ParseInfo(apex, Info(135, 1, 0x25), false)!.Value);  // état 2 = 100 %, connexion 1 = câble (ancien)
        Assert.Equal((40, true, true), FlydigiDriver.ParseInfo(apex, Info(128, 1, 0x12), true)!.Value);
        Assert.Null(FlydigiDriver.ParseInfo(vader, Info(128, 1, 0x03), true));    // identité d'un autre modèle
        Assert.Null(FlydigiDriver.ParseInfo(vader, Info(130, 1, 0x36), true));    // niveau 6 impossible
        Assert.Null(FlydigiDriver.ParseInfo(vader, Info(130, 3, 0x03), true));    // connexion invalide
        Assert.Null(FlydigiDriver.ParseInfo(vader, new byte[8], true));
    }

    [Fact]
    public void RepliesAreNormalised()
    {
        var r = new byte[] { 0x04, 0x5A, 0xA5, 0x07, 1, 0 };
        Assert.Equal(0x5A, FlydigiDriver.NormalizeReply(r, 0x07)![0]);
        Assert.Null(FlydigiDriver.NormalizeReply(r, 0x01));
        Assert.Null(FlydigiDriver.NormalizeReply(new byte[3], 0x07));
        Assert.Equal(new byte[] { 3, 0x5A, 0xA5, 1, 2, 0 }, FlydigiDriver.BuildCommand(3, 1, 2));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(112);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 40)]; rnd.NextBytes(r); if (r.Length > 6 && i % 2 == 0) { r[0] = 0x5A; r[1] = 0xA5; r[2] = 1; }
            var n = FlydigiDriver.NormalizeReply(r, 1);
            if (n != null) { var p = FlydigiDriver.ParseInfo(FlydigiDriver.Models[i % 2], n, i % 3 == 0); if (p != null) Assert.InRange(p.Value.Percent, 0, 100); }
        }
    }
}

public class FractalDriverTests
{
    [Fact]
    public void DongleAndHeadsetRepliesAreDecoded()
    {
        var dg = new byte[64]; dg[0] = 2; dg[1] = 0x11; dg[2] = 0x21; dg[4] = 1;
        Assert.True(FractalDriver.IsReply(dg, 0x11)); Assert.True(FractalDriver.HeadsetLinked(dg));
        dg[4] = 0; Assert.False(FractalDriver.HeadsetLinked(dg));
        var hs = new byte[64]; hs[0] = 2; hs[1] = 0xF1; hs[2] = 0x21; hs[15] = 72; hs[23] = 1;
        Assert.Equal((72, true), FractalDriver.ParseHeadset(hs)!.Value);
        hs[15] = 101; Assert.Null(FractalDriver.ParseHeadset(hs));
        Assert.False(FractalDriver.IsReply(hs, 0x11)); Assert.False(FractalDriver.IsReply(new byte[10], 0xF1));
        Assert.Equal(new byte[] { 2, 0xF1, 0x21 }, FractalDriver.BuildRequest(0xF1));
    }
}

public class GameSirDriverTests
{
    [Fact]
    public void NovaAndCycloneRepliesAreDecoded()
    {
        var nova = new byte[64]; nova[0] = 1; nova[18] = 64;
        Assert.Equal((64, false), GameSirDriver.ParseNova(nova)!.Value);
        nova[18] = 0xFF; Assert.Equal((-1, true), GameSirDriver.ParseNova(nova)!.Value);
        nova[18] = 150; Assert.Null(GameSirDriver.ParseNova(nova));
        nova[18] = 50; nova[0] = 0xF4; Assert.Null(GameSirDriver.ParseNova(nova));
        var cy = new byte[64]; cy[0] = 0x12; cy[35] = 1; cy[36] = 33;
        Assert.Equal((33, true), GameSirDriver.ParseCyclone(cy)!.Value);
        cy[0] = 0x10; Assert.Null(GameSirDriver.ParseCyclone(cy));
        Assert.True(GameSirDriver.IsNova(0x1098)); Assert.True(GameSirDriver.IsCyclone2(0x100B)); Assert.False(GameSirDriver.IsNova(0x103E));
    }

    [Fact]
    public void SapjoyReplyIsDecodedAndIdentified()
    {
        var req = GameSirSapjoyDriver.BuildDeviceInfoRequest();
        Assert.Equal(32, req.Length); Assert.Equal(new byte[] { 7, 3, 0x0A, 1 }, req[..4]);
        var r = new byte[64]; r[0] = 6; r[1] = 14; r[2] = 0x0A; r[3] = 1; r[4] = 9; for (int i = 0; i < 6; i++) r[7 + i] = (byte)(0x10 + i); r[13] = 0x60; r[14] = 3;
        var a = GameSirSapjoyDriver.DecodeDeviceInfo(r)!;
        Assert.Equal(96, a.Percent); Assert.False(a.Charging);
        Assert.Equal("gamesir:sapjoy:101112131415", GameSirSapjoyDriver.Identity(a, 0x103E));
        r[13] = 0xFF; Assert.True(GameSirSapjoyDriver.DecodeDeviceInfo(r)!.Charging);
        r[13] = 200; Assert.Equal(100, GameSirSapjoyDriver.DecodeDeviceInfo(r)!.Percent);
        r[2] = 0x0B; Assert.Null(GameSirSapjoyDriver.DecodeDeviceInfo(r));
        var zero = new byte[64]; zero[0] = 6; zero[1] = 14; zero[2] = 0x0A; zero[3] = 1; zero[13] = 50;
        Assert.Equal("gamesir:103E", GameSirSapjoyDriver.Identity(GameSirSapjoyDriver.DecodeDeviceInfo(zero)!, 0x103E));
        Assert.Null(GameSirSapjoyDriver.DecodeDeviceInfo(new byte[10]));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(113);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 3 && i % 2 == 0) { r[0] = 6; r[2] = 0x0A; r[3] = 1; }
            var s = GameSirSapjoyDriver.DecodeDeviceInfo(r); if (s != null) Assert.InRange(s.Percent, -1, 100);
            var n = GameSirDriver.ParseNova(r); if (n != null) Assert.InRange(n.Value.Percent, -1, 100);
            var c = GameSirDriver.ParseCyclone(r); if (c != null) Assert.InRange(c.Value.Percent, 0, 100);
        }
    }
}
