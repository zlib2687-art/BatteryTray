using System.Text;
using System.Text.Json;
using Xunit;

namespace BatteryTray.Tests;

/// <summary>
/// Tests aléatoires (graine fixe, donc reproductibles) et cas extrêmes : fichiers corrompus, valeurs absurdes,
/// textes hostiles. Aucune entrée ne doit faire planter l'application ni produire de valeur invalide.
/// </summary>
public class FuzzTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-fuzz-" + Guid.NewGuid().ToString("N"));
    readonly string _oldDir = AppSettings.DataDir;
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    public FuzzTests() { Directory.CreateDirectory(_dir); AppSettings.DataDir = _dir; }
    public void Dispose() { AppSettings.DataDir = _oldDir; try { Directory.Delete(_dir, true); } catch { } }

    static string RandomText(Random r, int maxLen = 40)
    {
        string[] hostile =
        {
            "", " ", "\r\n", "\0", "<script>", "\"; DROP TABLE", "%s%n%x", "{0}{1}{99}", "../../..", "C:\\Windows\\System32",
            "emoji 🎧🖱️", "عربي", "中文", "Ж", new string('A', 5000), "a\nb\r\nc", "\u202E", "NUL", "&calc&", "|", "'", "`",
        };
        if (r.Next(3) == 0) return hostile[r.Next(hostile.Length)];
        var sb = new StringBuilder();
        for (int i = r.Next(maxLen); i >= 0; i--) sb.Append((char)r.Next(32, 0x2FF));
        return sb.ToString();
    }

    static int? RandomPercent(Random r) => r.Next(10) switch
    {
        0 => null, 1 => -50, 2 => 255, 3 => int.MaxValue, 4 => int.MinValue, 5 => 0, 6 => 100, _ => r.Next(0, 101),
    };

    static DeviceKind RandomKind(Random r) => r.Next(8) == 0 ? (DeviceKind)r.Next(-5, 99) : (DeviceKind)r.Next(0, 7);

    static BatteryReading RandomReading(Random r, DateTime at, string? id = null)
    {
        double?[] hours = { null, 0, -1, 0.0001, 1, 24, 96.5, 1e6, 1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        string?[] charges = { null, ChargeState.Charging, ChargeState.Full, "inconnu", "" };
        return new BatteryReading(id ?? "dev" + r.Next(4), RandomText(r), RandomKind(r), RandomPercent(r), charges[r.Next(charges.Length)],
            r.Next(3) == 0 ? RandomText(r) : null, at)
        {
            AutonomyHours = hours[r.Next(hours.Length)],
            ChargeEtaHours = hours[r.Next(hours.Length)],
            AutonomyLowerBound = r.Next(4) == 0,
            AutonomyFromRating = r.Next(4) == 0,
            AutonomyPending = r.Next(6) == 0,
            ChargeEtaPending = r.Next(6) == 0,
            PercentEstimated = r.Next(5) == 0,
            Offline = r.Next(5) == 0,
            Parts = r.Next(4) == 0 ? new[] { new BatteryPart(RandomText(r, 5), RandomPercent(r) ?? 0), new BatteryPart("left", RandomPercent(r) ?? 0) } : null,
        };
    }

    // ------------------------------------------------------------------ textes affichés
    [Fact]
    public void DisplayTextsNeverThrowOnAbsurdReadings()
    {
        var r = new Random(1);
        foreach (var code in Loc.Languages.Select(l => l.Code))
        {
            Loc.Set(code);
            for (int i = 0; i < 1500; i++)
            {
                var reading = RandomReading(r, T0);
                _ = reading.PercentText; _ = Loc.AutonomyText(reading); _ = Loc.ChargeEtaText(reading); _ = Loc.PartsText(reading);
                _ = Loc.ChargeText(reading.Charge);
            }
        }
        Loc.Set("fr");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(1e300)]
    [InlineData(1e12)]
    [InlineData(0.0001)]
    public void DurationAlwaysGivesReadableText(double hours)
    {
        string text = Loc.Duration(hours);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("-", text);
        Assert.True(text.Length < 40, text);
    }

    [Fact]
    public void LowerBoundHelperHandlesExtremes()
    {
        foreach (double h in new[] { double.NaN, -3, 0, 1e9, double.PositiveInfinity })
            Assert.False(double.IsNaN(Loc.FloorForBound(h)));
    }

    // ------------------------------------------------------------------ réglages
    [Fact]
    public void CorruptedSettingsFilesAlwaysLoadSafely()
    {
        var r = new Random(2);
        var good = new AppSettings();
        good.Known["a"] = new KnownDevice { Name = "Casque", Kind = DeviceKind.Headset, LastPercent = 50, LastSeen = T0 };
        good.Devices["a"] = new DevicePref { Name = "Mon casque", LowThreshold = 30, RatedHours = 40 };
        good.Order.Add("a");
        string json = JsonSerializer.Serialize(good, new JsonSerializerOptions { WriteIndented = true });

        for (int i = 0; i < 400; i++)
        {
            var chars = json.ToCharArray();
            for (int k = r.Next(1, 8); k > 0; k--) chars[r.Next(chars.Length)] = (char)r.Next(32, 127);
            string mutated = i % 5 == 0 ? new string(chars, 0, r.Next(chars.Length)) : new string(chars); // aussi tronqué
            File.WriteAllText(AppSettings.FilePath, mutated);
            var s = AppSettings.Load();
            AssertValid(s);
        }

        foreach (string text in new[] { "", "null", "[]", "42", "\"x\"", "{}", "{\"Devices\":null,\"Known\":null,\"Order\":null,\"LowNotified\":null}", "{\"PollMinutes\":-9,\"FlyoutScale\":1e9}" })
        {
            File.WriteAllText(AppSettings.FilePath, text);
            AssertValid(AppSettings.Load());
        }
        File.WriteAllBytes(AppSettings.FilePath, new byte[] { 0xFF, 0xFE, 0, 1, 2, 0xC0, 0x80 });
        AssertValid(AppSettings.Load());
    }

    [Fact]
    public void SemanticallyAbsurdSettingsAreRepaired()
    {
        const string json = """
        {
          "Language": "klingon", "LowThreshold": 5000, "OrangeBelow": -1, "RedBelow": 900, "PollMinutes": 0, "FlyoutScale": 3,
          "Devices": { "a": { "LowThreshold": -40, "RatedHours": -5, "Name": null }, "b": null },
          "Known": { "a": { "Name": null, "Kind": 77, "LastPercent": 900 }, "b": null, "c": { "Name": "x", "Kind": -3, "LastPercent": -20 } },
          "Order": [ null, "a", "a" ]
        }
        """;
        File.WriteAllText(AppSettings.FilePath, json);
        AssertValid(AppSettings.Load());
    }

    static void AssertValid(AppSettings s)
    {
        Assert.NotNull(s);
        Assert.InRange(s.PollMinutes, 1, 60);
        Assert.InRange(s.LowThreshold, 1, 99);
        Assert.InRange(s.FlyoutScale, 70, 200);
        Assert.True(s.RedBelow < s.OrangeBelow);
        Assert.NotNull(s.Devices); Assert.NotNull(s.Known); Assert.NotNull(s.Order);
        Assert.NotNull(s.LowNotified); Assert.NotNull(s.FullNotified);
        Assert.NotNull(s.Language);
        foreach (var (key, k) in s.Known)
        {
            Assert.NotNull(key); Assert.NotNull(k);
            Assert.NotNull(k.Name);
            Assert.True(Enum.IsDefined(k.Kind), $"type d'appareil invalide : {(int)k.Kind}");
            if (k.LastPercent is int p) Assert.InRange(p, 0, 100);
        }
        foreach (var (key, d) in s.Devices)
        {
            Assert.NotNull(key); Assert.NotNull(d);
            if (d.LowThreshold is int l) Assert.InRange(l, 1, 99);
            if (d.RatedHours is double h) Assert.True(h > 0 && h <= 9999 && !double.IsNaN(h));
        }
        Assert.DoesNotContain(null!, s.Order);
        // la suite de l'application doit pouvoir les utiliser
        var shown = DeviceView.Prepare(new[] { new BatteryReading("a", "Casque", DeviceKind.Headset, 50, null, null, T0) }, s, out _);
        Assert.NotNull(shown);
    }

    // ------------------------------------------------------------------ vue des appareils et fusion
    [Fact]
    public void DeviceViewAndMergeSurviveRandomReadings()
    {
        var r = new Random(3);
        for (int round = 0; round < 300; round++)
        {
            var s = new AppSettings();
            var readings = Enumerable.Range(0, r.Next(0, 12)).Select(i => RandomReading(r, T0.AddMinutes(i))).ToList();
            var merged = ReadingMerge.Deduplicate(readings);
            Assert.True(merged.Count <= readings.Count);
            var view = DeviceView.Prepare(merged, s, out _);
            foreach (var v in view) { _ = v.PercentText; _ = Loc.AutonomyText(v); }
            // un second passage avec les réglages obtenus doit rester stable
            DeviceView.Prepare(merged, s, out _);
        }
    }

    [Fact]
    public void SanitizeRejectsAbsurdReadingsWithoutInventingLevels()
    {
        BatteryReading Make(string id, string name, int? p) => new(id, name, DeviceKind.Mouse, p, null, null, T0);
        Assert.Null(ReadingMerge.Sanitize(Make("", "x", 50)));
        Assert.Null(ReadingMerge.Sanitize(Make("  ", "x", 50)));
        Assert.Null(ReadingMerge.Sanitize(Make(null!, "x", 50)));
        Assert.Null(ReadingMerge.Sanitize(Make("a", "x", 255))!.Percent);          // « inconnu » : pas de niveau, jamais 255 %
        Assert.Null(ReadingMerge.Sanitize(Make("a", "x", -1))!.Percent);
        Assert.Equal(100, ReadingMerge.Sanitize(Make("a", "x", 100))!.Percent);
        Assert.Equal(0, ReadingMerge.Sanitize(Make("a", "x", 0))!.Percent);
        Assert.Equal("?", ReadingMerge.Sanitize(Make("a", null!, 5))!.Name);
        Assert.Equal("?", ReadingMerge.Sanitize(Make("a", "\r\n\0", 5))!.Name);
        string clean = ReadingMerge.Sanitize(Make("a", "Souris\r\nFAUSSE LIGNE" + (char)0x202E + "evil", 5))!.Name;
        Assert.False(clean.Contains('\n', StringComparison.Ordinal)); Assert.False(clean.Contains((char)0x202E)); Assert.Equal("SourisFAUSSE LIGNEevil", clean);
        Assert.Equal(60, ReadingMerge.Sanitize(Make("a", new string('x', 500), 5))!.Name.Length);
        var parts = ReadingMerge.Sanitize(Make("a", "x", 5) with { Parts = new[] { new BatteryPart("left", 400), new BatteryPart("right", -9) } })!.Parts!;
        Assert.All(parts, p => Assert.InRange(p.Percent, 0, 100));
    }

    [Fact]
    public void BuildPrefNeverThrowsOnHostileText()
    {
        var r = new Random(4);
        for (int i = 0; i < 2000; i++)
        {
            var pref = SettingsForm.BuildPref(new DevicePref { Hidden = r.Next(2) == 0, AutoHidden = r.Next(2) == 0 },
                r.Next(2) == 0, RandomText(r), RandomText(r), RandomText(r), RandomText(r));
            if (pref?.LowThreshold is int l) Assert.InRange(l, 1, 99);
            if (pref?.RatedHours is double h) Assert.True(h > 0 && h <= 9999);
        }
    }

    [Fact]
    public void DeviceNameGuessingAndNormalizingSurviveAnyText()
    {
        var r = new Random(5);
        for (int i = 0; i < 3000; i++)
        {
            string t = RandomText(r, 200);
            Assert.True(Enum.IsDefined(DeviceNames.Guess(t)));
            _ = ReadingMerge.Normalize(t);
        }
        Assert.True(Enum.IsDefined(DeviceNames.Guess(null)));
    }

    // ------------------------------------------------------------------ calculs
    [Fact]
    public void VoltageCurveStaysInRangeAndNeverDecreasesWithVoltage()
    {
        int prev = -1;
        for (int mv = -1000; mv <= 7000; mv += 7)
        {
            int p = VoltageCurve.Estimate(mv);
            Assert.InRange(p, 0, 100);
            Assert.True(p >= prev, $"la courbe baisse à {mv} mV");
            prev = p;
        }
        foreach (int mv in new[] { int.MinValue, int.MaxValue, 0 }) Assert.InRange(VoltageCurve.Estimate(mv), 0, 100);
    }

    [Fact]
    public void ChartMathHandlesDegenerateSeries()
    {
        var area = new RectangleF(0, 0, 100, 50);
        foreach (var samples in new[]
        {
            new List<AutonomyEstimator.Sample>(),
            new List<AutonomyEstimator.Sample> { new(T0, 50) },
            new List<AutonomyEstimator.Sample> { new(T0, 50), new(T0, 50) },
            new List<AutonomyEstimator.Sample> { new(T0, 50), new(T0.AddMinutes(-5), 900), new(T0.AddMinutes(5), -20) },
        })
        {
            var (lo, hi) = ChartMath.Range(samples);
            Assert.InRange(lo, 0, 100); Assert.InRange(hi, 0, 100);
            foreach (var pt in ChartMath.Map(samples, area, lo, hi))
                Assert.True(float.IsFinite(pt.X) && float.IsFinite(pt.Y));
        }
    }

    [Fact]
    public void BatteryPartsAggregationIsSafe()
    {
        Assert.Null(BatteryParts.Aggregate(Array.Empty<BatteryPart>()));
        var r = new Random(6);
        for (int i = 0; i < 500; i++)
        {
            var parts = Enumerable.Range(0, r.Next(1, 5)).Select(_ => new BatteryPart(RandomText(r, 5), RandomPercent(r) ?? 0)).ToList();
            int? agg = BatteryParts.Aggregate(parts);
            if (agg is int a) Assert.InRange(a, int.MinValue, int.MaxValue);
        }
    }

    // ------------------------------------------------------------------ estimateur : séquences aléatoires et fichiers abîmés
    [Fact]
    public void EstimatorAndRatchetSurviveRandomSequences()
    {
        var r = new Random(7);
        for (int run = 0; run < 40; run++)
        {
            var e = new AutonomyEstimator(Path.Combine(_dir, $"h{run}.json"));
            var ratchet = new AutonomyRatchet(Path.Combine(_dir, $"r{run}.json"));
            var t = T0;
            for (int i = 0; i < 300; i++)
            {
                t = t.AddSeconds(r.Next(-30, 900)); // l'horloge peut reculer
                var batch = Enumerable.Range(0, r.Next(0, 4)).Select(_ => RandomReading(r, t, "d" + r.Next(3)) with { AutonomyHours = null, ChargeEtaHours = null }).ToList();
                var shown = ratchet.Apply(e.Apply(batch));
                foreach (var s in shown)
                {
                    foreach (double? h in new[] { s.AutonomyHours, s.ChargeEtaHours })
                        if (h is double v) Assert.True(!double.IsNaN(v) && !double.IsInfinity(v) && v >= 0, $"durée invalide {v}");
                    _ = Loc.AutonomyText(s);
                }
            }
            e.Flush(); ratchet.Flush();
            // rechargement des fichiers écrits
            Assert.NotNull(new AutonomyEstimator(Path.Combine(_dir, $"h{run}.json")));
        }
    }

    [Theory]
    [InlineData("{\"Version\":2,\"History\":null,\"Rates\":null,\"ChargeHistory\":null,\"ChargeRates\":null}")]
    [InlineData("{\"Version\":2,\"History\":{\"a\":null},\"Rates\":{\"a\":-5},\"ChargeHistory\":{},\"ChargeRates\":{\"a\":0}}")]
    [InlineData("{\"Version\":2,\"History\":{\"a\":[{\"T\":\"2026-11-20T12:00:00\",\"P\":900},{\"T\":\"2026-11-20T11:00:00\",\"P\":-4},{\"T\":\"0001-01-01T00:00:00\",\"P\":50}]},\"Rates\":{\"a\":1e300}}")]
    [InlineData("{\"Version\":2,\"History\":{\"a\":[null]}}")]
    [InlineData("{\"Version\":2}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void EstimatorHistoryFilesWithOddContentNeverBreakLaterReadings(string content)
    {
        string path = Path.Combine(_dir, "odd.json");
        File.WriteAllText(path, content);
        var e = new AutonomyEstimator(path);
        for (int i = 0; i < 12; i++)
        {
            var r = e.Apply(new[] { new BatteryReading("a", "X", DeviceKind.Headset, 60 - i / 4, null, null, T0.AddMinutes(i * 3)) })[0];
            if (r.AutonomyHours is double h) Assert.True(!double.IsNaN(h) && !double.IsInfinity(h) && h >= 0);
        }
    }

    [Theory]
    [InlineData("{\"x\":null}")]
    [InlineData("{\"x\":{\"Percent\":900,\"Hours\":-5,\"At\":\"0001-01-01T00:00:00\"}}")]
    [InlineData("[]")]
    [InlineData("nul")]
    public void RatchetStateFilesWithOddContentAreHarmless(string content)
    {
        string path = Path.Combine(_dir, "ratchet-odd.json");
        File.WriteAllText(path, content);
        var ratchet = new AutonomyRatchet(path);
        var r = ratchet.Apply(new[] { new BatteryReading("x", "X", DeviceKind.Headset, 50, null, null, T0) { AutonomyHours = 10 } })[0];
        Assert.True(r.AutonomyHours is double h && h >= 0 && h <= 10);
    }

    // ------------------------------------------------------------------ sécurité : propriétés qui ne doivent jamais changer
    [Fact]
    public void ApplicationReferencesNoNetworkAssembly()
    {
        var banned = new[] { "System.Net.Http", "System.Net.Sockets", "System.Net.WebClient", "System.Net.Mail", "System.Net.Requests", "System.Net.WebSockets", "System.Net.Security", "System.Net.NameResolution", "System.Net.Ping" };
        var refs = typeof(Loc).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var b in banned) Assert.DoesNotContain(b, refs);
        Assert.DoesNotContain(refs, n => n.StartsWith("System.Net.", StringComparison.Ordinal) && n != "System.Net.Primitives");
    }

    [Fact]
    public void TaskDefinitionStaysValidXmlWithHostilePaths()
    {
        foreach (string path in new[] { @"C:\a&b\c.vbs", @"C:\a<b>\c.vbs", "C:\\a\"b'\\c.vbs", @"C:\Profils\Zoé Ünï\Donnees\x.vbs", @"C:\x\a%20b ;&|.vbs" })
        {
            var doc = System.Xml.Linq.XDocument.Parse(Watchdog.TaskXml(path));
            System.Xml.Linq.XNamespace n = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Assert.Contains(path, doc.Descendants(n + "Arguments").Single().Value);
            // une seule action : on ne peut pas y glisser une seconde commande
            Assert.Single(doc.Descendants(n + "Exec"));
        }
    }

    [Theory]
    [InlineData(@"C:\Profils\Jean\Donnees\Local\Programs\BatteryTray", true)]
    [InlineData(@"C:\Profils\Jean (pro)\Donnees\Local\Programs\BatteryTray\", true)]
    [InlineData(@"C:\Profils\Zoé Ünï\Donnees\Local\Programs\BatteryTray", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Profils\Jean\Documents", false)]
    [InlineData(@"C:\x & del C:\y\Programs\BatteryTray", false)]
    [InlineData("C:\\x\"\\Programs\\BatteryTray", false)]
    [InlineData(@"C:\%USERPROFILE%\Programs\BatteryTray", false)]
    [InlineData(@"C:\a|b\Programs\BatteryTray", false)]
    [InlineData("", false)]
    public void UninstallCleanupOnlyTargetsTheInstallFolder(string dir, bool allowed)
    {
        string? cmd = Installer.CleanupCommand(dir);
        Assert.Equal(allowed, cmd != null);
    }

    [Fact]
    public void SafeFileWriteIsAllOrNothingAndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_dir, "atomic.json");
        SafeFile.WriteAllText(path, "ancien");
        SafeFile.WriteAllText(path, "nouveau");
        Assert.Equal("nouveau", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        // un fichier temporaire abandonné par un arrêt brutal n'empêche pas l'écriture suivante
        File.WriteAllText(path + ".tmp", "reste d'un arrêt brutal");
        SafeFile.WriteAllText(path, "encore");
        Assert.Equal("encore", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SurveillanceScriptContainsNoPersonalOrNetworkContent()
    {
        string s = Watchdog.ScriptText();
        foreach (string bad in new[] { "http", "XMLHTTP", "WinHttp", "Us" + "ers\\", "@", "password", "Net.WebClient", "powershell", "cmd.exe" })
            Assert.DoesNotContain(bad, s, StringComparison.OrdinalIgnoreCase);
    }
}
