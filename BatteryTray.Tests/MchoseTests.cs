using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class MchoseDriverTests
{
    static byte[] Inverted(ushort vidField, byte flags, byte level, byte charging)
    {
        var r = new byte[65]; r[0] = 0x12; r[1] = 0x06 ^ 0xFF;
        var p = new byte[11]; p[0] = (byte)vidField; p[1] = (byte)(vidField >> 8); p[8] = flags; p[9] = level; p[10] = charging;
        for (int i = 0; i < 11; i++) r[2 + i] = (byte)(p[i] ^ 0xFF);
        for (int i = 13; i < 65; i++) r[i] = 0xFF;
        return r;
    }

    [Fact]
    public void InvertedPayloadIsDecoded()
    {
        var req = MchoseDriver.BuildRequest(65); Assert.Equal(0x12, req[0]); Assert.Equal(0xF9, req[1]); Assert.Equal(0xFF, req[2]); Assert.Equal(0xFF, req[64]);
        var s = MchoseDriver.ParseStatus(Inverted(0x5253, 9, 66, 0), 0x5253, 0x5253); Assert.Equal((MchoseDriver.Kind.Status, 66, false, 9), s);
        Assert.Equal((MchoseDriver.Kind.Status, 40, true, 0), MchoseDriver.ParseStatus(Inverted(0x41E4, 0, 40, 1), 0x41E4, 0x3837));
        var asleep = new byte[65]; asleep[0] = 0x12; asleep[1] = 0xF9; for (int i = 2; i < 65; i++) asleep[i] = 0xFF;
        Assert.Equal(MchoseDriver.Kind.Asleep, MchoseDriver.ParseStatus(asleep, 0x5253, 0x5253).Result);
        Assert.Equal(MchoseDriver.Kind.Invalid, MchoseDriver.ParseStatus(Inverted(0x1234, 9, 66, 0), 0x5253, 0x5253).Result);   // mauvais vid
        Assert.Equal(MchoseDriver.Kind.Invalid, MchoseDriver.ParseStatus(Inverted(0x5253, 9, 101, 0), 0x5253, 0x5253).Result);
        Assert.Equal(MchoseDriver.Kind.Invalid, MchoseDriver.ParseStatus(new byte[5], 0x5253, 0x5253).Result);
    }
}

public class MchoseA5DriverTests
{
    [Fact]
    public void ReplyAfterTheA1MarkerIsRead()
    {
        var r = new byte[65]; r[0] = 0; r[1] = 0xA1; r[5] = 0; r[6] = 0x83; r[7] = 1; r[8] = 72;        // [id][A1][..][..][..][page][cmd][charge][niveau]
        Assert.Equal((72, true), MchoseA5Driver.Parse(r)!.Value);
        r[8] = 0xFF; Assert.Null(MchoseA5Driver.Parse(r)); r[8] = 200; Assert.Equal(100, MchoseA5Driver.Parse(r)!.Value.Level);
        r[1] = 0xA3; Assert.Null(MchoseA5Driver.Parse(r)); Assert.Null(MchoseA5Driver.Parse(new byte[5]));
        var q = MchoseA5Driver.Request(65); Assert.Equal(0x83, q[6]); Assert.Equal(2, q[3]);
    }
}

public class MchoseG3AndHeadsetTests
{
    [Fact]
    public void G3ReplyIsDecoded()
    {
        var r = new byte[64]; r[0] = 0xAA; r[1] = 0x30; r[8] = 64; r[9] = 1;
        Assert.Equal((64, true), MchoseG3Driver.ParseReply(r)!.Value); r[8] = 0; Assert.Equal(-1, MchoseG3Driver.ParseReply(r)!.Value.Percent); r[0] = 0; Assert.Null(MchoseG3Driver.ParseReply(r));
    }

    [Fact]
    public void HeadsetFrameWithXorChecksum()
    {
        // [55][AA 01 01 03 00 01 0B <body: 01, 0x80|70> <xor>] : drapeaux 1 = somme XOR
        var f = new byte[] { 0xAA, 0x01, 0x01, 0x03, 0x00, 0x01, 0x0B, 0x01, 0x80 | 70 };
        byte x = (byte)(f[2] ^ f[3] ^ f[4] ^ f[5]); for (int i = 0; i < 3; i++) x ^= f[6 + i];
        var r = new byte[] { 0x55 }.Concat(f).Append(x).Concat(new byte[10]).ToArray();
        Assert.Equal((MchoseHeadsetDriver.Reply.Level, 70, true), MchoseHeadsetDriver.ParseBattery(r));
        r[10] ^= 1; Assert.Equal(MchoseHeadsetDriver.Reply.None, MchoseHeadsetDriver.ParseBattery(r).Result);           // somme fausse
        var off = (byte[])new byte[] { 0x55 }.Concat(new byte[] { 0xAA, 0x01, 0x00, 0x03, 0x00, 0x01, 0x0B, 0x00, 0x00 }).Append((byte)0).Concat(new byte[10]).ToArray();
        Assert.Equal(MchoseHeadsetDriver.Reply.Off, MchoseHeadsetDriver.ParseBattery(off).Result);
        Assert.Equal(MchoseHeadsetDriver.Reply.None, MchoseHeadsetDriver.ParseBattery(new byte[4]).Result); Assert.Equal(9, MchoseHeadsetDriver.BuildRequest().Length);
    }
}

public class MchoseKeyboardAndModernTests
{
    [Fact]
    public void FuncConfigChecksumAndBattery()
    {
        var r = new byte[8 + 56]; r[0] = 0xAA; r[1] = 5; r[4] = 56; r[5] = 0; r[6] = 0; r[7] = 0; r[8 + 32] = 0x80 | 85;
        int sum = r[4] + r[5] + r[6] + r[7]; for (int i = 0; i < 56; i++) sum += r[8 + i]; r[3] = (byte)sum;
        Assert.Equal((85, true), MchoseKeyboardDriver.ParseFuncConfig(r)!.Value);
        r[3] ^= 1; Assert.Null(MchoseKeyboardDriver.ParseFuncConfig(r)); Assert.Null(MchoseKeyboardDriver.ParseFuncConfig(new byte[20]));
        Assert.Equal("MCHOSE G75 V2", MchoseKeyboardDriver.DisplayName("MCHOSE G75 V2 2.4G")); Assert.Equal("MCHOSE keyboard", MchoseKeyboardDriver.DisplayName(""));
        Assert.Equal(9, MchoseKeyboardDriver.BuildRequest().Length);
    }

    static byte[] Info(int len, byte link, byte chg, byte pct)
    {
        var r = new byte[64]; r[0] = 0x4D; r[1] = 1; r[2] = 1; r[3] = (byte)len; r[4] = 0x00; r[5] = 0x09;
        r[8] = 0x37; r[9] = 0x38; r[8 + 10] = link; r[8 + 11] = chg; r[8 + 12] = pct;
        r[8 + len] = MchoseModernDriver.Xor(r, 2, 7 + len); return r;
    }

    [Fact]
    public void ModernDeviceInfoIsDecoded()
    {
        var q = MchoseModernDriver.BuildRequest(64); Assert.Equal(0x4D, q[0]); Assert.Equal(MchoseModernDriver.Xor(q, 2, 7), q[8]);
        Assert.Equal((1, 29, false), MchoseModernDriver.ParseInfo(Info(14, 2, 0, 29), false));
        Assert.Equal((1, 25, true), MchoseModernDriver.ParseInfo(Info(14, 1, 1, 25), false));
        Assert.Equal(0, MchoseModernDriver.ParseInfo(Info(14, 0, 0, 0), false).State);                       // non liée
        var off = new byte[64]; off[0] = 0x4D; off[1] = 1; off[2] = 1; off[3] = 1; off[5] = 9; off[8] = 0xFF; Assert.Equal(0, MchoseModernDriver.ParseInfo(off, false).State);
        Assert.Equal(0, MchoseModernDriver.ParseInfo(off, true).State); off[8] = 0; Assert.Equal(-1, MchoseModernDriver.ParseInfo(off, true).State);
        var bad = Info(14, 2, 0, 29); bad[8 + 14] ^= 1; Assert.Equal(-1, MchoseModernDriver.ParseInfo(bad, false).State);
        Assert.Equal(-1, MchoseModernDriver.ParseInfo(Info(14, 2, 0, 101), false).State); Assert.Equal(-1, MchoseModernDriver.ParseInfo(new byte[5], false).State);
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(201);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 6 && i % 2 == 0) { r[0] = 0x4D; r[4] = 0; r[5] = 9; r[3] = (byte)rnd.Next(0, 30); }
            MchoseModernDriver.ParseInfo(r, i % 3 == 0); MchoseKeyboardDriver.ParseFuncConfig(r); MchoseHeadsetDriver.ParseBattery(r); MchoseG3Driver.ParseReply(r); MchoseA5Driver.Parse(r);
            MchoseDriver.ParseStatus(r, 0x5253, 0x5253);
        }
    }
}
