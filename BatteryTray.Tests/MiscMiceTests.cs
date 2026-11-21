using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class MiscMiceDriverTests
{
    [Fact]
    public void MotospeedAndArdorAreDecoded()
    {
        var r = new byte[32]; r[0] = 0xB4; r[20] = 128 + 70;
        Assert.Equal((70, true), MiscMiceDriver.ParseMotospeed(r)!.Value); r[20] = 55; Assert.Equal((55, false), MiscMiceDriver.ParseMotospeed(r)!.Value);
        Assert.Null(MiscMiceDriver.ParseMotospeed(new byte[10]));
        Assert.Equal(50, MiscMiceDriver.ArdorFuryV2Percent(new byte[] { 3, 0, 0x40, 0x33, 7 }));
        Assert.Equal(75, MiscMiceDriver.ArdorFuryV2Percent(new byte[] { 3, 0, 0x40, 0x73, 8 }));
        Assert.Equal(-1, MiscMiceDriver.ArdorFuryV2Percent(new byte[] { 3, 0, 0x40, 0x34, 8 })); Assert.Equal(-1, MiscMiceDriver.ArdorFuryV2Percent(new byte[] { 3, 1, 0x40, 0x33, 8 }));
    }

    [Fact]
    public void XentaStatesFollowTheDocumentedRules()
    {
        var f = MiscMiceDriver.ParseXenta(new byte[] { 3, 0x24, 0x41, 1, 100 })!;                      // « 03 24 41 01 64 » : Attack Shark V6 sur batterie à 100 %
        var s = MiscMiceDriver.XentaStatus(0xFA60, f, true, -1, false, -1);
        Assert.Equal("Attack Shark V6", s.Name); Assert.Equal(100, s.Percent); Assert.False(s.Charging);
        Assert.Equal(-1, MiscMiceDriver.XentaStatus(0xFA60, f, false, -1, false, -1).Percent);         // une seule trame : pas de faux 100 % de liaison
        var chg = MiscMiceDriver.XentaStatus(0xFA60, MiscMiceDriver.ParseXenta(new byte[] { 3, 0x24, 0x40, 3, 60 })!, true, -1, false, -1);
        Assert.True(chg.Charging); Assert.Equal(-1, chg.Percent);
        var full = MiscMiceDriver.XentaStatus(0xFA60, MiscMiceDriver.ParseXenta(new byte[] { 3, 0x24, 0x40, 2, 100 })!, true, -1, false, -1); Assert.Equal(100, full.Percent);
        var tens = MiscMiceDriver.XentaStatus(0xFA61, MiscMiceDriver.ParseXenta(new byte[] { 3, 0x10, 0x40, 1, 7 })!, true, -1, false, -1); Assert.Equal(70, tens.Percent);
        var delux = MiscMiceDriver.XentaStatus(0xFA65, MiscMiceDriver.ParseXenta(new byte[] { 3, 0x30, 0x40, 3, 44 })!, false, -1, false, -1); Assert.Equal((44, true), (delux.Percent, delux.Charging));
        var ardor = MiscMiceDriver.XentaStatus(0xFA60, MiscMiceDriver.ParseXenta(new byte[] { 3, 0, 0x40, 0x73, 8 })!, false, 75, true, 75); Assert.Equal("ARDOR GAMING Fury V2", ardor.Name); Assert.Equal(75, ardor.Percent);
        var asleep = MiscMiceDriver.XentaStatus(0xFA60, MiscMiceDriver.ParseXenta(new byte[] { 3, 0, 0x40, 0x73, 8 })!, false, 75, false, 75); Assert.False(asleep.Connected);
        Assert.Null(MiscMiceDriver.ParseXenta(new byte[] { 3, 1, 0x42, 1, 1 })); Assert.Null(MiscMiceDriver.ParseXenta(new byte[4]));
    }

    [Fact]
    public void AjazzFamiliesAreDecoded()
    {
        var r = new byte[32]; r[0] = 0x10; r[4] = (byte)'M'; r[5] = (byte)'1'; r[6] = (byte)'7'; r[7] = (byte)'9'; r[12] = 1; r[13] = 66; r[14] = 1;
        int sum = 0; for (int i = 4; i <= 30; i++) sum += r[i]; r[31] = (byte)sum;
        var a = MiscMiceDriver.ParseAj179(r)!.Value; Assert.Equal(("Ajazz AJ179", 66, true, true), (a.Name, a.Percent, a.Charging, a.MouseConnected));
        r[31] ^= 1; Assert.Null(MiscMiceDriver.ParseAj179(r));
        Assert.Equal(80, MiscMiceDriver.ParseAjSeries(new byte[] { 5, 0, 0, 80 })); Assert.Null(MiscMiceDriver.ParseAjSeries(new byte[] { 5, 1, 0, 80 })); Assert.Null(MiscMiceDriver.ParseAjSeries(new byte[] { 5, 0, 0, 101 }));
        var b = new byte[64]; b[0] = 0xAA; b[1] = 0x30; b[8] = 45; b[9] = 1;
        Assert.Equal((45, true), MiscMiceDriver.ParseAj139(b)!.Value); b[1] = 0xFA; Assert.Null(MiscMiceDriver.ParseAj139(b));
    }

    [Fact]
    public void IncottWraithAndDeluxAreDecoded()
    {
        Assert.Equal((70, true), MiscMiceDriver.ParseIncott(new byte[] { 9, 0x89, 0x80 | 70, 5 })!.Value);
        Assert.Null(MiscMiceDriver.ParseIncott(new byte[] { 9, 0x89, 0, 0 })); Assert.Null(MiscMiceDriver.ParseIncott(new byte[] { 9, 0x88, 50, 5 })); Assert.Null(MiscMiceDriver.ParseIncott(new byte[] { 9, 0x89, 101, 5 }));
        Assert.Equal(18, MiscMiceDriver.ParseW1Info(new byte[] { 9, 0x8F, 0, 18 })); Assert.Equal(20, MiscMiceDriver.ParseW1Info(new byte[] { 9, 0x8F, 0, 20 }));
        Assert.Equal(-1, MiscMiceDriver.ParseW1Info(new byte[] { 9, 0x8F, 0, 6 })); Assert.Equal(0, MiscMiceDriver.ParseW1Info(new byte[] { 9, 0x86, 0, 18 }));
        Assert.Equal((90, true), MiscMiceDriver.ParseW1Status(new byte[] { 9, 0x80 | 90 })!.Value); Assert.Null(MiscMiceDriver.ParseW1Status(new byte[] { 9, 101 }));
        var d = new byte[33]; d[0] = 0x0C; d[2] = 0x20; d[6] = 1; d[18] = 64; d[19] = 1;
        Assert.Equal((64, (byte)1, true), MiscMiceDriver.ParseDelux(d)!.Value); d[18] = 101; Assert.Null(MiscMiceDriver.ParseDelux(d));
        Assert.Equal(7, MiscMiceDriver.BuildDeluxRequest(33, 7)[4]);
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(171);
        for (int i = 0; i < 40_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            MiscMiceDriver.ParseMotospeed(r); MiscMiceDriver.ArdorFuryV2Percent(r); MiscMiceDriver.ParseAj179(r); MiscMiceDriver.ParseAjSeries(r); MiscMiceDriver.ParseAj139(r);
            MiscMiceDriver.ParseIncott(r); MiscMiceDriver.ParseW1Info(r); MiscMiceDriver.ParseW1Status(r); MiscMiceDriver.ParseDelux(r);
            if (MiscMiceDriver.ParseXenta(r) is { } f) { var s = MiscMiceDriver.XentaStatus((ushort)(0xFA55 + i % 12), f, i % 2 == 0, i % 3 == 0 ? 50 : -1, i % 5 == 0, 50); Assert.InRange(s.Percent, -1, 100); }
        }
    }
}
