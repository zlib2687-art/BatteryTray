using Xunit;

namespace BatteryTray.Tests;

public class BatteryHistoryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-hist-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    public BatteryHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static BatteryReading R(int? p, string id = "a", string? charge = null, bool offline = false, bool estimated = false) =>
        new(id, "Casque", DeviceKind.Headset, p, charge, null, T0) { Offline = offline, PercentEstimated = estimated };

    [Fact]
    public void FirstReadingIsRecordedThenOneEveryThirtyMinutes()
    {
        var h = new BatteryHistory();
        Assert.Equal(1, h.Record(new[] { R(80) }, T0));
        Assert.Equal(0, h.Record(new[] { R(79) }, T0.AddMinutes(10)));
        Assert.Equal(0, h.Record(new[] { R(79) }, T0.AddMinutes(29)));
        Assert.Equal(1, h.Record(new[] { R(78) }, T0.AddMinutes(30)));
        Assert.Equal(2, h.Points("a", TimeSpan.FromDays(1), T0.AddHours(1)).Count);
    }

    [Fact]
    public void ChargingStateChangeIsRecordedAfterOneMinuteOnly()
    {
        var h = new BatteryHistory();
        h.Record(new[] { R(50) }, T0);
        Assert.Equal(0, h.Record(new[] { R(50, charge: ChargeState.Charging) }, T0.AddSeconds(30)));
        Assert.Equal(1, h.Record(new[] { R(51, charge: ChargeState.Charging) }, T0.AddSeconds(61)));
        var points = h.Points("a", TimeSpan.FromDays(1), T0.AddMinutes(5));
        Assert.True(points[^1].Charging);
    }

    [Fact]
    public void OnlyRealReadingsOfConnectedDevicesAreKept()
    {
        var h = new BatteryHistory();
        Assert.Equal(0, h.Record(new[]
        {
            R(60, offline: true), R(null), R(60, estimated: true), R(101), R(-1), R(int.MaxValue), R(60, id: ""),
        }, T0));
        Assert.Empty(h.Devices());
        Assert.Equal(1, h.Record(new[] { R(0), R(100, id: "b") }, T0) - 1); // 0 % et 100 % sont valides : 2 ajoutés
    }

    [Fact]
    public void DataOlderThanThirtyDaysIsDropped()
    {
        var h = new BatteryHistory();
        h.Record(new[] { R(80) }, T0);
        h.Record(new[] { R(70) }, T0.AddDays(31));
        var all = h.Points("a", TimeSpan.FromDays(60), T0.AddDays(31));
        Assert.Single(all);
        Assert.Equal(70, all[0].P);
    }

    [Fact]
    public void PointsAreFilteredByRange()
    {
        var h = new BatteryHistory();
        for (int i = 0; i < 10; i++) h.Record(new[] { R(90 - i) }, T0.AddHours(i * 6));
        var now = T0.AddHours(54);
        Assert.Equal(5, h.Points("a", TimeSpan.FromHours(24), now).Count);
        Assert.Equal(10, h.Points("a", TimeSpan.FromDays(7), now).Count);
        Assert.Empty(h.Points("inconnu", TimeSpan.FromDays(7), now));
    }

    [Fact]
    public void ClockGoingBackwardNeverCorruptsTheSeries()
    {
        var h = new BatteryHistory();
        h.Record(new[] { R(80) }, T0);
        h.Record(new[] { R(79) }, T0.AddHours(-3)); // horloge reculée
        h.Record(new[] { R(78) }, T0.AddMinutes(40));
        var pts = h.Points("a", TimeSpan.FromDays(3), T0.AddHours(1));
        Assert.All(pts, p => Assert.InRange(p.P, 0, 100));
    }

    [Fact]
    public void SummaryGivesLatestLowestHighestAndChange()
    {
        var pts = new[] { new BatteryHistory.Point(T0, 90, false), new(T0.AddHours(1), 60, false), new(T0.AddHours(2), 75, false) };
        var s = BatteryHistory.Summarize(pts);
        Assert.Equal(3, s.Count); Assert.Equal(75, s.Latest!.P); Assert.Equal(60, s.Lowest); Assert.Equal(90, s.Highest); Assert.Equal(-15, s.Change);
        var empty = BatteryHistory.Summarize(Array.Empty<BatteryHistory.Point>());
        Assert.Equal(0, empty.Count); Assert.Null(empty.Latest);
    }

    [Fact]
    public void LongGapsBreakTheLineInsteadOfInventingValues()
    {
        var pts = new[]
        {
            new BatteryHistory.Point(T0, 80, false), new(T0.AddMinutes(30), 79, false),
            new(T0.AddHours(10), 60, false), new(T0.AddHours(10.5), 59, false),
        };
        var seg = BatteryHistory.Segments(pts, TimeSpan.FromHours(2.5));
        Assert.Equal(2, seg.Count);
        Assert.Equal(2, seg[0].Count); Assert.Equal(2, seg[1].Count);
        Assert.Empty(BatteryHistory.Segments(Array.Empty<BatteryHistory.Point>(), TimeSpan.FromHours(1)));
        Assert.Single(BatteryHistory.Segments(pts, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void HistorySurvivesRestartAndClearing()
    {
        string path = Path.Combine(_dir, "h.json");
        var a = new BatteryHistory(path);
        a.Record(new[] { R(80), R(70, id: "b") }, T0);
        a.Flush();
        var b = new BatteryHistory(path);
        Assert.Equal(2, b.Devices().Count);
        Assert.Equal("Casque", b.Devices()[0].Name);
        b.Clear("a");
        Assert.Single(new BatteryHistory(path).Devices());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Devices\":null,\"Names\":null}")]
    [InlineData("{\"Devices\":{\"a\":null,\"b\":[null,{\"T\":\"2026-11-20T12:00:00\",\"P\":900,\"Charging\":false},{\"T\":\"2026-11-20T11:00:00\",\"P\":50,\"Charging\":true}]}}")]
    public void CorruptHistoryFilesNeverBreakStartupOrRecording(string content)
    {
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, content);
        var h = new BatteryHistory(path);
        foreach (var (id, _) in h.Devices())
            Assert.All(h.Points(id, TimeSpan.FromDays(400), T0.AddYears(1)), p => Assert.InRange(p.P, 0, 100));
        Assert.Equal(1, h.Record(new[] { R(55, id: "nouveau") }, T0));
    }

    [Fact]
    public void NumberOfTrackedDevicesAndPointsIsBounded()
    {
        var h = new BatteryHistory();
        for (int i = 0; i < BatteryHistory.MaxDevices + 10; i++) h.Record(new[] { R(50, id: "d" + i) }, T0);
        Assert.Equal(BatteryHistory.MaxDevices, h.Devices().Count);
        for (int i = 0; i < BatteryHistory.MaxPointsPerDevice + 50; i++) h.Record(new[] { R(50, id: "d0") }, T0.AddMinutes(30 * (i + 1)));
        Assert.True(h.Points("d0", TimeSpan.FromDays(10_000), T0.AddDays(10_000)).Count <= BatteryHistory.MaxPointsPerDevice);
    }

    [Fact]
    public void HistoryWindowDrawsInEveryLanguageAndRange()
    {
        var failures = new List<string>();
        var thread = new Thread(() =>
        {
            string before = Loc.Current;
            try
            {
                Application.EnableVisualStyles();
                var h = new BatteryHistory();
                var now = DateTime.Now;
                for (int i = 100; i >= 0; i--) h.Record(new[] { R(100 - i / 2) }, now.AddHours(-i));
                foreach (bool light in new[] { true, false })
                foreach (var (code, _) in Loc.Languages)
                {
                    try
                    {
                        SettingsTheme.Forced = light; Loc.Set(code);
                        using var form = new HistoryForm(h);
                        for (int range = 0; range < 3; range++)
                        {
                            form.SetRange(range);
                            using var bmp = form.RenderPreview();
                            var colors = new HashSet<int>();
                            for (int x = 0; x < bmp.Width; x += 9) for (int y = 0; y < bmp.Height; y += 9) colors.Add(bmp.GetPixel(x, y).ToArgb());
                            if (colors.Count < 6) failures.Add($"{code}/{(light ? "clair" : "sombre")}/{range} : image presque unie");
                        }
                    }
                    catch (Exception e) { failures.Add($"{code}/{(light ? "clair" : "sombre")} : {e.GetType().Name} {e.Message}"); }
                }
            }
            finally { SettingsTheme.Forced = null; Loc.Set(before); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "la fenêtre ne répond pas");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
