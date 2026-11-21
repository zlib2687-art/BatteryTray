using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class PulsarDriverTests
{
    [Fact]
    public void RequestChecksumAndRepliesAreHandled()
    {
        var q = PulsarDriver.BuildRequest();
        Assert.Equal(65, q.Length); Assert.Equal(0x08, q[2]); Assert.Equal(0x81, q[3]);
        int sum = 0; for (int i = 1; i <= 62; i++) sum += q[i]; Assert.Equal((byte)(sum & 0xFF), q[63]); Assert.Equal((byte)(sum >> 8), q[64]);
        var r = new byte[65]; r[1] = 1; r[2] = 8; r[3] = 0x81; r[4] = 1; r[7] = 64;
        Assert.Equal((PulsarDriver.Step.Battery, 64), PulsarDriver.ParseReply(r));
        r[7] = 0xFF; Assert.Equal(PulsarDriver.Step.NoReading, PulsarDriver.ParseReply(r).Result);
        r[7] = 50; r[1] = 2; Assert.Equal(PulsarDriver.Step.Pending, PulsarDriver.ParseReply(r).Result);
        r[1] = 5; Assert.Equal(PulsarDriver.Step.PendingOffline, PulsarDriver.ParseReply(r).Result);
        r[2] = 9; Assert.Equal(PulsarDriver.Step.NotOurs, PulsarDriver.ParseReply(r).Result);
        Assert.Equal(PulsarDriver.Step.NoReading, PulsarDriver.ParseReply(new byte[10]).Result);
    }
}

public class RawmDriverTests
{
    static byte[] EventFrame(string json)
    {
        var body = System.Text.Encoding.Latin1.GetBytes(json + "\0");
        int len = body.Length + 2;
        return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)(RawMsg(0x02, len)), (byte)(len & 0xFF) }.Concat(body).ToArray();
    }
    static int RawMsg(int cmd, int len) => cmd | ((len >> 4) & 0xF0);

    static void FeedAll(RawmDriver.Link link, byte[] stream, int channel = -1)
    {
        for (int i = 0; i < stream.Length; i += 20)
        {
            var chunk = stream[i..Math.Min(stream.Length, i + 20)];
            var pkt = new List<byte>();
            if (channel >= 0) pkt.Add((byte)(0xC0 | channel));
            pkt.Add((byte)(0x80 | chunk.Length)); pkt.AddRange(chunk);
            link.Feed(pkt.ToArray());
        }
    }

    [Fact]
    public void PacketsAndQueryAreBuilt()
    {
        Assert.Equal(new byte[] { 0, 0x84, 0x0E, 4, 0, 0 }, RawmDriver.Packet(RawmDriver.PingEvent()));
        Assert.Equal(new byte[] { 0, 0xC0, 0x84, 0x0E, 4, 0, 0 }, RawmDriver.Packet(RawmDriver.PingEvent(), 0));
        var q = RawmDriver.QueryEvent(0x0102030405060708UL); Assert.Equal(13, q.Length); Assert.Equal(13, q[1]); Assert.Equal(0x08, q[5]);
        Assert.True(RawmDriver.IsReceiver(0x2346)); Assert.True(RawmDriver.IsMouse(0x234A)); Assert.False(RawmDriver.IsMouse(0x2346));
    }

    [Fact]
    public void InfoJsonIsReassembledFromChunkedPackets()
    {
        var link = new RawmDriver.Link();
        FeedAll(link, EventFrame("{\"dn\":\"Leviathan V4\",\"pi\":9034,\"battery\":72,\"chr\":1}"), channel: 0);
        var st = link.Channel0.Status;
        Assert.True(st.HaveInfo); Assert.Equal(72, st.Percent); Assert.True(st.Charging);
        Assert.Equal("Leviathan V4", RawmDriver.JsonString(st.Json, "dn")); Assert.True(RawmDriver.JsonInt(st.Json, "pi", out long pi)); Assert.Equal(9034, pi);
        Assert.False(link.Direct.Status.HaveInfo);                                   // rien sur le flot direct
        var l2 = new RawmDriver.Link(); FeedAll(l2, EventFrame("{\"battery\":101}")); Assert.Equal(-1, l2.Direct.Status.Percent);
    }

    [Fact]
    public void NotificationsUpdateTheBattery()
    {
        var link = new RawmDriver.Link();
        var notify = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0B, 5, 0x17, 55, 0 };
        FeedAll(link, notify);
        Assert.Equal(55, link.Direct.Status.Percent); Assert.False(link.Direct.Status.Charging);
        FeedAll(link, new byte[] { 0x01, 0x02 }); // flot sans en-tête : ignoré
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(181); var link = new RawmDriver.Link();
        for (int i = 0; i < 20_000; i++) { var r = new byte[rnd.Next(0, 65)]; rnd.NextBytes(r); link.Feed(r); Assert.InRange(link.Direct.Status.Percent, -1, 100); RawmDriver.JsonInt("{\"a\":" + i + "x", "a", out _); }
    }
}
