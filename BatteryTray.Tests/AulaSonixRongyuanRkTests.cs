using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class AulaDriverTests
{
    static byte[] Reply(byte cmd, byte level, byte state, bool withChecksum = true)
    {
        var r = new byte[20]; r[0] = 0x13; r[1] = cmd; r[2] = 1; r[5] = level; r[6] = state; if (withChecksum) r[19] = AulaDriver.Sum(r, 19); return r;
    }

    [Fact]
    public void BatteryRepliesAreRecognisedAndDecoded()
    {
        var q = AulaDriver.BuildRequest(0x4A, 1); Assert.Equal(0x4A, q[1]); Assert.Equal(AulaDriver.Sum(q, 19), q[19]);
        Assert.True(AulaDriver.IsReply(Reply(0x4A, 80, 0x10), 0x4A)); Assert.True(AulaDriver.IsReply(Reply(0xCA, 80, 0x10), 0x4A));     // bit haut masqué
        Assert.False(AulaDriver.IsReply(Reply(0x0A, 80, 0), 0x4A));                                                                         // autre commande poussée
        Assert.Equal((80, true), AulaDriver.ParseBattery(Reply(0x4A, 80, 0x10))!.Value); Assert.Equal((55, false), AulaDriver.ParseBattery(Reply(0x4A, 55, 0x01))!.Value);
        Assert.Null(AulaDriver.ParseBattery(Reply(0x4A, 0, 0))); Assert.Null(AulaDriver.ParseBattery(Reply(0x4A, 101, 0)));
        var mchose = Reply(0x4A, 66, 1, withChecksum: false); mchose[3] = 0; Assert.True(AulaDriver.IsReply(mchose, 0x4A));                  // fin à 0, trame unique
        mchose[2] = 2; Assert.False(AulaDriver.IsReply(mchose, 0x4A));
        var echo = new byte[20]; echo[0] = 0x13; echo[1] = 0x4A; echo[19] = AulaDriver.Sum(echo, 19); Assert.False(AulaDriver.IsReply(echo, 0x4A)); // écho de la requête
    }

    [Fact]
    public void ModelCodesAreMatchedAtBothOffsets()
    {
        var r = new byte[20]; new byte[] { 3, 0, 0, 0, 1, 0x35 }.CopyTo(r, 5);
        Assert.Equal("MCHOSE K87", AulaDriver.IdentifyModel(r));
        var early = new byte[20]; new byte[] { 3, 0, 0, 0, 0, 0x6A }.CopyTo(early, 4); Assert.Equal("Redragon Horus Pro", AulaDriver.IdentifyModel(early));
        Assert.Null(AulaDriver.IdentifyModel(new byte[20])); Assert.Null(AulaDriver.IdentifyModel(new byte[5]));
    }

    [Fact]
    public void HeroFramesAreBuiltAndAcknowledged()
    {
        var msg = new byte[56]; msg[0] = 9; msg[1] = 0x87; msg[4] = 1;
        var f1 = AulaDriver.HeroDataFrame(1, 1, msg);
        Assert.Equal(new byte[] { 9, 0x66, 5, 1, 0x8E }, f1[..5]); Assert.Equal(9, f1[5]); Assert.Equal(AulaDriver.SumRange(f1, 1, 19), f1[19]);
        var t = AulaDriver.HeroTrailer(5, msg); Assert.Equal(0x08 | 0x80, t[4]); Assert.Equal((byte)(0xFF - AulaDriver.SumRange(msg, 0, 56)), t[12]); Assert.Equal(AulaDriver.SumRange(t, 1, 13), t[13]);
        // le clavier acquitte : octets 2..4 répétés, type effacé, compteur conservé
        var ack = new byte[20]; ack[0] = 9; ack[1] = 0x66; ack[2] = f1[2]; ack[3] = f1[3]; ack[4] = (byte)(f1[4] & 0x80); ack[5] = AulaDriver.SumRange(ack, 1, 5);
        Assert.True(AulaDriver.IsAckFor(ack, f1)); Assert.False(AulaDriver.IsAckFor(ack, AulaDriver.HeroDataFrame(2, 2, msg)));
        var echo = AulaDriver.HeroAckFrame(f1); Assert.Equal(0x80, echo[4]); Assert.Equal(AulaDriver.SumRange(echo, 1, 5), echo[5]);
    }

    [Fact]
    public void Sc580InfoIsValidatedAndDecoded()
    {
        var q = AulaDriver.BuildScRequest(); Assert.Equal(33, q.Length); Assert.Equal(0x30, q[1]);
        var r = new byte[33]; r[0] = 0x30; r[1] = 0; for (int i = 4; i <= 30; i++) r[i] = 0; r[12] = 1; r[13] = 87; r[14] = 1; r[31] = AulaDriver.SumRange(r, 4, 31);
        var data = AulaDriver.ParseScInfo(r)!; Assert.Equal(27, data.Length);
        var dec = AulaDriver.DecodeSc(AulaDriver.ScResult.Ok, data); Assert.Equal((false, -1, true, false), dec);                              // en charge : 100 fixe ignoré
        data[8] = 0; var batt = AulaDriver.DecodeSc(AulaDriver.ScResult.Ok, data); Assert.Equal((false, 87, false, false), batt);
        data[10] = 0; Assert.True(AulaDriver.DecodeSc(AulaDriver.ScResult.Ok, data).Offline);
        Assert.True(AulaDriver.DecodeSc(AulaDriver.ScResult.Offline, new byte[27]).Offline);
        r[31] ^= 1; Assert.Null(AulaDriver.ParseScInfo(r));
    }

    [Fact]
    public void CollectionsAreMatchedByLayout()
    {
        Assert.True(AulaDriver.IsByCollection(new HidColl { Vid = 0x3554, Pid = 0xFA09, Page = 0xFF02, Usage = 2, OutLen = 20, InLen = 20 }));
        Assert.False(AulaDriver.IsByCollection(new HidColl { Vid = 0x05AC, Pid = 0x024F, Iface = 3, Page = 0xFF60, Usage = 0x61, OutLen = 33, InLen = 33 }));
        Assert.True(AulaDriver.IsByCollection(new HidColl { Vid = 0x05AC, Pid = 0x024F, Iface = 1, Page = 0xFF02, Usage = 2, OutLen = 20, InLen = 20 }));
    }
}

public class AulaMouseDriverTests
{
    [Fact]
    public void RepliesAndHeartbeatsAreDistinguished()
    {
        Assert.Equal(new byte[] { 4, 0, 0, 0x1A }, AulaMouseDriver.Request(0x1A)[..4]);
        var r = new byte[64]; r[0] = 4; r[3] = 0x1A; r[8] = 90; r[9] = 2;
        Assert.True(AulaMouseDriver.IsReply(r, 0x1A)); Assert.Equal((90, true), AulaMouseDriver.ParseBattery(r)!.Value);
        r[9] = 3; Assert.Null(AulaMouseDriver.ParseBattery(r)); r[9] = 0; r[8] = 0; Assert.Null(AulaMouseDriver.ParseBattery(r)); // zéro = cache non initialisé
        var hb = new byte[64]; hb[0] = 4; hb[1] = 0xAA; hb[3] = 0xAA; hb[8] = 0; Assert.True(AulaMouseDriver.IsHeartbeat(hb)); hb[8] = 1; Assert.False(AulaMouseDriver.IsHeartbeat(hb));
    }
}

public class SonixDriverTests
{
    [Fact]
    public void RequestChecksumReplyAndNames()
    {
        var q = SonixDriver.BuildRequest(); Assert.Equal(33, q.Length); Assert.Equal(0x20, q[1]); Assert.Equal((byte)(0x20 + 0x01), q[32]);
        Assert.Equal(72, SonixDriver.ParseReply(new byte[] { 0x20, 1, 0, 72 })); Assert.Null(SonixDriver.ParseReply(new byte[] { 0x20, 1, 1, 72 })); Assert.Null(SonixDriver.ParseReply(new byte[3]));
        Assert.Equal("AJAZZ AK820 MAX", SonixDriver.KeyboardName("AJAZZ AK820 MAX 2.4G")); Assert.Equal("Wireless keyboard", SonixDriver.KeyboardName(""));
        var d = SonixDriver.Dongles[0];
        Assert.True(SonixDriver.IsVendorCollection(d, new HidColl { Iface = 3, Page = 0xFF60, Usage = 0x61, OutLen = 33, InLen = 33 })); Assert.False(SonixDriver.IsVendorCollection(d, new HidColl { Iface = 1, Page = 0xFF60, Usage = 0x61, OutLen = 33, InLen = 33 }));
        Assert.True(SonixDriver.Dongles.Length >= 20);
    }
}

public class RongyuanDriverTests
{
    [Fact]
    public void ReceiversNamesAndStatusAreHandled()
    {
        Assert.True(RongyuanDriver.IsReceiver(0x3151, 0x402D)); Assert.False(RongyuanDriver.IsReceiver(0x1234, 0x5678));
        Assert.Equal("AJAZZ", RongyuanDriver.Brand("AJAZZ 2.4G 8K")); Assert.Equal("", RongyuanDriver.Brand("2.4G Wireless Receiver"));
        Assert.Equal("Attack Shark R3", RongyuanDriver.SlotName(0x3151, 0x402D, false, "2.4G Wireless Receiver"));
        Assert.Equal("AJAZZ wireless mouse", RongyuanDriver.SlotName(0x3151, 0x402D, false, "AJAZZ 2.4G 8K"));
        Assert.Equal("MonsGeek keyboard", RongyuanDriver.SlotName(0x3151, 0x5038, true, "MonsGeek 2.4G Wireless Keyboard"));
        Assert.Equal("Wireless keyboard", RongyuanDriver.SlotName(0x3151, 0x5008, true, "2.4G Wireless Receiver"));
        Assert.Equal("Foo Dongle X", RongyuanDriver.SlotName(0x3151, 0x5008, true, "Foo Dongle X"));
        Assert.True(RongyuanDriver.IsValidStatus(new byte[] { 0, 1, 80, 70, 0, 0, 0 })); Assert.False(RongyuanDriver.IsValidStatus(new byte[7])); Assert.Equal(0xF7, RongyuanDriver.BuildQuery(9)[1]);
    }
}

public class RoyalKludgeDriverTests
{
    [Fact]
    public void BatteryReplyIsDecoded()
    {
        var r = new byte[65]; r[0] = 1; r[1] = 1; r[2] = 2; r[3] = 0x10 | 1; r[4] = 0xA0; r[5] = 0x0F; r[6] = 64; r[10] = 1; // 4000 mV, petit-boutiste
        var p = RoyalKludgeDriver.ParseReply(r); Assert.Equal((RoyalKludgeDriver.Kind.Online, 64, 4000, true), p);
        r[3] = 1; Assert.Equal(RoyalKludgeDriver.Kind.Offline, RoyalKludgeDriver.ParseReply(r).Result);
        r[3] = 0x11; r[6] = 101; Assert.Equal(RoyalKludgeDriver.Kind.Invalid, RoyalKludgeDriver.ParseReply(r).Result);
        r[2] = 5; Assert.Equal(RoyalKludgeDriver.Kind.NotBattery, RoyalKludgeDriver.ParseReply(r).Result); Assert.Equal(RoyalKludgeDriver.Kind.NotBattery, RoyalKludgeDriver.ParseReply(new byte[5]).Result);
        Assert.True(RoyalKludgeDriver.IsCable(0x00A5)); Assert.False(RoyalKludgeDriver.IsCable(0x00A6));
    }
}

public class AulaSonixRandomTests
{
    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(191);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            AulaDriver.IsReply(r, 0x4A); AulaDriver.ParseBattery(r); AulaDriver.IdentifyModel(r); AulaDriver.ParseScInfo(r);
            AulaMouseDriver.IsReply(r, 0x1A); AulaMouseDriver.ParseBattery(r); AulaMouseDriver.IsHeartbeat(r); SonixDriver.ParseReply(r); RoyalKludgeDriver.ParseReply(r); RongyuanDriver.IsValidStatus(r);
            if (r.Length >= 6) { AulaDriver.IsAckFor(r.Length >= 20 ? r : r.Concat(new byte[20]).ToArray(), AulaDriver.HeroAckFrame(r.Concat(new byte[20]).ToArray())); }
            var sc = r.Length >= 28 ? AulaDriver.DecodeSc(AulaDriver.ScResult.Ok, r.Length >= 27 ? r[..27] : r) : default;
        }
    }
}
