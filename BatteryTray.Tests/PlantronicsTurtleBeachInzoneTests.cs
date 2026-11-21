using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class PlantronicsDriverTests
{
    [Fact]
    public void FramesAndBatteryAreDecoded()
    {
        var f = PlantronicsDriver.Frame(0x02, 0x0A, 0x1A, 0x20);
        Assert.Equal(64, f.Length); Assert.Equal(0x06, f[4]); Assert.Equal(0x20, f[5]);
        Assert.Equal(0x07, PlantronicsDriver.Frame(0x01, 1, 2, 0)[4]);
        var r = new byte[64]; r[0] = 7; r[1] = 1; r[2] = 1; r[3] = 0x10; r[5] = 0x02; r[8] = 0x03; r[9] = 0x0A; r[10] = 0x1A; r[11] = 3; r[12] = 5; r[13] = 1;
        Assert.True(PlantronicsDriver.IsReply(r, 0x03, 0x0A, 0x1A, 0x02)); Assert.False(PlantronicsDriver.IsReply(r, 0x03, 0x0A, 0x1A, 0x00));
        Assert.Equal((75, true), PlantronicsDriver.ParseBattery(r)!.Value);   // niveau 3 sur 5 → 3 × 100 / 4
        r[12] = 1; Assert.Null(PlantronicsDriver.ParseBattery(r)); r[12] = 5; r[11] = 5; Assert.Null(PlantronicsDriver.ParseBattery(r));
        Assert.Null(PlantronicsDriver.ParseBattery(new byte[10]));
    }
}

public class TurtleBeachDriverTests
{
    [Fact]
    public void GsiDocumentIsParsedAcrossReports()
    {
        Assert.Equal(62, TurtleBeachDriver.BuildSend(TurtleBeachDriver.EnableCoap).Length);
        Assert.Equal(14, TurtleBeachDriver.BuildSend(TurtleBeachDriver.GetGsi)[1]);
        Assert.Equal(-1, TurtleBeachDriver.FindLevel("{\"OR\":\"GSI\",\"KVP\":{\"240\":\"5"));           // coupé
        Assert.Equal(58, TurtleBeachDriver.FindLevel("{\"OR\":\"GSI\",\"KVP\":{\"1\":\"2\",\"240\":\"58\",\"250\":\"0\"}}"));
        Assert.Equal(-1, TurtleBeachDriver.FindLevel("{\"OR\":\"GSI\",\"KVP\":{\"240\":\"101\"}}"));
        Assert.Equal(-1, TurtleBeachDriver.FindLevel("{\"KVP\":{\"240\":\"50\"}}"));                       // pas un GSI
        var r = new byte[62]; r[0] = 7; r[1] = 4; r[3] = (byte)'{'; r[4] = (byte)'"'; r[5] = (byte)'O'; r[6] = (byte)'R';
        Assert.Equal("{\"OR", TurtleBeachDriver.Payload(r)); Assert.Equal("", TurtleBeachDriver.Payload(new byte[] { 6, 1, 0, 65 }));
        Assert.Equal("Turtle Beach Stealth 700X Gen 3", TurtleBeachDriver.ModelName("Stealth 700X Gen 3")); Assert.Equal("Turtle Beach X", TurtleBeachDriver.ModelName("turtle beach X"));
    }
}

public class SonyInzoneDriverTests
{
    static byte[] Event(ushort tid, byte type, params byte[] payload)
    {
        int len = 12 + payload.Length;
        var b = new byte[64]; b[0] = 2; b[1] = (byte)len; b[2] = 4; b[3] = 0xFF; b[5] = 0; b[6] = 0x96; b[7] = 0xC3; b[8] = 0x14; b[9] = 4; b[10] = type; b[11] = (byte)tid; b[12] = (byte)(tid >> 8);
        payload.CopyTo(b, 13);
        int sum = 0; for (int i = 5; i <= len; i++) sum += b[i]; b[len + 1] = (byte)sum; return b;
    }

    [Fact]
    public void EventsAreValidatedAndDecoded()
    {
        var e = SonyInzoneDriver.ParseEvent(Event(5, 0x10, 1, 80))!;
        Assert.Equal(5, e.Tid); Assert.Equal(new byte[] { 1, 80 }, e.Payload);
        var bad = Event(5, 0x10, 1, 80); bad[14] ^= 1; Assert.Null(SonyInzoneDriver.ParseEvent(bad));          // somme fausse
        var toHeadset = Event(5, 0x10, 1, 80); toHeadset[8] = 0x41; Assert.Null(SonyInzoneDriver.ParseEvent(toHeadset));
        Assert.Null(SonyInzoneDriver.ParseEvent(new byte[10]));
        var q = SonyInzoneDriver.BuildRequest(0x0102); Assert.Equal(0x02, q[11]); Assert.Equal(0x01, q[12]);
        int sum = 0; for (int i = 6; i <= 12; i++) sum += q[i]; Assert.Equal((byte)sum, q[13]);
    }

    [Fact]
    public void HeadsetAndBudsPayloads()
    {
        Assert.Equal((true, 80, true), SonyInzoneDriver.ParseHeadset(new byte[] { 1, 80 }));
        Assert.Equal((false, -1, false), SonyInzoneDriver.ParseHeadset(new byte[] { 0, 0xFF }));
        var b = SonyInzoneDriver.ParseBuds(new byte[] { 0, 60, 0, 70, 0, 90 });
        Assert.Equal((true, 60), (b.Connected, b.Percent)); Assert.Equal("L 60%, R 70%, case 90%", b.Detail);
        var one = SonyInzoneDriver.ParseBuds(new byte[] { 0, 0xFF, 0, 70, 0, 0xFF }); Assert.Equal(70, one.Percent); Assert.Equal("L -, R 70%", one.Detail);
        Assert.False(SonyInzoneDriver.ParseBuds(new byte[] { 0, 0xFF, 0, 0xFF, 0, 0xFF }).Connected);
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(161);
        for (int i = 0; i < 30_000; i++) { var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r); if (r.Length > 8 && i % 2 == 0) { r[0] = 2; r[1] = (byte)rnd.Next(12, 40); r[2] = 4; r[3] = 0xFF; r[5] = 0; r[6] = 0x96; r[7] = 0xC3; r[8] = 0x14; } SonyInzoneDriver.ParseEvent(r); }
    }
}
