using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class KysonaDriverTests
{
    static byte[] Reply(byte cmd, int percentOrOnline, int charging = 0)
    {
        var r = new byte[17];
        r[0] = 0x08; r[1] = cmd; r[6] = (byte)percentOrOnline; r[7] = (byte)charging;
        r[16] = CorsairDriver.CompxChecksum(r);
        return r;
    }

    [Fact]
    public void ConnectedMouseReportsItsLevelAndCharging()
    {
        var p = KysonaDriver.Parse(Reply(3, 1), Reply(4, 64, 1));
        Assert.True(p.Answered); Assert.True(p.Connected); Assert.Equal(64, p.Percent); Assert.True(p.Charging); Assert.False(p.Asleep);
    }

    [Fact]
    public void MouseOffTheReceiverIsNotConnected()
    {
        var p = KysonaDriver.Parse(Reply(3, 0), null);
        Assert.True(p.Answered); Assert.False(p.Connected); Assert.Equal(-1, p.Percent);
    }

    [Fact]
    public void OnlineButSilentMouseIsAsleepAndKeepsNoValue()
    {
        var p = KysonaDriver.Parse(Reply(3, 1), null);
        Assert.True(p.Answered); Assert.True(p.Connected); Assert.True(p.Asleep); Assert.Equal(-1, p.Percent);
    }

    [Fact]
    public void BatteryQueryAloneIsEnoughWhenOnlineCommandIsUnsupported()
    {
        var p = KysonaDriver.Parse(null, Reply(4, 90));
        Assert.True(p.Answered); Assert.True(p.Connected); Assert.Equal(90, p.Percent);
        Assert.False(KysonaDriver.Parse(null, null).Answered);
        Assert.Equal(-1, KysonaDriver.Parse(null, Reply(4, 200)).Percent); // niveau invalide : inconnu
        Assert.False(KysonaDriver.Parse(new byte[5], new byte[5]).Answered);  // trames trop courtes
    }

    [Theory]
    [InlineData("Pulsar Wireless Dongle", 0x3710, 0x5406, "Pulsar Wireless")]
    [InlineData("Hitscan USB Receiver", 0x3770, 0x0200, "Hitscan Hyperlight")]
    [InlineData("2.4G Wireless Receiver", 0x3554, 0xF55D, "Redragon wireless mouse")]
    [InlineData("VXE NordicMouse 1K Dongle", 0x3554, 0xF58A, "VXE Dragonfly R1")]
    [InlineData("anything", 0x35BB, 0xD3FB, "HECATE mouse")]
    [InlineData("anything", 0x33E4, 0x3608, "G-Wolves Fenrir Pro")]
    [InlineData("", 0x373B, 0x1000, "ATK wireless mouse")]
    [InlineData("", 0x3554, 0x1000, "Wireless mouse")]
    public void NamesAreCleanedPerVendor(string raw, int vid, int pid, string expected) =>
        Assert.Equal(expected, KysonaDriver.CleanName(raw, (ushort)vid, (ushort)pid));

    [Fact]
    public void VxeCableAndReceiverShareOneIdentity()
    {
        Assert.Equal(0xF58A, KysonaDriver.KeyPid(0x3554, 0xF58C));
        Assert.Equal(0xF58E, KysonaDriver.KeyPid(0x3554, 0xF58F));
        Assert.Equal(0xF505, KysonaDriver.KeyPid(0x3554, 0xF506));
        Assert.Equal(0x1234, KysonaDriver.KeyPid(0x3554, 0x1234));
        Assert.True(KysonaDriver.IsCable(0x3554, 0xF58C));
        Assert.False(KysonaDriver.IsCable(0x3554, 0xF58A));
        Assert.True(KysonaDriver.IsCable(0x35BB, 0xD3FA));
        Assert.True(KysonaDriver.IsCable(0x33E4, 0x3608));
    }

    [Theory]
    [InlineData(0x373B, 0x11CA, false)]  // casque ATK N9 : protocole série
    [InlineData(0x373B, 0x1178, false)]  // récepteur 8K Nano : protocole Holtek
    [InlineData(0x3554, 0xFA09, false)]  // récepteur de claviers « BY »
    [InlineData(0x3554, 0xFB40, false)]
    [InlineData(0x35BB, 0xA009, false)]  // casque HECATE
    [InlineData(0x35BB, 0xD3FB, true)]   // souris HECATE
    [InlineData(0x3554, 0xF58A, true)]
    [InlineData(0x3710, 0x5406, true)]
    public void MouseFramesAreNeverSentToKeyboardsHeadsetsOrOtherProtocols(int vid, int pid, bool probe) =>
        Assert.Equal(probe, KysonaDriver.ShouldProbe((ushort)vid, (ushort)pid));

    [Fact]
    public void ParserSurvivesRandomFrames()
    {
        var rnd = new Random(5);
        for (int i = 0; i < 20_000; i++)
        {
            var a = new byte[rnd.Next(0, 30)]; rnd.NextBytes(a);
            var b = new byte[rnd.Next(0, 30)]; rnd.NextBytes(b);
            var p = KysonaDriver.Parse(i % 3 == 0 ? null : a, i % 5 == 0 ? null : b);
            Assert.InRange(p.Percent, -1, 100);
            KysonaDriver.CleanName(new string((char)rnd.Next(32, 400), rnd.Next(0, 30)), (ushort)rnd.Next(0, 0xFFFF), (ushort)rnd.Next(0, 0xFFFF));
        }
    }
}
