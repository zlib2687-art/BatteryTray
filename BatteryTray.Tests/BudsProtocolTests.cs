using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class GalaxyBudsProtocolTests
{
    static byte[] Frame(byte id, byte[] payload, bool legacy = false)
    {
        var body = new byte[] { id }.Concat(payload).ToArray();
        ushort crc = GalaxyBudsProtocol.Crc16(body, 0, body.Length);
        int len = body.Length + 2;
        var f = new List<byte>();
        if (legacy) { f.Add(0xFE); f.Add(0); f.Add((byte)len); }
        else { f.Add(0xFD); f.Add((byte)(len & 0xFF)); f.Add((byte)(len >> 8)); }
        f.AddRange(body); f.Add((byte)(crc & 0xFF)); f.Add((byte)(crc >> 8)); f.Add(legacy ? (byte)0xEE : (byte)0xDD);
        return f.ToArray();
    }

    [Fact]
    public void Crc16MatchesTheXmodemCheckValue() => Assert.Equal(0x31C3, GalaxyBudsProtocol.Crc16(System.Text.Encoding.ASCII.GetBytes("123456789"), 0, 9));

    [Fact]
    public void ExtendedAndPlainStatusFramesAreDecoded()
    {
        var ext = Frame(0x61, new byte[] { 0, 0, 80, 75, 0, 0, 0, 60 });
        var s = GalaxyBudsProtocol.FindStatus(ext, false)!; Assert.Equal((80, 75, 60), (s.Left, s.Right, s.CaseLevel));
        var plain = Frame(0x60, new byte[] { 0, 90, 85, 0, 0, 0, 101 });
        var p = GalaxyBudsProtocol.FindStatus(plain, false)!; Assert.Equal((90, 85, -1), (p.Left, p.Right, p.CaseLevel));          // 101 = boîtier inconnu
        var legacy = Frame(0x61, new byte[] { 0, 0, 40, 0 }, legacy: true);
        var l = GalaxyBudsProtocol.FindStatus(legacy, true)!; Assert.Equal((40, -1, -1), (l.Left, l.Right, l.CaseLevel));            // 0 = non connecté
        var noise = new byte[] { 1, 2, 3 }.Concat(ext).Concat(new byte[] { 9 }).ToArray(); Assert.NotNull(GalaxyBudsProtocol.FindStatus(noise, false));
        var bad = (byte[])ext.Clone(); bad[6] ^= 1; Assert.Null(GalaxyBudsProtocol.FindStatus(bad, false));                            // CRC faux
        Assert.Null(GalaxyBudsProtocol.FindStatus(new byte[5], false));
    }

    [Fact]
    public void SummaryShowsTheLowerEarbud()
    {
        var s = GalaxyBudsDriver.Summarise(80, 75, 60); Assert.Equal(75, s.Percent); Assert.Equal("L 80%, R 75%, case 60%", s.Detail);
        Assert.Equal(80, GalaxyBudsDriver.Summarise(80, -1, -1).Percent); Assert.Equal("L 80%, R -", GalaxyBudsDriver.Summarise(80, -1, -1).Detail);
        Assert.True(GalaxyBudsDriver.LooksLikeBuds("Galaxy Buds2 Pro")); Assert.False(GalaxyBudsDriver.LooksLikeBuds("Headset"));
    }

    [Fact]
    public void ParserSurvivesRandomStreams()
    {
        var rnd = new Random(231);
        for (int i = 0; i < 20_000; i++) { var r = new byte[rnd.Next(0, 60)]; rnd.NextBytes(r); if (r.Length > 6 && i % 2 == 0) r[0] = 0xFD; GalaxyBudsProtocol.FindStatus(r, i % 3 == 0); NothingEarProtocol.FindStatus(r); }
    }
}

public class NothingEarProtocolTests
{
    static byte[] Reply(ushort cmd, params byte[] pairs)
    {
        var payload = new byte[] { (byte)(pairs.Length / 2) }.Concat(pairs).ToArray();
        var f = new List<byte> { 0x55, 0x60, 0x01, (byte)cmd, (byte)(cmd >> 8), (byte)payload.Length, 0, 1 }; f.AddRange(payload);
        ushort crc = NothingEarProtocol.Crc16Modbus(f.ToArray(), 0, f.Count); f.Add((byte)crc); f.Add((byte)(crc >> 8));
        return f.ToArray();
    }

    [Fact]
    public void QueryAndRepliesFollowTheFraming()
    {
        var q = NothingEarProtocol.BuildBatteryQuery(1);
        Assert.Equal(10, q.Length); Assert.Equal(0xC007, q[3] | (q[4] << 8)); Assert.Equal(NothingEarProtocol.Crc16Modbus(q, 0, 8), (ushort)(q[8] | (q[9] << 8)));
        Assert.Equal(0x4B37, NothingEarProtocol.Crc16Modbus(System.Text.Encoding.ASCII.GetBytes("123456789"), 0, 9));             // valeur de contrôle CRC-16/MODBUS
        var s = NothingEarProtocol.FindStatus(Reply(0xC007, 0x02, 0x80 | 70, 0x03, 65, 0x04, 90))!;
        Assert.Equal((70, 65, 90, true, false), (s.Left, s.Right, s.CaseLevel, s.LeftCharging, s.RightCharging));
        Assert.NotNull(NothingEarProtocol.FindStatus(Reply(0xE001, 0x02, 50)));                                                   // poussée
        Assert.Null(NothingEarProtocol.FindStatus(Reply(0xC008, 0x02, 50)));                                                      // autre commande
        var bad = Reply(0xC007, 0x02, 50); bad[9] ^= 1; Assert.Null(NothingEarProtocol.FindStatus(bad));
        Assert.Null(NothingEarProtocol.FindStatus(Reply(0xC007, 0x02, 120)));                                                     // niveau impossible
    }

    [Fact]
    public void NamesAreRecognised()
    {
        Assert.True(NothingEarDriver.LooksLikeNothingEar("Nothing Ear (3a)")); Assert.True(NothingEarDriver.LooksLikeNothingEar("CMF Buds Pro")); Assert.True(NothingEarDriver.LooksLikeNothingEar("Ear (2)"));
        Assert.False(NothingEarDriver.LooksLikeNothingEar("Sony WH-1000XM5"));
    }
}
