using BatteryTray.Providers.Hid;
using Xunit;

namespace BatteryTray.Tests;

/// <summary>Pilotes de marques portés de BatteryHub : analyse des trames (sans matériel), contexte, utilitaires et robustesse.</summary>
public class HidDriversTests
{
    static byte[] Frame(int len, params (int Index, int Value)[] bytes)
    {
        var f = new byte[len];
        foreach (var (i, v) in bytes) f[i] = (byte)v;
        return f;
    }

    // ------------------------------------------------------------------ Glorious / Hitscan / G-Wolves / WLMouse
    [Fact]
    public void GloriousRequestHasTheDocumentedLayout()
    {
        var w = GloriousDriver.BuildRequest();
        Assert.Equal(65, w.Length);
        Assert.Equal(0x02, w[3]); Assert.Equal(0x02, w[4]); Assert.Equal(0x83, w[6]);
        Assert.Equal(0, w[0]);
    }

    [Theory]
    [InlineData(0xA1, 0, 73, GloriousDriver.Awake.Awake, false, 73)]
    [InlineData(0xA1, 1, 100, GloriousDriver.Awake.Awake, true, 100)]
    [InlineData(0xA1, 0, 0, GloriousDriver.Awake.Awake, false, 1)]   // 0 % signalé : au moins 1 (l'appareil répond)
    [InlineData(0xA1, 0, 101, GloriousDriver.Awake.Asleep, false, -1)]
    [InlineData(0xA4, 0, 50, GloriousDriver.Awake.Asleep, false, -1)]
    [InlineData(0xA0, 0, 50, GloriousDriver.Awake.WakingUp, false, -1)]
    [InlineData(0x00, 0, 50, GloriousDriver.Awake.Invalid, false, -1)]
    public void GloriousReplyIsInterpreted(int status, int charge, int percent, GloriousDriver.Awake state, bool charging, int expected)
    {
        var r = Frame(65, (1, status), (4, 2), (6, 0x83), (7, charge), (8, percent));
        var parsed = GloriousDriver.Parse(r);
        Assert.Equal(state, parsed.State);
        Assert.Equal(charging, parsed.Charging);
        Assert.Equal(expected, parsed.Percent);
    }

    [Fact]
    public void GloriousIgnoresAnotherProtocolAndShortFrames()
    {
        Assert.Equal(GloriousDriver.Awake.Invalid, GloriousDriver.Parse(Frame(65, (1, 0xA1), (6, 0x84), (8, 50))).State);
        Assert.Equal(GloriousDriver.Awake.Invalid, GloriousDriver.Parse(new byte[5]).State);
        Assert.Equal(GloriousDriver.Awake.Invalid, GloriousDriver.Parse(Array.Empty<byte>()).State);
    }

    [Fact]
    public void GWolvesNamesAndCablesAreRecognized()
    {
        Assert.Equal("G-Wolves Fenrir Pro", GloriousDriver.GWolvesModelName(0x3608));
        Assert.Equal("G-Wolves wireless mouse", GloriousDriver.GWolvesModelName(0xFFFF));
        Assert.True(GloriousDriver.GWolvesIsCable(0x3608));
        Assert.False(GloriousDriver.GWolvesIsCable(0x3617));
    }

    [Fact]
    public void SinowealthKindUsesNameThenCollections()
    {
        HidColl C(ushort page, ushort usage) => new() { Vid = 0x258A, Pid = 0x1, Page = page, Usage = usage };
        var keyboard = new[] { C(1, 6), C(1, 6), C(0xFF, 1) };
        var mouse = new[] { C(1, 2), C(0xFF, 1) };
        Assert.Equal(DeviceKind.Keyboard, GloriousDriver.SinowealthKind(keyboard, 0x258A, 1, "Wireless Device"));
        Assert.Equal(DeviceKind.Mouse, GloriousDriver.SinowealthKind(mouse, 0x258A, 1, null));
        Assert.Equal(DeviceKind.Keyboard, GloriousDriver.SinowealthKind(mouse, 0x258A, 1, "Redragon Keyboard K1"));
    }

    // ------------------------------------------------------------------ SteelSeries
    [Fact]
    public void Arctis1AndArctis7ReadTheLevel()
    {
        var a1 = SteelSeriesDriver.ParseArctis1(0x12B3, Frame(8, (2, 0), (3, 64)))!;
        Assert.True(a1.Connected); Assert.Equal(64, a1.Percent); Assert.Equal(DeviceKind.Headset, a1.Kind);
        var off = SteelSeriesDriver.ParseArctis1(0x12B3, Frame(8, (2, 1), (3, 64)))!;
        Assert.False(off.Connected); Assert.Equal(-1, off.Percent);
        var a7 = SteelSeriesDriver.ParseArctis7(0x1260, Frame(8, (2, 88)))!;
        Assert.Equal(88, a7.Percent); Assert.Equal("SteelSeries Arctis 7", a7.Name);
        Assert.Equal("SteelSeries Arctis Pro", SteelSeriesDriver.ParseArctis7(0x1252, Frame(8, (2, 10)))!.Name);
        Assert.False(SteelSeriesDriver.ParseArctis7(0x1260, Frame(8, (2, 0)))!.Connected);
        Assert.Null(SteelSeriesDriver.ParseArctis1(0x12B3, new byte[2]));
    }

    [Fact]
    public void Arctis9MapsRawLevelAndCharging()
    {
        var low = SteelSeriesDriver.ParseArctis9(0x12C2, Frame(8, (3, 0x64), (4, 0)))!;
        var high = SteelSeriesDriver.ParseArctis9(0x12C2, Frame(8, (3, 0x9A), (4, 1)))!;
        Assert.Equal(0, low.Percent); Assert.Equal(100, high.Percent); Assert.True(high.Charging);
        Assert.InRange(SteelSeriesDriver.ParseArctis9(0x12C2, Frame(8, (3, 0xFF)))!.Percent, 0, 100);
    }

    [Fact]
    public void ArctisProWirelessAndNovaProWirelessAreParsed()
    {
        var on = SteelSeriesDriver.ParseArctisProWireless(0x1290, Frame(4, (0, 4)), Frame(4, (0, 2)))!;
        Assert.True(on.Connected); Assert.Equal(50, on.Percent);
        var off = SteelSeriesDriver.ParseArctisProWireless(0x1290, Frame(4, (0, 2)), null)!;
        Assert.False(off.Connected);
        var nova = SteelSeriesDriver.ParseNovaProWireless(0x12E0, Frame(20, (6, 4), (15, 2)))!;
        Assert.Equal(50, nova.Percent); Assert.True(nova.Charging);
        Assert.False(SteelSeriesDriver.ParseNovaProWireless(0x12E0, Frame(20, (15, 1)))!.Connected);
        Assert.Null(SteelSeriesDriver.ParseNovaProWireless(0x12E0, new byte[10]));
    }

    [Fact]
    public void NovaFamilyLayoutsDependOnTheModel()
    {
        var p7 = SteelSeriesDriver.ParseNovaFamily(0x220E, Frame(12, (1, 0), (2, 2), (3, 1)))!;
        Assert.Equal("SteelSeries Arctis 7+", p7.Name); Assert.Equal(50, p7.Percent); Assert.True(p7.Charging);
        var n5 = SteelSeriesDriver.ParseNovaFamily(0x2232, Frame(12, (1, 0), (3, 77), (4, 0)))!;
        Assert.Equal("SteelSeries Arctis Nova 5", n5.Name); Assert.Equal(77, n5.Percent);
        var n3 = SteelSeriesDriver.ParseNovaFamily(0x2269, Frame(12, (1, 2), (3, 77)))!;
        Assert.False(n3.Connected); Assert.Equal(-1, n3.Percent);
        var buds = SteelSeriesDriver.ParseNovaFamily(0x230A, Frame(12, (3, 3), (4, 3), (5, 60), (6, 40)))!;
        Assert.Equal(40, buds.Percent); // le plus bas des deux écouteurs
        var budsOne = SteelSeriesDriver.ParseNovaFamily(0x230A, Frame(12, (3, 0), (4, 3), (6, 55)))!;
        Assert.Equal(55, budsOne.Percent);
        var budsNone = SteelSeriesDriver.ParseNovaFamily(0x230A, Frame(12))!;
        Assert.False(budsNone.Connected);
        var n7 = SteelSeriesDriver.ParseNovaFamily(0x2202, Frame(12, (2, 3), (3, 0)))!;
        Assert.False(n7.Connected);
        var n7on = SteelSeriesDriver.ParseNovaFamily(0x2202, Frame(12, (2, 3), (3, 2)))!;
        Assert.Equal(75, n7on.Percent); Assert.True(n7on.Charging);
        Assert.Null(SteelSeriesDriver.ParseNovaFamily(0x2202, new byte[3]));
    }

    [Fact]
    public void AeroxStepLayoutAndPercentLayout()
    {
        var step = SteelSeriesDriver.Aerox.First(m => m.Pid == 0x1838);
        var s = SteelSeriesDriver.ParseAerox(step, Frame(4, (0, 0xD2), (1, 0x0A)))!;   // (10 - 1) * 5 = 45 %
        Assert.Equal(45, s.Percent); Assert.False(s.Charging);
        var cable = SteelSeriesDriver.Aerox.First(m => m.Pid == 0x183A);
        var c = SteelSeriesDriver.ParseAerox(cable, Frame(4, (0, 0x92), (1, 0x8B)))!;  // 0x0B → 50 %, en charge
        Assert.Equal(50, c.Percent); Assert.True(c.Charging);
        Assert.Equal(c.Key, s.Key); // même identité sur récepteur et câble
        Assert.Null(SteelSeriesDriver.ParseAerox(step, Frame(4, (1, 0)))); // éteinte ou en veille
        Assert.Equal(-1, SteelSeriesDriver.ParseAerox(step, Frame(4, (1, 0x40)))!.Percent); // niveau invalide : inconnu

        var gen2 = SteelSeriesDriver.Aerox.First(m => m.Pid == 0x1890);
        var g = SteelSeriesDriver.ParseAerox(gen2, Frame(4, (1, 1), (2, 88)))!;
        Assert.Equal(88, g.Percent); Assert.True(g.Charging);
        Assert.Equal(-1, SteelSeriesDriver.ParseAerox(gen2, Frame(4, (1, 0), (2, 0xFF)))!.Percent); // pas encore mesuré
        Assert.Null(SteelSeriesDriver.ParseAerox(gen2, Frame(4, (1, 0), (2, 0))));                  // récepteur : souris éteinte
        Assert.Null(SteelSeriesDriver.ParseAerox(gen2, Frame(4, (1, 7), (2, 50))));                 // pas un octet d'état Gen 2
    }

    [Fact]
    public void RivalAndRivalProAreParsed()
    {
        var r = SteelSeriesDriver.ParseRival(0x1830, Frame(4, (0, 66), (2, 1)))!;
        Assert.Equal(66, r.Percent); Assert.True(r.Charging);
        Assert.Equal("steelseries:172B", SteelSeriesDriver.ParseRival(0x1726, Frame(4, (0, 10)))!.Key);
        var pro = SteelSeriesDriver.ParseRivalPro(0x1880, Frame(6, (0, 0x92), (1, 0), (2, 55)))!;
        Assert.Equal(55, pro.Percent); Assert.Equal("UWB base station", pro.Via);
        Assert.Null(SteelSeriesDriver.ParseRivalPro(0x1880, Frame(6, (0, 0x92), (1, 0), (2, 200))));
        Assert.Null(SteelSeriesDriver.ParseRivalPro(0x1880, Frame(6, (0, 0x91))));
    }

    [Fact]
    public void AeroxModelsHaveUniqueProductIds() =>
        Assert.Equal(SteelSeriesDriver.Aerox.Length, SteelSeriesDriver.Aerox.Select(a => a.Pid).Distinct().Count());

    // ------------------------------------------------------------------ contexte et utilitaires
    static HidColl C(ushort vid, ushort pid, int iface = -1, ushort page = 0, ushort usage = 0) => new() { Vid = vid, Pid = pid, Iface = iface, Page = page, Usage = usage };

    [Fact]
    public void ContextClaimsFindsAndKeysDevices()
    {
        var ctx = new PollContext(new List<HidColl> { C(1, 10), C(1, 10, 2), C(1, 11), C(2, 20) });
        Assert.Equal(new[] { ((ushort)1, (ushort)10), ((ushort)1, (ushort)11) }, ctx.Products(1).ToArray());
        Assert.Equal(2, ctx.Find(1, 10).Count);
        Assert.Single(ctx.Find(1, 10, c => c.Iface == 2));
        ctx.Add(new DeviceStatus { Key = "a", Percent = 50 }, 1, 10);
        Assert.True(ctx.IsClaimed(1, 10));
        Assert.Equal(new[] { ((ushort)1, (ushort)11) }, ctx.Products(1).ToArray());
        Assert.Equal(1, ctx.Out[0].Vid);
        Assert.Equal("a#2", ctx.StableKey("a"));
        Assert.Equal("b", ctx.StableKey("b"));
        ctx.Add(new DeviceStatus { Key = "a#2", Percent = 40 }, 1, 11);
        Assert.Equal("a#3", ctx.StableKey("a"));
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_1038&pid_1252&mi_03#7&abc&0&0000#{guid}", 3)]
    [InlineData(@"\\?\HID#VID_1038&PID_1252&MI_0A#xxx", 10)]
    [InlineData(@"\\?\hid#vid_1038&pid_1252#xxx", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void InterfaceNumberIsReadFromTheDevicePath(string? path, int expected) => Assert.Equal(expected, HidColl.InterfaceFromPath(path));

    [Fact]
    public void LevelConversionHelpersAreClampedAndMonotonic()
    {
        Assert.Equal(0, HidUtil.MapTo100(0x50, 0x64, 0x9A));
        Assert.Equal(100, HidUtil.MapTo100(0xFF, 0x64, 0x9A));
        Assert.Equal(50, HidUtil.MapTo100(4, 0, 8));
        Assert.Equal(0, HidUtil.MapTo100(5, 3, 3));
        Assert.Equal(100, HidUtil.Clamp100(500)); Assert.Equal(0, HidUtil.Clamp100(-5));
        Assert.Equal("00AB", HidUtil.Hex4(0xAB));
        int[] mv = { 4200, 3900, 3700, 3300 }, pct = { 100, 80, 40, 0 };
        Assert.Equal(100, HidUtil.Interpolate(5000, mv, pct));
        Assert.Equal(0, HidUtil.Interpolate(2000, mv, pct));
        Assert.Equal(60, HidUtil.Interpolate(3800, mv, pct));
        int prev = -1;
        for (int v = 3000; v <= 4400; v += 10) { int p = HidUtil.Interpolate(v, mv, pct); Assert.True(p >= prev); prev = p; }
    }

    [Fact]
    public void OnlyConnectedDevicesWithALevelBecomeReadings()
    {
        var list = HidDriversProvider.ToReadings(new[]
        {
            new DeviceStatus { Key = "a", Name = "A", Percent = 80, Kind = DeviceKind.Mouse, Charging = true, Via = "USB cable" },
            new DeviceStatus { Key = "b", Name = "B", Percent = -1 },
            new DeviceStatus { Key = "c", Name = "C", Percent = 50, Connected = false },
            new DeviceStatus { Key = "d", Name = "D", Percent = 101 },
            new DeviceStatus { Key = "", Name = "E", Percent = 50 },
        }, DateTime.Now);
        var only = Assert.Single(list);
        Assert.Equal("hid-a", only.Id); Assert.Equal(ChargeState.Charging, only.Charge); Assert.Equal("USB cable", only.Detail);
        Assert.Equal("hid", ReadingMerge.SourceOf(only.Id));
    }

    [Fact]
    public void RealEnumerationAndPollingNeverThrowAndStayInRange()
    {
        var provider = new HidDriversProvider();
        var readings = provider.ReadAll(); // utilise les vrais périphériques de ce PC, sans en exiger aucun
        Assert.All(readings, r => Assert.InRange(r.Percent!.Value, 0, 100));
    }

    // ------------------------------------------------------------------ robustesse
    [Fact]
    public void ParsersSurviveRandomFramesOfAnyLength()
    {
        var rnd = new Random(2026);
        for (int i = 0; i < 20_000; i++)
        {
            var frame = new byte[rnd.Next(0, 80)];
            rnd.NextBytes(frame);
            void Check(DeviceStatus? s) { if (s != null) Assert.True(s.Percent is >= -1 and <= 100, $"niveau hors bornes : {s.Percent}"); }
            ushort pid = (ushort)rnd.Next(0, 0x2400);
            Check(SteelSeriesDriver.ParseArctis1(pid, frame)); Check(SteelSeriesDriver.ParseArctis7(pid, frame));
            Check(SteelSeriesDriver.ParseArctis9(pid, frame)); Check(SteelSeriesDriver.ParseNovaProWireless(pid, frame));
            Check(SteelSeriesDriver.ParseNovaFamily(pid, frame)); Check(SteelSeriesDriver.ParseRival(pid, frame));
            Check(SteelSeriesDriver.ParseRivalPro(pid, frame)); Check(SteelSeriesDriver.ParseArctisProWireless(pid, frame, frame));
            foreach (var m in SteelSeriesDriver.Aerox) Check(SteelSeriesDriver.ParseAerox(m, frame));
            var g = GloriousDriver.Parse(frame);
            Assert.True(g.Percent is >= -1 and <= 100);
        }
    }
}
