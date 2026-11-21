using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class NuphyDriverTests
{
    static byte[] Reply(int value)
    {
        var r = new byte[64]; r[0] = 0xAA; r[1] = 5; r[4] = 1; r[5] = 0x20; r[8] = (byte)value;
        int sum = 0; for (int i = 4; i < 64; i++) sum += r[i]; r[3] = (byte)sum; return r;
    }

    [Fact]
    public void ReplyIsValidatedAndDecoded()
    {
        Assert.Equal((70, false), NuphyDriver.ParseReply(Reply(70))!.Value);
        Assert.Equal((30, true), NuphyDriver.ParseReply(Reply(0x80 | 30))!.Value);
        Assert.Null(NuphyDriver.ParseReply(Reply(101)));
        var bad = Reply(50); bad[3] ^= 1; Assert.Null(NuphyDriver.ParseReply(bad));         // somme fausse
        var other = Reply(50); other[1] = 6; Assert.Null(NuphyDriver.ParseReply(other));
        Assert.Null(NuphyDriver.ParseReply(new byte[63]));
        Assert.Equal(9, NuphyDriver.BuildRequest().Length);
    }
}

public class NzxtDriverTests
{
    [Fact]
    public void TelemetryIsDecodedAndAcksSkipped()
    {
        var r = new byte[64]; r[0] = 0x4E; r[1] = 2; r[2] = 0x97; r[8] = 0x0F; r[9] = 0xA0; r[17] = 64; r[22] = 1;
        var t = NzxtDriver.ParseTelemetry(r)!.Value;
        Assert.Equal((64, 4000, true), (t.Percent, t.Millivolts, t.Charging));
        r[17] = 200; Assert.Null(NzxtDriver.ParseTelemetry(r));                              // pas un pourcentage
        var ack = new byte[64]; ack[0] = 0x4E; ack[1] = 0xE5; Assert.Null(NzxtDriver.ParseTelemetry(ack));
        Assert.Null(NzxtDriver.ParseTelemetry(new byte[20]));
        Assert.True(NzxtDriver.PlausibleMv(4000)); Assert.False(NzxtDriver.PlausibleMv(100));
        var q = NzxtDriver.BuildRequest(); Assert.Equal(65, q.Length); Assert.Equal(0x4E, q[1]); Assert.Equal(0xB0, q[5]);
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(131);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 2 && i % 2 == 0) { r[0] = 0x4E; r[1] = 2; r[2] = 0x97; } var t = NzxtDriver.ParseTelemetry(r); if (t != null) Assert.InRange(t.Value.Percent, 0, 100); }
    }
}

public class RapooDriverTests
{
    [Fact]
    public void DocumentedFramesAreDecoded()
    {
        var rf = Convert.FromHexString("BBB0718403" + "0164");                       // VT1 Pro Max, récepteur : 100 %
        var a = RapooDriver.DecodeBattery(rf, RapooDriver.Profile.LegacyVT3Pro)!;
        Assert.Equal((100, false), (a.Percent, a.Charging));
        var cable = Convert.FromHexString("BBB0718403" + "025F");
        var b = RapooDriver.DecodeBattery(cable, RapooDriver.Profile.LegacyVT3Pro)!;
        Assert.Equal((95, true), (b.Percent, b.Charging));
        var unknown = Convert.FromHexString("BBB0718403" + "027F");                 // 7F en charge : niveau inconnu
        Assert.Equal(-1, RapooDriver.DecodeBattery(unknown, RapooDriver.Profile.LegacyVT3Pro)!.Percent);
        Assert.Null(RapooDriver.DecodeBattery(Convert.FromHexString("BBB0718403" + "017F"), RapooDriver.Profile.LegacyVT3Pro));
        Assert.Null(RapooDriver.DecodeBattery(Convert.FromHexString("BBB0718403" + "0064"), RapooDriver.Profile.LegacyVT3Pro)); // état 0 : transition
    }

    [Fact]
    public void Nordic54LFramesCarryConnectionAndMousePid()
    {
        var r = new byte[16]; r[0] = 7; r[1] = 0x02; r[7] = 1; r[8] = 55; r[13] = 0x17; r[14] = 0x46;
        var b = RapooDriver.DecodeBattery(r)!;
        Assert.Equal((55, false, RapooDriver.Connection.Cable, (ushort)0x4617), (b.Percent, b.Charging, b.Connection, b.MousePid));
        var short13 = new byte[13]; short13[0] = 7; short13[7] = 2; short13[8] = 20;
        var s = RapooDriver.DecodeBattery(short13)!; Assert.Equal((20, true, RapooDriver.Connection.Receiver, (ushort)0), (s.Percent, s.Charging, s.Connection, s.MousePid));
        r[1] = 3; Assert.Null(RapooDriver.DecodeBattery(r));                          // mode invalide
        r[1] = 0; r[8] = 150; Assert.Null(RapooDriver.DecodeBattery(r));
        Assert.Null(RapooDriver.DecodeBattery(new byte[5])); Assert.Null(RapooDriver.DecodeBattery(Array.Empty<byte>()));
        Assert.Null(RapooDriver.DecodeBattery(Convert.FromHexString("BBB0718403" + "0164")));  // BB refusé avec le profil Nordic
    }

    [Fact]
    public void ModelsAndCollections()
    {
        Assert.Equal("Rapoo VT1 Pro Max", RapooDriver.FindModel(0x1233)!.Name); Assert.Equal("Rapoo VT3", RapooDriver.FindModel(0x4612)!.Name); Assert.Null(RapooDriver.FindModel(1));
        Assert.True(RapooDriver.IsLegacyVT3Pro(0x1244)); Assert.False(RapooDriver.IsLegacyVT3Pro(0x1412));
        Assert.True(RapooDriver.IsBatteryCollection(new HidColl { Page = 0xFF00, Usage = 2, InLen = 16 }, 0x1412));
        Assert.True(RapooDriver.IsBatteryCollection(new HidColl { Page = 0xFF00, Usage = 2, InLen = 13 }, 0x1417));
        Assert.False(RapooDriver.IsBatteryCollection(new HidColl { Page = 0xFF00, Usage = 2, InLen = 13 }, 0x1412));
        Assert.False(RapooDriver.IsBatteryCollection(new HidColl { Page = 0xFF00, Usage = 1, InLen = 16 }, 0x1412));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(132);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 24)]; rnd.NextBytes(r); if (r.Length > 0 && i % 2 == 0) r[0] = (byte)(i % 4 == 0 ? 7 : 0xBB);
            foreach (var p in new[] { RapooDriver.Profile.Nordic54L, RapooDriver.Profile.LegacyVT3Pro }) { var b = RapooDriver.DecodeBattery(r, p); if (b != null) Assert.InRange(b.Percent, -1, 100); }
        }
    }
}
