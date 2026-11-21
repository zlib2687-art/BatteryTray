using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

/// <summary>Pilotes Corsair et HyperX (portés de BatteryHub) : analyse des trames sans matériel.</summary>
public class HidDriversMoreTests
{
    static byte[] F(int len, params (int I, int V)[] b)
    {
        var f = new byte[len];
        foreach (var (i, v) in b) f[i] = (byte)v;
        return f;
    }

    // ------------------------------------------------------------------ Corsair
    [Theory]
    [InlineData(0, 80, false, false, -1)]   // hors ligne
    [InlineData(1, 80, true, false, 80)]
    [InlineData(2, 0x80 | 55, true, false, 55)] // bit 7 = micro relevé
    [InlineData(4, 30, true, true, 30)]
    [InlineData(5, 30, true, true, 30)]
    [InlineData(3, 30, true, false, -1)]    // état inconnu : pas de niveau
    public void CorsairVoidReply(int status, int level, bool connected, bool charging, int percent)
    {
        var p = CorsairDriver.ParseVoid(F(8, (2, level), (4, status)))!.Value;
        Assert.Equal(connected, p.Connected); Assert.Equal(charging, p.Charging); Assert.Equal(percent, p.Percent);
        Assert.Null(CorsairDriver.ParseVoid(new byte[4]));
    }

    [Fact]
    public void CorsairBragiRepliesAreReadInBothFramings()
    {
        // « plain » : source 02 état valeur(4)
        var plain = F(10, (0, 1), (1, 2), (2, 0), (3, 0xE8), (4, 0x03)); // 0x03E8 = 1000
        Assert.Equal(1000, CorsairDriver.ParseBragi(plain, true, 0x09));
        // « prefixed » : 02 source 02 état valeur(4)
        var prefixed = F(10, (0, 2), (1, 0), (2, 2), (3, 0), (4, 0xF4), (5, 0x01)); // 500, cible 08 → source 00
        Assert.Equal(500, CorsairDriver.ParseBragi(prefixed, false, 0x08));
        Assert.Null(CorsairDriver.ParseBragi(plain, true, 0x08));                     // autre source : ignorée
        Assert.Null(CorsairDriver.ParseBragi(F(10, (0, 1), (1, 9)), true, 0x09));     // notification
        Assert.Equal(-1, CorsairDriver.ParseBragi(F(10, (0, 1), (1, 2), (2, 5)), true, 0x09)); // propriété inconnue
        Assert.Null(CorsairDriver.ParseBragi(new byte[3], true, 0x09));
        Assert.Equal(100, CorsairDriver.BragiPercent(1000)); Assert.Equal(50, CorsairDriver.BragiPercent(500)); Assert.Equal(0, CorsairDriver.BragiPercent(0));
    }

    [Fact]
    public void CorsairCompxFramesAreChecksummed()
    {
        var req = CorsairDriver.BuildCompxRequest(0x04);
        Assert.Equal(17, req.Length); Assert.Equal(0x08, req[0]); Assert.Equal(0x04, req[1]);
        Assert.Equal((byte)(0x55 - 0x08 - 0x04), req[16]);
        var reply = F(17, (0, 8), (1, 4), (6, 73), (7, 1), (8, 0x0F), (9, 0xA0));
        reply[16] = CorsairDriver.CompxChecksum(reply);
        Assert.True(CorsairDriver.IsValidCompxReply(reply, 0x04));
        Assert.False(CorsairDriver.IsValidCompxReply(reply, 0x03));
        reply[6] = 74;
        Assert.False(CorsairDriver.IsValidCompxReply(reply, 0x04)); // somme de contrôle fausse
        Assert.False(CorsairDriver.IsValidCompxReply(new byte[10], 0x04));
    }

    // ------------------------------------------------------------------ HyperX
    [Fact]
    public void Cloud3AndCloud2AreParsed()
    {
        var c3 = HyperXDriver.ParseCloud3(0x0C9D, F(6, (2, 1)), F(6, (2, 1)), F(6, (2, 0x0F), (3, 0xA0), (4, 66)))!;
        Assert.True(c3.Connected); Assert.True(c3.Charging); Assert.Equal(66, c3.Percent);
        Assert.False(HyperXDriver.ParseCloud3(0x0C9D, F(6, (2, 0)), null, null)!.Connected);
        Assert.Equal("HyperX Cloud II Core Wireless", HyperXDriver.ParseCloud3(0x069F, F(6, (2, 1)), null, null)!.Name);
        Assert.Equal(-1, HyperXDriver.ParseCloud3(0x0C9D, F(6, (2, 1)), null, F(6, (4, 200)))!.Percent);
        Assert.Null(HyperXDriver.ParseCloud3(0x0C9D, null, null, null));

        var c2 = HyperXDriver.ParseCloud2(0x0696, F(10, (7, 90)), F(10, (4, 1)), false)!;
        Assert.Equal(90, c2.Percent); Assert.True(c2.Charging);
        Assert.Equal("HyperX Cloud Flight S", HyperXDriver.ParseCloud2(0x16EA, F(10, (7, 50)), null, true)!.Name);
        Assert.Null(HyperXDriver.ParseCloud2(0x0696, F(10, (7, 150)), null, false));
    }

    [Fact]
    public void CloudAlphaFamilyIsParsed()
    {
        var a = HyperXDriver.ParseCloudAlpha(0x098D, F(5, (3, 0)), F(5, (3, 1)), F(5, (3, 42)))!;
        Assert.True(a.Connected); Assert.True(a.Charging); Assert.Equal(42, a.Percent);
        Assert.False(HyperXDriver.ParseCloudAlpha(0x098D, F(5, (3, 1)), null, null)!.Connected);
        var a2 = HyperXDriver.ParseCloudAlpha2(0x08BE, F(10, (2, 1)), F(10, (2, 77), (3, 1)))!;
        Assert.Equal(77, a2.Percent); Assert.True(a2.Charging);
        Assert.False(HyperXDriver.ParseCloudAlpha2(0x08BE, F(10, (2, 0)), null)!.Connected);
        Assert.Null(HyperXDriver.ParseCloudAlpha2(0x08BE, new byte[3], null));
    }

    [Fact]
    public void HasteIgnoresTheStartupFortyPercentAndTolerantlyMissingLinkReply()
    {
        var link = F(10, (2, 1));
        var battery = F(10, (2, 70), (3, 2));
        var ok = HyperXDriver.ParseHaste(0x0AB5, link, battery, settled: true)!;
        Assert.Equal(DeviceKind.Mouse, ok.Kind); Assert.Equal(70, ok.Percent); Assert.True(ok.Charging);
        Assert.Equal(-1, HyperXDriver.ParseHaste(0x0AB5, link, F(10, (2, 40)), settled: false)!.Percent); // lecture non fiable
        Assert.Equal(55, HyperXDriver.ParseHaste(0x0F98, null, F(10, (2, 55)), true)!.Percent);          // réponse « lien » manquante
        Assert.Null(HyperXDriver.ParseHaste(0x0F98, null, null, true));
        Assert.False(HyperXDriver.ParseHaste(0x0DA0, F(10, (2, 0)), null, true)!.Connected);
        Assert.Equal("HyperX Pulsefire Haste 2 Mini", HyperXDriver.ParseHaste(0x0DA0, link, battery, true)!.Name);
    }

    [Fact]
    public void SettleTrackerWaitsSixSecondsAfterPowerOn()
    {
        var t = new HyperXDriver.SettleTracker();
        Assert.True(t.Update(true, 1_000));          // premier relevé : on suppose la souris allumée depuis longtemps
        Assert.False(t.Update(false, 2_000));        // éteinte
        Assert.False(t.Update(true, 3_000));         // vient de s'allumer : pas encore fiable
        Assert.False(t.Update(true, 8_000));
        Assert.True(t.Update(true, 9_500));          // plus de 6 s
    }

    [Fact]
    public void CloudFlightConvertsVoltageAndDetectsCharging()
    {
        static byte[] Mv(int mv) => F(8, (3, mv >> 8), (4, mv & 0xFF));
        Assert.True(HyperXDriver.ParseCloudFlight(0x0E90, Mv(0x1100))!.Charging);
        Assert.Equal(100, HyperXDriver.ParseCloudFlight(0x0E90, Mv(4000))!.Percent);
        Assert.InRange(HyperXDriver.ParseCloudFlight(0x0E90, Mv(3600))!.Percent, 0, 10);
        int prev = -1;
        for (int mv = 3650; mv <= 3975; mv += 5) { int p = HyperXDriver.ParseCloudFlight(0x0E90, Mv(mv))!.Percent; Assert.InRange(p, 0, 100); prev = p; }
        Assert.True(prev >= 90);
        Assert.Null(HyperXDriver.ParseCloudFlight(0x0E90, new byte[2]));
    }

    [Fact]
    public void Cloud3SAndStingerCoreAreParsed()
    {
        var s = HyperXDriver.ParseCloud3S(0x06BE, F(8, (6, 2)), F(8, (6, 64)), F(8, (6, 1)))!;
        Assert.Equal(64, s.Percent); Assert.True(s.Charging);
        Assert.False(HyperXDriver.ParseCloud3S(0x06BE, F(8, (6, 1)), null, null)!.Connected);

        var conn = F(16, (11, 1));
        var full = HyperXDriver.ParseStingerCore(0x0A93, conn, F(16, (9, 6)))!;
        Assert.Equal(100, full.Percent);
        var bat = F(16, (9, 3), (11, 0x1A), (12, 0x0F)); // 0x0F1A = 3866 mV sur batterie
        var d = HyperXDriver.ParseStingerCore(0x0A93, conn, bat)!;
        Assert.InRange(d.Percent, 60, 70); Assert.False(d.Charging); // 3866 mV entre 3905 (70 %) et 3838 (60 %)
        var ch = HyperXDriver.ParseStingerCore(0x0A93, conn, F(16, (9, 5), (11, 0x1A), (12, 0x0F)))!;
        Assert.True(ch.Charging);
        Assert.False(HyperXDriver.ParseStingerCore(0x0A93, F(16, (11, 3)), bat)!.Connected);
        Assert.Equal(-1, HyperXDriver.ParseStingerCore(0x0A93, conn, F(16, (9, 9)))!.Percent); // code inconnu
        Assert.Equal("HyperX Cloud Core Wireless", HyperXDriver.ParseStingerCore(0x173F, conn, null)!.Name);
        Assert.Null(HyperXDriver.ParseStingerCore(0x0A93, new byte[5], null));
    }

    // ------------------------------------------------------------------ robustesse
    [Fact]
    public void CorsairAndHyperXParsersSurviveRandomFrames()
    {
        var rnd = new Random(77);
        for (int i = 0; i < 20_000; i++)
        {
            var a = new byte[rnd.Next(0, 70)]; rnd.NextBytes(a);
            var b = new byte[rnd.Next(0, 70)]; rnd.NextBytes(b);
            ushort pid = (ushort)rnd.Next(0, 0xFFFF);
            void Ok(DeviceStatus? s) { if (s != null) Assert.InRange(s.Percent, -1, 100); }
            var v = CorsairDriver.ParseVoid(a); if (v != null) Assert.InRange(v.Value.Percent, -1, 100);
            CorsairDriver.ParseBragi(a, i % 2 == 0, (byte)rnd.Next(8, 12));
            CorsairDriver.IsValidCompxReply(a, (byte)rnd.Next(0, 5));
            Ok(HyperXDriver.ParseCloud3(pid, a, b, a)); Ok(HyperXDriver.ParseCloud2(pid, a, b, i % 2 == 0));
            Ok(HyperXDriver.ParseCloudAlpha(pid, a, b, a)); Ok(HyperXDriver.ParseCloudAlpha2(pid, a, b));
            Ok(HyperXDriver.ParseHaste(pid, a, b, i % 2 == 0)); Ok(HyperXDriver.ParseCloudFlight(pid, a));
            Ok(HyperXDriver.ParseCloud3S(pid, a, b, a)); Ok(HyperXDriver.ParseStingerCore(pid, a, b));
        }
    }

    [Fact]
    public void BragiPercentIsAlwaysInRange()
    {
        foreach (long level in new long[] { long.MinValue, -1, 0, 4, 5, 1000, 1001, 5000, int.MaxValue, long.MaxValue / 2 })
            Assert.InRange(CorsairDriver.BragiPercent(level), 0, 100);
    }
}
