using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class EightBitDoDriverTests
{
    [Fact]
    public void DInputAndVendorRepliesAreDecoded()
    {
        var r = new byte[34]; r[0] = 1; r[14] = 0x80 | 55;
        Assert.Equal((55, true), EightBitDoDriver.ParseDInput(r, 15)!.Value);
        r[14] = 0x80 | 100; Assert.Equal((100, false), EightBitDoDriver.ParseDInput(r, 15)!.Value);   // pleine : pas « en charge »
        r[14] = 101; Assert.Null(EightBitDoDriver.ParseDInput(r, 15));
        r[14] = 50; r[0] = 7; Assert.Null(EightBitDoDriver.ParseDInput(r, 15));
        Assert.Null(EightBitDoDriver.ParseDInput(new byte[12], 15));
        var v = new byte[64]; v[0] = 2; v[1] = 4; v[4] = 0x21; v[5] = 1; v[18] = 0x80 | 70;
        Assert.True(EightBitDoDriver.IsVendorReply(v, 0x21, 0x01)); Assert.False(EightBitDoDriver.IsVendorReply(v, 0x20, 0x01));
        Assert.Equal((70, true), EightBitDoDriver.ParseVendorBattery(v)!.Value);
        v[18] = 0; Assert.Null(EightBitDoDriver.ParseVendorBattery(v));
        var info = new byte[64]; info[22] = 0x13; info[23] = 0x60; Assert.Equal(0x6013, EightBitDoDriver.InternalPid(info));
        Assert.Null(EightBitDoDriver.InternalPid(new byte[10]));
    }

    [Fact]
    public void NamesAndModelsAreResolved()
    {
        Assert.Equal("8BitDo Pro 2", EightBitDoDriver.ModelName(0x6003, ""));
        Assert.Equal("8BitDo Foo", EightBitDoDriver.ModelName(0x9999, "Foo"));
        Assert.Equal("8Bitdo Bar", EightBitDoDriver.ModelName(0x9999, "8Bitdo Bar"));
        Assert.Equal("8BitDo Wireless Controller", EightBitDoDriver.ModelName(0x9999, ""));
        Assert.True(EightBitDoDriver.IsUltimate2(0x310B)); Assert.False(EightBitDoDriver.IsUltimate2(0x6003));
        Assert.Equal(new byte[] { 0x81, 4, 0x21, 1, 0, 0, 6 }, EightBitDoDriver.BatteryQuery());
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(121);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 5 && i % 2 == 0) { r[0] = 2; r[1] = 4; }
            var a = EightBitDoDriver.ParseDInput(r, 15); if (a != null) Assert.InRange(a.Value.Percent, 0, 100);
            if (r.Length >= 19) { var b = EightBitDoDriver.ParseVendorBattery(r); if (b != null) Assert.InRange(b.Value.Percent, 1, 100); }
            EightBitDoDriver.InternalPid(r);
        }
    }
}

public class KeychronDriverTests
{
    [Fact]
    public void MouseQueryChecksum()
    {
        var q = KeychronDriver.BuildMouseQuery(0x41);
        Assert.Equal(64, q.Length); Assert.Equal(0x41, q[0]); Assert.Equal(0x81, q[2]);
        int sum = 0; for (int i = 0; i < 63; i++) sum += q[i];
        Assert.Equal((byte)(0xA1 - sum), q[63]);
    }

    [Fact]
    public void MouseRepliesAreDecoded()
    {
        var classic = new byte[64]; classic[6] = 0x34; classic[7] = 0x34; classic[10] = 1; classic[11] = 66;
        Assert.Equal((66, true), KeychronDriver.ParseClassic(classic)!.Value);
        classic[6] = 0; Assert.Null(KeychronDriver.ParseClassic(classic));
        var k8 = new byte[64]; k8[10] = 1; k8[11] = 40; Assert.Equal((40, true), KeychronDriver.Parse8k(k8)!.Value);
        k8[10] = 4; Assert.Null(KeychronDriver.Parse8k(k8));
        var cable = new byte[64]; cable[5] = 4; cable[10] = 0; cable[11] = 80; Assert.Equal((80, false), KeychronDriver.Parse8kCable(cable)!.Value);
        cable[5] = 9; Assert.Null(KeychronDriver.Parse8kCable(cable));
        var k4 = new byte[64]; k4[5] = 1; k4[6] = 90; Assert.Equal((90, true), KeychronDriver.Parse4k(k4)!.Value);
        k4[6] = 0; Assert.Null(KeychronDriver.Parse4k(k4));
        Assert.Equal("Keychron M4 4K", KeychronDriver.Name4k(0x0621)); Assert.Null(KeychronDriver.Name4k(1));
        Assert.Equal("Keychron mouse", KeychronDriver.CleanName("Keychron Link")); Assert.Equal("Keychron M3", KeychronDriver.LegacyName(0xD033));
    }

    [Fact]
    public void LegacyAndLinkAndNapeAndUltraAreDecoded()
    {
        var l = new byte[64]; l[0] = 0x51; l[1] = 6; l[2] = 1; l[4] = 0x34; l[5] = 0x34; l[6] = 0x33; l[7] = 0xD0; l[11] = 77; l[12] = 1;
        Assert.Equal((0xD033, 77, true), ((int)KeychronDriver.ParseLegacy(l)!.Value.Pid, KeychronDriver.ParseLegacy(l)!.Value.Percent, KeychronDriver.ParseLegacy(l)!.Value.Charging));
        l[11] = 101; Assert.Null(KeychronDriver.ParseLegacy(l));
        var b2 = new byte[32]; b2[0] = 0xB2; b2[1] = 3; b2[2] = 0x34; b2[3] = 0x34; b2[4] = 0x50; b2[5] = 0x0A; b2[6] = 1;
        Assert.Equal(0x0A50, KeychronDriver.FirstConnectedKeyboardPid(b2)); b2[6] = 0; Assert.Equal(0, KeychronDriver.FirstConnectedKeyboardPid(b2));
        Assert.Equal("Keychron K5 Max", KeychronDriver.KeyboardName(0x0A52)); Assert.Equal("Keychron keyboard", KeychronDriver.KeyboardName(1));
        var a4 = new byte[32]; a4[0] = 0xA4; a4[1] = 88; a4[2] = 0x10; a4[3] = 0x0F; a4[4] = 1;
        Assert.Equal((88, 3856, true), KeychronDriver.ParseKeyboardBattery(a4)!.Value);
        a4[1] = 101; Assert.Null(KeychronDriver.ParseKeyboardBattery(a4));
        var nape = new byte[32]; nape[0] = 0xB2; nape[4] = 0x40; nape[5] = 0x04; nape[6] = 1;
        Assert.True(KeychronDriver.NapeConnected(nape)); nape[6] = 0; Assert.False(KeychronDriver.NapeConnected(nape));
        Assert.Equal((55, false), KeychronDriver.ParseNape(new byte[] { 0xA7, 0x31, 55, 0 })!.Value); Assert.Null(KeychronDriver.ParseNape(new byte[] { 0xA7, 0x31, 150, 0 }));
        var ul = new byte[32]; ul[3] = 0x34; ul[7] = 1; Assert.True(KeychronDriver.UltraConnected(ul)); Assert.False(KeychronDriver.UltraConnected(new byte[32]));
        var pw = new byte[32]; pw[20] = 0x80 | 45; Assert.Equal((45, true), KeychronDriver.ParseUltraPower(pw)!.Value); Assert.Null(KeychronDriver.ParseUltraPower(new byte[32]));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(122);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            KeychronDriver.ParseClassic(r); KeychronDriver.Parse8k(r); KeychronDriver.Parse8kCable(r); KeychronDriver.Parse4k(r); KeychronDriver.ParseLegacy(r);
            KeychronDriver.FirstConnectedKeyboardPid(r); KeychronDriver.ParseKeyboardBattery(r); KeychronDriver.NapeConnected(r); KeychronDriver.ParseNape(r);
            KeychronDriver.UltraConnected(r); KeychronDriver.ParseUltraPower(r);
        }
    }
}

public class MsiDriverTests
{
    [Fact]
    public void ConfirmedGm31FrameReads86Percent()
    {
        var r = Convert.FromHexString("0F2000002305" + "7A91000000000580" + "56" + "00");
        var s = MsiDriver.ParseStatus(r)!.Value;
        Assert.Equal(86, s.Percent); Assert.False(s.Charging);
        r[14] = 0x80 | 30; Assert.Equal((30, true), MsiDriver.ParseStatus(r)!.Value);
        r[14] = 0x7F; Assert.Equal(-1, MsiDriver.ParseStatus(r)!.Value.Percent);
        r[0] = 0x10; Assert.Null(MsiDriver.ParseStatus(r));
        Assert.Null(MsiDriver.ParseStatus(new byte[10]));
    }

    [Fact]
    public void Gm41FitAndFrame()
    {
        Assert.InRange(MsiDriver.Gm41RawToPercent(0x4D), 56, 58); Assert.InRange(MsiDriver.Gm41RawToPercent(0x45), 52, 54);
        Assert.Equal(100, MsiDriver.Gm41RawToPercent(255));
        var f = new byte[] { 0xA0, 0x4D, 0x07, 0x11, 0x94, 0, 0x94 };
        Assert.Equal((0x4D, (byte)0x11), MsiDriver.ParseGm41(f)!.Value); f[2] = 8; Assert.Null(MsiDriver.ParseGm41(f));
        var q = MsiDriver.Models.First(m => m.Mode == MsiDriver.Mode.Query);
        Assert.True(MsiDriver.Matches(q, new HidColl { Iface = 1, Page = q.Page, Usage = q.Usage, OutLen = 65, InLen = 65 }));
        Assert.False(MsiDriver.Matches(q, new HidColl { Iface = 0, Page = q.Page, Usage = q.Usage, OutLen = 65, InLen = 65 }));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(123);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 40)]; rnd.NextBytes(r); if (r.Length > 1 && i % 2 == 0) { r[0] = 0x0F; r[1] = 0x20; } var s = MsiDriver.ParseStatus(r); if (s != null) Assert.InRange(s.Value.Percent, -1, 100); MsiDriver.ParseGm41(r); }
    }
}
