using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class LogitechExtraDriverTests
{
    [Fact]
    public void RepliesAreMatchedAndErrorsDecoded()
    {
        var ok = new byte[20]; ok[0] = 0x11; ok[1] = 1; ok[2] = 5; ok[3] = 0x1D; ok[4] = 9;
        var p = LogitechExtraDriver.ParseReply(ok, 1, 5, 0x1D);
        Assert.Equal(LogitechExtraDriver.Reply.Ok, p.Result); Assert.Equal(9, p.Payload[0]);
        var err = new byte[7]; err[0] = 0x10; err[1] = 2; err[2] = 0x8F; err[3] = 0x81; err[4] = 0x0D; err[5] = 3;
        var e = LogitechExtraDriver.ParseReply(err, 2, 0x81, 0x0D);
        Assert.Equal(LogitechExtraDriver.Reply.Error, e.Result); Assert.Equal(3, e.Error);
        Assert.Equal(LogitechExtraDriver.Reply.None, LogitechExtraDriver.ParseReply(ok, 2, 5, 0x1D).Result);
        Assert.Equal(LogitechExtraDriver.Reply.None, LogitechExtraDriver.ParseReply(new byte[3], 1, 5, 0x1D).Result);
    }

    [Fact]
    public void BatteryFeaturesAreDecoded()
    {
        Assert.Equal((80, true), LogitechExtraDriver.ParseUnified(new byte[] { 80, 0, 1 })!.Value);
        Assert.Equal(60, LogitechExtraDriver.ParseUnified(new byte[] { 0, 4, 0 })!.Value.Percent);   // niveau « Good »
        Assert.Equal(100, LogitechExtraDriver.ParseLevelStatus(new byte[] { 0, 0, 3 })!.Value.Percent); // 0 + chargé = 100
        Assert.Equal(-1, LogitechExtraDriver.ParseLevelStatus(new byte[] { 200, 0, 0 })!.Value.Percent);
        var v = LogitechExtraDriver.ParseVoltage(new byte[] { 0x0F, 0xA0, 0x80 })!.Value; // 4000 mV
        Assert.Equal(4000, v.Millivolts); Assert.True(v.Charging); Assert.InRange(v.Percent, 80, 90);
        Assert.Equal((4000, false), LogitechExtraDriver.ParseAdc(new byte[] { 0x0F, 0xA0, 0 })!.Value);
        Assert.True(LogitechExtraDriver.ParseAdc(new byte[] { 0, 1, 3 })!.Value.Charging);
        Assert.Null(LogitechExtraDriver.ParseAdc(new byte[2]));
    }

    [Fact]
    public void Hidpp10RegistersAreDecoded()
    {
        Assert.Equal((55, true), LogitechExtraDriver.ParseRegister0D(new byte[] { 55, 0, 0x22 })!.Value);
        Assert.Null(LogitechExtraDriver.ParseRegister0D(new byte[] { 0, 0, 0 }));
        Assert.Null(LogitechExtraDriver.ParseRegister0D(new byte[] { 101, 0, 0 }));
        Assert.Equal((50, false), LogitechExtraDriver.ParseRegister07(new byte[] { 5, 0 })!.Value);
        Assert.Equal(100, LogitechExtraDriver.ParseRegister07(new byte[] { 200, 0 })!.Value.Percent);
        Assert.Null(LogitechExtraDriver.ParseRegister07(new byte[] { 0, 0 }));
    }

    [Fact]
    public void HeadsetCurvesAndKindsAndPhysicalKey()
    {
        Assert.Equal(100, LogitechExtraDriver.HeadsetPercent(0x0A66, 4300));
        Assert.Equal(0, LogitechExtraDriver.HeadsetPercent(0x0ABA, 3000));
        Assert.InRange(LogitechExtraDriver.HeadsetPercent(0x0ABA, 3800), 55, 70);
        Assert.Equal(DeviceKind.Headset, LogitechExtraDriver.KindFromType(8));
        Assert.Equal(DeviceKind.Keyboard, LogitechExtraDriver.KindFromType(0));
        Assert.Equal(DeviceKind.Other, LogitechExtraDriver.KindFromType(0x7F));
        var a = LogitechExtraDriver.PhysicalKey(@"\\?\hid#vid_046d&pid_c547&mi_02&col01#8&2a2b3c4d&0&0000#{guid}");
        var b = LogitechExtraDriver.PhysicalKey(@"\\?\hid#vid_046d&pid_c547&mi_02&col02#8&2a2b3c4d&0&0001#{guid}");
        var c = LogitechExtraDriver.PhysicalKey(@"\\?\hid#vid_046d&pid_c547&mi_02&col01#8&99999999&0&0000#{guid}");
        Assert.Equal(a, b); Assert.NotEqual(a, c);
        Assert.Equal("", LogitechExtraDriver.PhysicalKey("bizarre"));
    }

    [Fact]
    public void ParsersSurviveRandomInput()
    {
        var rnd = new Random(101);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 24)]; rnd.NextBytes(r);
            LogitechExtraDriver.ParseReply(r, (byte)rnd.Next(7), (byte)rnd.Next(256), (byte)rnd.Next(256));
            LogitechExtraDriver.ParseUnified(r); LogitechExtraDriver.ParseLevelStatus(r); LogitechExtraDriver.ParseVoltage(r);
            LogitechExtraDriver.ParseAdc(r); LogitechExtraDriver.ParseRegister0D(r); LogitechExtraDriver.ParseRegister07(r);
            var v = LogitechExtraDriver.ParseVoltage(r); if (v != null) Assert.InRange(v.Value.Percent, -1, 100);
        }
    }
}

public class LogitechG435DriverTests
{
    [Fact]
    public void Crc16MatchesTheXmodemCheckValue() => Assert.Equal(0x31C3, LogitechG435Driver.Crc16(System.Text.Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void FrameCrcCoversTheWholeFrameWithTheCrcFieldZeroed()
    {
        var f = LogitechG435Driver.Frame(0x84, new byte[] { 1 });
        Assert.Equal(64, f.Length); Assert.Equal(0x84, f[0]); Assert.Equal(1, f[1]); Assert.Equal(1, f[4]);
        int crc = f[2] | (f[3] << 8);
        var z = (byte[])f.Clone(); z[2] = 0; z[3] = 0;
        Assert.Equal(LogitechG435Driver.Crc16(z), (ushort)crc);
        Assert.Equal(new byte[] { 0x50, 0x41, 6, 2, 1, 0, 0x31 }, LogitechG435Driver.Pa(6, 2, 1, 0, 0x31));
    }

    [Fact]
    public void VoltageIsFoundAndConvertedToPercent()
    {
        var r = new byte[64]; new byte[] { 0, 7, 0, 6, 9, 0, 0x68, 0x10 }.CopyTo(r, 20); // 4200 mV
        Assert.Equal(4200, LogitechG435Driver.ParseMillivolts(r)); Assert.Equal(100, LogitechG435Driver.Percent(4200));
        r[26] = 0x00; r[27] = 0x00; Assert.Equal(-1, LogitechG435Driver.ParseMillivolts(r)); // 0 mV : invraisemblable
        Assert.Equal(0, LogitechG435Driver.Percent(3300)); Assert.Equal(100, LogitechG435Driver.Percent(4500));
        Assert.InRange(LogitechG435Driver.Percent(3800), 54, 56);
        Assert.Equal(-1, LogitechG435Driver.ParseMillivolts(new byte[4]));
    }

    [Fact]
    public void PercentIsMonotonic()
    {
        int last = -1; for (int mv = 3000; mv <= 4400; mv += 10) { int p = LogitechG435Driver.Percent(mv); Assert.InRange(p, 0, 100); Assert.True(p >= last); last = p; }
    }
}

public class ZowieDriverTests
{
    [Fact]
    public void CwAndDwRepliesAreDecoded()
    {
        var cw = new byte[64]; cw[0] = 0x68; cw[1] = 0x0F; cw[2] = 67;                      // 3944 mV, 67 %
        Assert.Equal((3944, 67), ZowieDriver.ParseCw(cw)!.Value);
        Assert.Equal((0, -1), ZowieDriver.ParseCw(new byte[64])!.Value);                     // non liée
        cw[0] = 0x00; cw[1] = 0x01; Assert.Null(ZowieDriver.ParseCw(cw));                    // 256 mV : pas une batterie
        var dw = new byte[16]; dw[0] = 9; dw[1] = 1; dw[2] = 0x7C; dw[4] = 0xC8;             // (256 + 124) × 10 = 3800 mV
        Assert.Equal(3800, ZowieDriver.ParseDw(dw));
        dw[4] = 0; Assert.Null(ZowieDriver.ParseDw(dw)); dw[4] = 0xC8; dw[1] = 0xE6; Assert.Null(ZowieDriver.ParseDw(dw));
        Assert.Null(ZowieDriver.ParseDw(new byte[3]));
    }

    [Fact]
    public void ChargeIsInferredFromTheVoltageTrend()
    {
        var t = new ZowieDriver.ChargeTrend();
        Assert.False(t.Update(3800)); Assert.False(t.Update(3790)); Assert.False(t.Update(3820));
        Assert.True(t.Update(3850));                                    // +50 mV au-dessus du minimum : posée sur la base
        Assert.True(t.Update(4100)); Assert.False(t.Update(4070));      // −20 mV sous le maximum : retirée
        Assert.True(new ZowieDriver.ChargeTrend().Update(4300));        // démarrage au-dessus de 4,25 V
    }

    [Fact]
    public void PercentFollowsTheLedThresholds()
    {
        Assert.Equal(100, ZowieDriver.Percent(4300)); Assert.Equal(50, ZowieDriver.Percent(3890)); Assert.Equal(10, ZowieDriver.Percent(3680)); Assert.Equal(0, ZowieDriver.Percent(3000));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(102);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); ZowieDriver.ParseCw(r); var d = ZowieDriver.ParseDw(r); if (d != null) Assert.InRange(ZowieDriver.Percent(d.Value), 0, 100); }
    }
}
