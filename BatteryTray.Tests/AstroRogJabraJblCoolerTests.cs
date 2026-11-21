using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

public class CoolerMasterDriverTests
{
    static byte[] Report(byte model, int pct, int chg, int link)
    {
        var r = new byte[32]; r[0] = 0x47; r[1] = 0x81; r[2] = model; r[10] = (byte)pct; r[11] = (byte)chg; r[12] = (byte)link; return r;
    }

    [Fact]
    public void StatusIsRead()
    {
        var s = CoolerMasterDriver.ParseStatus(0x01B7, Report(0x50, 64, 1, 1))!;
        Assert.Equal("Cooler Master MM712", s.Name); Assert.Equal(64, s.Percent); Assert.True(s.Charging); Assert.Equal("2.4 GHz receiver", s.Via);
        var c = CoolerMasterDriver.ParseStatus(0x0167, Report(0x70, 100, 0, 0))!;
        Assert.Equal("Cooler Master MM731", c.Name); Assert.Equal("USB cable", c.Via); Assert.False(c.Charging);
        Assert.Equal("coolermaster:01B7", s.Key);
    }

    [Fact]
    public void WrongOrShortReportsAreRejectedAndOutOfRangeLevelIsUnknown()
    {
        Assert.Null(CoolerMasterDriver.ParseStatus(0x01B7, new byte[12]));
        var bad = Report(0x50, 50, 0, 1); bad[0] = 0x48;
        Assert.Null(CoolerMasterDriver.ParseStatus(0x01B7, bad));
        Assert.Equal(-1, CoolerMasterDriver.ParseStatus(0x01B7, Report(0x50, 0, 0, 1))!.Percent);
        Assert.Equal(-1, CoolerMasterDriver.ParseStatus(0x01B7, Report(0x50, 200, 0, 1))!.Percent);
        Assert.Equal("Cooler Master wireless mouse", CoolerMasterDriver.ModelName(0x01B7, 0x99));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(61);
        for (int i = 0; i < 20_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            if (r.Length > 1 && i % 2 == 0) { r[0] = 0x47; r[1] = 0x81; }
            var s = CoolerMasterDriver.ParseStatus((ushort)rnd.Next(0x10000), r);
            if (s != null) Assert.InRange(s.Percent, -1, 100);
        }
    }
}

public class JblDriverTests
{
    static byte[] Reply(int feature, int info, int sizeLo = 1, int status = 0)
    {
        var r = new byte[64];
        r[0] = 0x86; r[2] = 0xDD; r[3] = 1; r[5] = 1; r[9] = (byte)status; r[10] = (byte)(status >> 8);
        r[11] = (byte)feature; r[12] = (byte)(feature >> 8); r[13] = (byte)sizeLo; r[15] = (byte)info; return r;
    }

    [Fact]
    public void Hidv3BatteryIsRead()
    {
        var p = JblDriver.ParseHidv3(Reply(0x000D, 0x80 | 55));
        Assert.Equal(JblDriver.Hidv3Result.Battery, p.Result); Assert.Equal(55, p.Percent); Assert.True(p.Charging);
        var q = JblDriver.ParseHidv3(Reply(0x000D, 100));
        Assert.Equal(100, q.Percent); Assert.False(q.Charging);
    }

    [Fact]
    public void OtherRepliesErrorsAndImplausibleLevelsAreHandled()
    {
        Assert.Equal(JblDriver.Hidv3Result.NotThisReply, JblDriver.ParseHidv3(Reply(0x0010, 50)).Result);   // autre fonction
        Assert.Equal(JblDriver.Hidv3Result.NotThisReply, JblDriver.ParseHidv3(Reply(0x000D, 50, 0)).Result); // valeur vide
        Assert.Equal(JblDriver.Hidv3Result.NotThisReply, JblDriver.ParseHidv3(new byte[10]).Result);
        Assert.Equal(JblDriver.Hidv3Result.Error, JblDriver.ParseHidv3(Reply(0x000D, 50, 1, 0x8000)).Result);
        Assert.Equal(JblDriver.Hidv3Result.Error, JblDriver.ParseHidv3(Reply(0x000D, 101)).Result);
        var other = Reply(0x000D, 50); other[0] = 0x87;
        Assert.Equal(JblDriver.Hidv3Result.NotThisReply, JblDriver.ParseHidv3(other).Result);
    }

    [Fact]
    public void RequestAndHidv1Bytes()
    {
        var q = JblDriver.BuildBatteryRequest();
        Assert.Equal(0x88, q[0]); Assert.Equal(0x0D, q[9]); Assert.Equal(0x00, q[10]);
        Assert.Equal(1, JblDriver.FeatureByte(0x68, new byte[] { 0x68, 1, 0 }));
        Assert.Null(JblDriver.FeatureByte(0x49, new byte[] { 0x68, 1, 0 })); // le micrologiciel a répondu avec un autre identifiant
        Assert.Null(JblDriver.FeatureByte(0x49, new byte[] { 0x49 }));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(62);
        for (int i = 0; i < 20_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            if (r.Length > 15 && i % 2 == 0) { r[0] = 0x86; r[1] = 0; r[2] = 0xDD; r[3] = 1; r[4] = 0; r[5] = 1; r[11] = 0x0D; r[12] = 0; }
            var p = JblDriver.ParseHidv3(r);
            if (p.Result == JblDriver.Hidv3Result.Battery) Assert.InRange(p.Percent, 0, 100);
        }
    }
}

public class JabraDriverTests
{
    [Fact]
    public void ReplyIsMatchedToTheQuery()
    {
        // [05][00][src 04][seq 21][C0|8][12][02][flags][niveau]…
        var r = new byte[64]; r[0] = 0x05; r[1] = 0; r[2] = 0x04; r[3] = 0x21; r[4] = 0xC8; r[5] = 0x12; r[6] = 0x02; r[7] = 0x01; r[8] = 77;
        var p = JabraDriver.ParseReply(r, 0x04, 0x21, 0x12, 0x02);
        Assert.Equal(JabraDriver.Kind.Ok, p.Result);
        var b = JabraDriver.ParseBattery(p.Payload)!.Value;
        Assert.Equal(77, b.Percent); Assert.True(b.Charging);
        Assert.Equal(JabraDriver.Kind.None, JabraDriver.ParseReply(r, 0x08, 0x21, 0x12, 0x02).Result); // autre adresse
        Assert.Equal(JabraDriver.Kind.None, JabraDriver.ParseReply(r, 0x04, 0x22, 0x12, 0x02).Result); // autre séquence
        Assert.Equal(JabraDriver.Kind.None, JabraDriver.ParseReply(r, 0x04, 0x21, 0x02, 0x00).Result); // autre commande
    }

    [Fact]
    public void NakCarriesItsReason()
    {
        var r = new byte[64]; r[0] = 0x05; r[2] = 0x04; r[3] = 0x30; r[4] = 0xC7; r[5] = 0xFE; r[6] = 0xF3;
        var p = JabraDriver.ParseReply(r, 0x04, 0x30, 0x12, 0x02);
        Assert.Equal(JabraDriver.Kind.Nak, p.Result); Assert.Equal(0xF3, p.Payload[0]);
    }

    [Fact]
    public void BatteryNameAndRequestAreBuilt()
    {
        Assert.Null(JabraDriver.ParseBattery(new byte[] { 1 }));
        Assert.Equal(-1, JabraDriver.ParseBattery(new byte[] { 0, 150 })!.Value.Percent);
        var name = System.Text.Encoding.UTF8.GetBytes("Evolve3 75");
        Assert.Equal("Evolve3 75", JabraDriver.ParseName(new byte[] { (byte)name.Length }.Concat(name).ToArray()));
        Assert.Equal("", JabraDriver.ParseName(new byte[] { 9, 65 })); // longueur annoncée trop grande
        Assert.Equal("", JabraDriver.ParseName(new byte[] { 0 }));
        Assert.Equal(new byte[] { 5, 4, 0, 0x20, 0x46, 0x12, 2 }, JabraDriver.BuildQuery(4, 0x20, 0x12, 2));
    }

    [Fact]
    public void DectBasesAreExcluded()
    {
        Assert.True(JabraDriver.IsDectBase(0x1000)); Assert.True(JabraDriver.IsDectBase(0xAE63));
        Assert.False(JabraDriver.IsDectBase(0x2E51)); Assert.False(JabraDriver.IsDectBase(0x2548));
    }

    [Fact]
    public void ParserSurvivesRandomReports()
    {
        var rnd = new Random(63);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            if (r.Length > 6 && i % 2 == 0) { r[0] = 5; r[1] = 0; r[2] = 4; r[3] = 0x21; r[4] |= 0xC0; if (i % 4 == 0) { r[5] = 0x12; r[6] = 2; } }
            var p = JabraDriver.ParseReply(r, 4, 0x21, 0x12, 2);
            if (p.Result == JabraDriver.Kind.Ok) { var b = JabraDriver.ParseBattery(p.Payload); if (b != null) Assert.InRange(b.Value.Percent, -1, 100); }
            JabraDriver.ParseName(r);
        }
    }
}

public class RogDriverTests
{
    static RogDriver.Model Find(ushort pid, byte rid) => RogDriver.Models.First(m => m.Pid == pid && m.ReportId == rid);

    [Fact]
    public void ModelTableIsConsistent()
    {
        Assert.True(RogDriver.Models.Length > 60);
        foreach (var m in RogDriver.Models) { Assert.StartsWith("ROG", m.Name); Assert.True(m.Offset is 5 or 6 or 7 or 11); if (m.ReportId != 0) Assert.NotNull(m.Col); }
    }

    [Fact]
    public void KeyboardAndMouseLevelsAreRead()
    {
        var kb = new byte[20]; kb[0] = 2; kb[1] = 0x12; kb[2] = 1; kb[6] = 81; kb[9] = 1;
        var k = RogDriver.ReadLevel(Find(0x1A85, 2), kb);
        Assert.Equal((81, true, false), k);
        var falchion = new byte[20]; falchion[11] = 40;
        Assert.Equal(40, RogDriver.ReadLevel(Find(0x193E, 0), falchion).Percent);
        var ms = new byte[20]; ms[5] = 3; ms[10] = 1;
        Assert.Equal((75, true, false), RogDriver.ReadLevel(Find(0x18E5, 0), ms)); // ancienne jauge 0-4 : ×25
        var ms2 = new byte[20]; ms2[5] = 64;
        Assert.Equal((64, false, false), RogDriver.ReadLevel(Find(0x1A1A, 0), ms2));
        Assert.True(RogDriver.ReadLevel(Find(0x1A1A, 0), new byte[20]).Asleep); // 0 = indisponible, pas vide
        Assert.True(RogDriver.ReadLevel(Find(0x1A1A, 0), new byte[8]).Asleep);
    }

    [Fact]
    public void RepliesRestoreTheReportIdSlot()
    {
        var raw = new byte[63]; raw[0] = 0x12; raw[1] = 0x07; raw[5 - 1] = 55; // rapport non numéroté : l'identifiant 0 a été retiré
        var p = RogDriver.ParseReply(raw, 0, 0x12, 0x07);
        Assert.Equal(RogDriver.Kind.Ok, p.Result); Assert.Equal(55, p.Report[5]);
        var asleep = new byte[63]; asleep[0] = 0xFF; asleep[1] = 0xAA;
        Assert.Equal(RogDriver.Kind.Asleep, RogDriver.ParseReply(asleep, 0, 0x12, 0x07).Result);
        Assert.Equal(RogDriver.Kind.Invalid, RogDriver.ParseReply(new byte[5], 0, 0x12, 0x07).Result);
        Assert.Equal(RogDriver.Kind.Skip, RogDriver.ParseReply(new byte[63], 0, 0x12, 0x07).Result);
    }

    [Fact]
    public void OmniPairedListStopsAtZero()
    {
        var r = new byte[64]; r[5] = 0xD0; r[6] = 0x1A; r[9] = 0x85; r[10] = 0x1A; // 1AD0 puis 1A85 puis fin
        Assert.Equal(new ushort[] { 0x1AD0, 0x1A85 }, RogDriver.ParseOmniList(r));
        Assert.Empty(RogDriver.ParseOmniList(new byte[4]));
    }

    [Fact]
    public void HeadsetRepliesAndCetraDetail()
    {
        var ok = new byte[64]; ok[0] = 0xCC; ok[1] = 0x12; ok[2] = 7; ok[6] = 88;
        var p = RogDriver.ParseHeadsetReply(ok, 7);
        Assert.Equal(RogDriver.Kind.Ok, p.Result); Assert.Equal(88, RogDriver.HeadsetLevel(p.Report));
        ok[6] = 0xFE; Assert.Equal(-1, RogDriver.HeadsetLevel(ok));
        var asleep = new byte[64]; asleep[0] = 0xCC; asleep[1] = 0x12; asleep[2] = 7; asleep[5] = 0xFF; asleep[6] = 0xAA;
        Assert.Equal(RogDriver.Kind.Asleep, RogDriver.ParseHeadsetReply(asleep, 7).Result);
        Assert.Equal(RogDriver.Kind.Skip, RogDriver.ParseHeadsetReply(ok, 8).Result);
        var cetra = new byte[64]; cetra[5] = 99; cetra[6] = 98; cetra[7] = 86;
        var (l, r, c) = RogDriver.CetraLevels(cetra);
        Assert.Equal((99, 98, 86), (l, r, c));
        Assert.Equal("L 99%, R 98%, case 86%", RogDriver.CetraDetail(l, r, c));
        Assert.Equal("L 50%", RogDriver.CetraDetail(50, -1, -1));
        Assert.Equal((-1, -1, -1), RogDriver.CetraLevels(new byte[64]));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(64);
        var models = RogDriver.Models;
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            var m = models[rnd.Next(models.Length)];
            var p = RogDriver.ParseReply(r, m.ReportId, 0x12, 0x07);
            if (p.Result == RogDriver.Kind.Ok) { var lv = RogDriver.ReadLevel(m, p.Report); if (!lv.Asleep) Assert.InRange(lv.Percent, -1, 100); }
            RogDriver.ParseOmniList(r); RogDriver.CetraLevels(r); RogDriver.HeadsetLevel(r);
            var h = RogDriver.ParseHeadsetReply(r, 7); if (h.Result == RogDriver.Kind.Ok) RogDriver.HeadsetLevel(h.Report);
        }
    }
}

public class AstroDriverTests
{
    [Fact]
    public void StatusAndBatteryBytes()
    {
        Assert.Equal((true, true), AstroDriver.ParseStatus(0x03));
        Assert.Equal((false, false), AstroDriver.ParseStatus(0x00));
        Assert.Equal((70, false), AstroDriver.ParseBatteryByte(70, false)!.Value);
        Assert.Equal((70, true), AstroDriver.ParseBatteryByte(0x80 | 70, false)!.Value);
        Assert.Equal((90, true), AstroDriver.ParseBatteryByte(90, true)!.Value);   // posé sur la base et pas plein : en charge
        Assert.Equal((100, false), AstroDriver.ParseBatteryByte(100, true)!.Value);
        Assert.Null(AstroDriver.ParseBatteryByte(120, false));
    }

    [Fact]
    public void FramesAndLegacyReplies()
    {
        Assert.Equal(new byte[] { 0x02, 0x54 }, AstroDriver.LegacyFrame(AstroDriver.Framing.ReportId02, 0x54));
        Assert.Equal(new byte[] { 0x00, 0x02, 0x7C, 1, 9 }, AstroDriver.LegacyFrame(AstroDriver.Framing.NoReportId, 0x7C, new byte[] { 9 }));
        Assert.Null(AstroDriver.ClassifyLegacyReply(new byte[] { 0x02, 0x02 }));          // trop court
        Assert.Null(AstroDriver.ClassifyLegacyReply(new byte[] { 0x03, 0x02, 1, 2 }));    // autre rapport
        Assert.Equal(AstroDriver.QResult.Error, AstroDriver.ClassifyLegacyReply(new byte[] { 0x02, 0x01, 0, 0 })!.Value.Result);
        var ok = new byte[] { 0x02, 0x02, 1, 0x42 };
        var c = AstroDriver.ClassifyLegacyReply(ok)!.Value;
        Assert.Equal(AstroDriver.QResult.Ok, c.Result); Assert.Equal(0x42, AstroDriver.LegacyValue(c.Frame!));
    }

    [Fact]
    public void Gen3BatteryFrame()
    {
        var r = new byte[3 + 16]; r[2] = 16; r[3 + 12] = 0x02; r[3 + 13] = 0x04; r[3 + 14] = 0x85;
        Assert.Equal((byte)0x85, AstroDriver.ParseGen3Battery(r));
        r[3 + 14] = 0x7F; Assert.Null(AstroDriver.ParseGen3Battery(r));
        Assert.Null(AstroDriver.ParseGen3Battery(new byte[10]));
    }

    [Fact]
    public void A50XRequestAndReply()
    {
        var q = AstroDriver.BuildA50XRequest();
        Assert.Equal(64, q.Length); Assert.Equal(0x0C, q[1]); Assert.Equal(0x06, q[4]);
        var r = new byte[64]; r[0] = 2; r[1] = 0x0C; r[2] = 6; r[4] = 6; r[5] = 0x0C; r[6] = 45; r[8] = 1;
        Assert.Equal((45, true), AstroDriver.ParseA50X(r, 9)!.Value);
        Assert.Null(AstroDriver.ParseA50X(r, 8));                       // lecture partielle
        r[6] = 101; Assert.Null(AstroDriver.ParseA50X(r, 9));           // niveau impossible
        var ev = new byte[64]; ev[0] = 2; ev[1] = 0x0D;                 // évènement non sollicité (volume)
        Assert.Null(AstroDriver.ParseA50X(ev, 64));
    }

    [Fact]
    public void ParsersSurviveRandomReports()
    {
        var rnd = new Random(65);
        for (int i = 0; i < 30_000; i++)
        {
            var r = new byte[rnd.Next(0, 70)]; rnd.NextBytes(r);
            if (r.Length > 6 && i % 2 == 0) { r[0] = 2; r[1] = (byte)(i % 4 == 0 ? 0x0C : 0x02); }
            var a = AstroDriver.ParseA50X(r, rnd.Next(0, 70)); if (a != null) Assert.InRange(a.Value.Percent, 0, 100);
            AstroDriver.ParseGen3Battery(r); AstroDriver.ClassifyLegacyReply(r);
            var b = AstroDriver.ParseBatteryByte((byte)rnd.Next(256), i % 2 == 0); if (b != null) Assert.InRange(b.Value.Percent, 0, 100);
        }
    }
}
