using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

/// <summary>Pilotes Razer BlackShark et LAMZU (portés de BatteryHub) : trames synthétiques.</summary>
public class RazerLamzuDriverTests
{
    static byte[] F(int len, params (int I, int V)[] b) { var f = new byte[len]; foreach (var (i, v) in b) f[i] = (byte)v; return f; }

    // ------------------------------------------------------------------ BlackShark V3
    [Fact]
    public void V3PacketLayoutAndChecksum()
    {
        var p = RazerHeadsetDriver.BuildV3Packet(0x80, 0x21);
        Assert.Equal(64, p.Length);
        Assert.Equal(0x02, p[0]); Assert.Equal(0x60, p[2]); Assert.Equal(0x04, p[6]); Assert.Equal(0x80, p[9]); Assert.Equal(0x21, p[10]);
        byte x = 0; for (int i = 0; i < 62; i++) x ^= p[i];
        Assert.Equal(x, p[62]);
    }

    [Fact]
    public void V3ReplyIsParsedWithCableAndWirelessIdentities()
    {
        var reply = F(20, (0, 2), (2, 0x60), (10, 0x21), (13, 82), (14, 0));
        Assert.True(RazerHeadsetDriver.IsV3Reply(reply, 0x21));
        Assert.False(RazerHeadsetDriver.IsV3Reply(reply, 0x2A));
        var s = RazerHeadsetDriver.ParseV3(0x057A, reply, F(20, (13, 1)))!;
        Assert.Equal(82, s.Percent); Assert.True(s.Charging); Assert.Equal("2.4 GHz receiver", s.Via); Assert.Equal("Razer BlackShark V3", s.Name);
        Assert.Equal("USB cable", RazerHeadsetDriver.ParseV3(0x0576, reply, null)!.Via);
        Assert.Equal("Razer BlackShark V3 Pro for Xbox", RazerHeadsetDriver.ParseV3(0x0A55, reply, null)!.Name);
        Assert.Null(RazerHeadsetDriver.ParseV3(0x057A, F(20, (13, 150)), null));
        Assert.Null(RazerHeadsetDriver.ParseV3(0x057A, new byte[5], null));
    }

    // ------------------------------------------------------------------ V3 X / V2 HyperSpeed / V2 Pro / PA
    [Fact]
    public void V3xRequestAndReplyAreChecked()
    {
        var req = RazerHeadsetDriver.BuildV3xRequest(0x41, 0x80);
        Assert.Equal(0x07, req[0]); Assert.Equal(0x41, req[2]); Assert.Equal(0x07, req[7]); Assert.Equal(0x80, req[8]);
        var reply = F(64, (1, 2), (2, 0x41), (6, 2), (7, 7), (8, 0x80), (10, 64));
        var ok = RazerHeadsetDriver.ParseV3xReply(reply, 0x41, 0x80);
        Assert.True(ok.Matches); Assert.True(ok.Success); Assert.Equal(64, ok.Value);
        Assert.False(RazerHeadsetDriver.ParseV3xReply(reply, 0x42, 0x80).Matches);  // autre transaction
        var failed = RazerHeadsetDriver.ParseV3xReply(F(64, (1, 5), (2, 0x41), (6, 2), (7, 7), (8, 0x80)), 0x41, 0x80);
        Assert.True(failed.Matches); Assert.False(failed.Success);                   // casque éteint
        Assert.False(RazerHeadsetDriver.ParseV3xReply(new byte[10], 0x41, 0x80).Matches);
    }

    [Fact]
    public void HyperSpeedReplyNeedsMatchingSequenceCommandAndChecksum()
    {
        byte seq = 0x63;
        var reply = F(64, (0, 2), (2, seq), (10, 0x21), (12, 1), (13, 77));
        byte x = 0; for (int i = 1; i < 62; i++) x ^= reply[i];
        reply[62] = x;
        Assert.Equal(77, RazerHeadsetDriver.ParseHsReply(reply, 0x21, seq));
        Assert.Equal(-1, RazerHeadsetDriver.ParseHsReply(reply, 0x21, 0x64));
        Assert.Equal(-1, RazerHeadsetDriver.ParseHsReply(reply, 0x2A, seq));
        reply[13] = 78;
        Assert.Equal(-1, RazerHeadsetDriver.ParseHsReply(reply, 0x21, seq)); // somme de contrôle fausse
        Assert.Equal(-1, RazerHeadsetDriver.ParseHsReply(new byte[10], 0x21, seq));
        var q = RazerHeadsetDriver.BuildHsQuery(0x21, seq);
        byte y = 0; for (int i = 0; i < 62; i++) y ^= q[i];
        Assert.Equal(y, q[62]);
    }

    [Fact]
    public void V2Pro2020FreshStaleAndChargingReplies()
    {
        byte[] Fresh(int mvHi, int mvLo, int state, int pct) =>
            new byte[] { 0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05, 0, 0, (byte)state, (byte)mvHi, (byte)mvLo, (byte)pct }.Concat(new byte[49]).ToArray();
        var ok = RazerHeadsetDriver.ParseV2Pro2020(Fresh(0x0F, 0xA0, 0x03, 64), false)!;
        Assert.True(ok.Connected); Assert.Equal(64, ok.Percent); Assert.False(ok.Charging);
        var charging = RazerHeadsetDriver.ParseV2Pro2020(Fresh(0x0F, 0xA0, 0x09, 80), false)!;
        Assert.True(charging.Charging); Assert.Equal(-1, charging.Percent); // le récepteur saute à 80 % en charge : niveau inconnu
        var stale = new byte[] { 0xFF, 0x01, 0x00, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05 }.Concat(new byte[55]).ToArray();
        Assert.Null(RazerHeadsetDriver.ParseV2Pro2020(stale, false));          // on attend la réponse fraîche
        Assert.False(RazerHeadsetDriver.ParseV2Pro2020(stale, true)!.Connected); // dernière lecture : casque éteint
        Assert.Null(RazerHeadsetDriver.ParseV2Pro2020(new byte[10], true));
    }

    [Fact]
    public void PaEnvelopeFramesAndReplies()
    {
        var on = RazerHeadsetDriver.BuildPaFrame(0x07, 0x0E, 0x02, 0xE1, 1);
        Assert.Equal((byte)'P', on[5]); Assert.Equal((byte)'A', on[6]); Assert.Equal(0xE1, on[10]); Assert.Equal(1, on[11]);
        var reply = F(20, (0, 2), (12, 0x21), (13, 1), (14, 1), (15, 73));
        Assert.Equal(73, RazerHeadsetDriver.ParsePaReply(reply, 0x21));
        Assert.Equal(-1, RazerHeadsetDriver.ParsePaReply(reply, 0x2A));
        Assert.Equal(-1, RazerHeadsetDriver.ParsePaReply(F(20, (0, 2), (12, 0x21), (13, 0), (14, 1)), 0x21)); // pas d'accusé
        Assert.Equal(-1, RazerHeadsetDriver.ParsePaReply(new byte[8], 0x21));
    }

    // ------------------------------------------------------------------ LAMZU
    [Fact]
    public void LamzuReplyIsReadWithAndWithoutTheReportIdByte()
    {
        var withId = F(12, (1, 0xA1), (4, 2), (6, 0x83), (7, 1), (8, 55));
        var p = LamzuDriver.ParseReply(withId)!.Value; Assert.Equal(1, p.Charging); Assert.Equal(55, p.Percent);
        var without = F(12, (0, 0xA1), (3, 2), (5, 0x83), (6, 0), (7, 91));
        var q = LamzuDriver.ParseReply(without)!.Value; Assert.Equal(0, q.Charging); Assert.Equal(91, q.Percent);
        Assert.Null(LamzuDriver.ParseReply(F(12, (1, 0xA1), (4, 2), (6, 0x83), (8, 200))));  // niveau invalide
        Assert.Null(LamzuDriver.ParseReply(F(12, (1, 0xB1))));                                // autre protocole
        Assert.Null(LamzuDriver.ParseReply(new byte[4]));
        var req = LamzuDriver.BuildRequest();
        Assert.Equal(65, req.Length); Assert.Equal(0x02, req[3]); Assert.Equal(0x02, req[4]); Assert.Equal(0x83, req[6]);
    }

    [Fact]
    public void LamzuStatusDistinguishesDongleOffFromCable()
    {
        var off = LamzuDriver.ToStatus(0x37B0, 0x0012, 0, 0);       // récepteur, 0 % : souris éteinte
        Assert.False(off.Connected); Assert.Equal(-1, off.Percent); Assert.Equal("LAMZU wireless mouse", off.Name);
        var cable = LamzuDriver.ToStatus(0x37B0, 0x0005, 1, 0);     // câble : toujours connectée
        Assert.True(cable.Connected); Assert.Equal("USB cable", cable.Via); Assert.True(cable.Charging); Assert.Equal("LAMZU Tachi", cable.Name);
        var on = LamzuDriver.ToStatus(0x373E, 0x001D, 0, 80);
        Assert.Equal(80, on.Percent); Assert.Equal("LAMZU Maya X", on.Name); Assert.Equal("2.4 GHz receiver", on.Via);
        Assert.True(LamzuDriver.IsCable(0x373E, 0x001C)); Assert.False(LamzuDriver.IsCable(0x373E, 0x001D));
        Assert.Null(LamzuDriver.ModelName(0x1234, 0x0001));
    }

    [Fact]
    public void ParsersSurviveRandomFrames()
    {
        var rnd = new Random(12);
        for (int i = 0; i < 20_000; i++)
        {
            var a = new byte[rnd.Next(0, 80)]; rnd.NextBytes(a);
            ushort pid = (ushort)rnd.Next(0, 0xFFFF);
            var v3 = RazerHeadsetDriver.ParseV3(pid, a, a); if (v3 != null) Assert.InRange(v3.Percent, -1, 100);
            RazerHeadsetDriver.ParseV3xReply(a, (byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255));
            Assert.InRange(RazerHeadsetDriver.ParseHsReply(a, 0x21, (byte)rnd.Next(0, 255)), -1, 255);
            var v2 = RazerHeadsetDriver.ParseV2Pro2020(a, i % 2 == 0); if (v2 != null) Assert.InRange(v2.Percent, -1, 100);
            Assert.InRange(RazerHeadsetDriver.ParsePaReply(a, (byte)rnd.Next(0, 255)), -1, 255);
            var l = LamzuDriver.ParseReply(a); if (l != null) Assert.InRange(l.Value.Percent, 0, 100);
        }
    }
}
