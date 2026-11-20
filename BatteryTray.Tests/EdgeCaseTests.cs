using System.Diagnostics;
using Xunit;

namespace BatteryTray.Tests;

/// <summary>Cas limites : horloge qui recule, longues absences, fichiers abîmés, valeurs extrêmes.</summary>
public class EdgeCaseTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-edge-" + Guid.NewGuid().ToString("N"));
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    public EdgeCaseTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static BatteryReading R(string id, int? p, DateTime at, string? charge = null, double? hours = null, bool lower = false, bool offline = false) =>
        new(id, "Test", DeviceKind.Headset, p, charge, null, at) { AutonomyHours = hours, AutonomyLowerBound = lower, Offline = offline };

    // ------------------------------------------------------------------ AutonomyRatchet
    [Fact]
    public void RatchetAfterLongAbsenceDoesNotShowZero()
    {
        // application fermée 2 jours (PC éteint) : retirer 48 h de la durée précédente afficherait « 0 h » jusqu'à la prochaine charge
        var r = new AutonomyRatchet();
        r.Apply(new[] { R("d", 60, T0, hours: 40) });
        var back = r.Apply(new[] { R("d", 60, T0.AddDays(2), hours: 30) })[0];
        Assert.Equal(30, back.AutonomyHours);
    }

    [Fact]
    public void RatchetNeverGoesNegativeOrNaN()
    {
        var r = new AutonomyRatchet();
        r.Apply(new[] { R("d", 60, T0, hours: 1) });
        var next = r.Apply(new[] { R("d", 59, T0.AddMinutes(50), hours: 5) })[0];
        Assert.InRange(next.AutonomyHours!.Value, 0, 1);
    }

    [Fact]
    public void RatchetKeepsStateAcrossRestart()
    {
        string file = Path.Combine(_dir, "shown.json");
        var a = new AutonomyRatchet(file);
        a.Apply(new[] { R("d", 60, T0, hours: 40) });
        a.Flush();
        var b = new AutonomyRatchet(file);
        var after = b.Apply(new[] { R("d", 59, T0.AddMinutes(10), hours: 45) })[0];
        Assert.True(after.AutonomyHours < 40);
    }

    [Fact]
    public void RatchetSurvivesCorruptStateFile()
    {
        string file = Path.Combine(_dir, "shown.json");
        File.WriteAllText(file, "{ pas du json ]]");
        var r = new AutonomyRatchet(file);
        Assert.Equal(12, r.Apply(new[] { R("d", 50, T0, hours: 12) })[0].AutonomyHours);
    }

    [Fact]
    public void RatchetLeavesOfflineNullPercentAndLowerBoundAlone()
    {
        var r = new AutonomyRatchet();
        r.Apply(new[] { R("d", 60, T0, hours: 10) });
        var list = r.Apply(new[]
        {
            R("a", null, T0.AddMinutes(5)),
            R("b", 50, T0.AddMinutes(5), hours: 24, lower: true),
            R("d", 60, T0.AddMinutes(5), hours: 99, offline: true),
        });
        Assert.Null(list[0].AutonomyHours);
        Assert.Equal(24, list[1].AutonomyHours);
        Assert.Equal(99, list[2].AutonomyHours);
    }

    [Fact]
    public void RatchetClockGoingBackwardKeepsValueFinite()
    {
        var r = new AutonomyRatchet();
        r.Apply(new[] { R("d", 60, T0, hours: 20) });
        var back = r.Apply(new[] { R("d", 60, T0.AddHours(-3), hours: 25) })[0];
        Assert.InRange(back.AutonomyHours!.Value, 0, 25);
    }

    // ------------------------------------------------------------------ AutonomyEstimator
    AutonomyEstimator NewEstimator(string name = "h.json") => new(Path.Combine(_dir, name));

    static void AssertSane(BatteryReading r)
    {
        if (r.AutonomyHours is double h) { Assert.False(double.IsNaN(h) || double.IsInfinity(h)); Assert.True(h >= 0); }
        if (r.ChargeEtaHours is double c) { Assert.False(double.IsNaN(c) || double.IsInfinity(c)); Assert.True(c >= 0); }
    }

    [Fact]
    public void EstimatorHandlesNullPercent()
    {
        var e = NewEstimator();
        var r = e.Apply(new[] { R("d", null, T0) })[0];
        Assert.Null(r.Percent);
    }

    [Fact]
    public void EstimatorHandlesClockGoingBackward()
    {
        var e = NewEstimator();
        BatteryReading last = null!;
        int[] levels = { 80, 80, 79, 79, 78, 78, 77 };
        for (int i = 0; i < levels.Length; i++)
            last = e.Apply(new[] { R("d", levels[i], T0.AddMinutes(i % 2 == 0 ? i * 10 : -i * 10)) })[0];
        AssertSane(last);
    }

    [Fact]
    public void EstimatorHandlesHugeDropAndZeroPercent()
    {
        var e = NewEstimator();
        foreach (var (p, m) in new[] { (100, 0), (20, 2), (5, 4), (1, 6), (0, 8), (0, 10) })
            AssertSane(e.Apply(new[] { R("d", p, T0.AddMinutes(m)) })[0]);
    }

    [Fact]
    public void EstimatorHandlesFlappingLevels()
    {
        var e = NewEstimator();
        for (int i = 0; i < 200; i++)
            AssertSane(e.Apply(new[] { R("d", 50 + i % 2, T0.AddMinutes(i)) })[0]);
    }

    [Fact]
    public void EstimatorHandlesSimultaneousSameTimestamps()
    {
        var e = NewEstimator();
        for (int i = 0; i < 10; i++)
            AssertSane(e.Apply(new[] { R("d", 70 - i, T0) })[0]);
    }

    [Fact]
    public void EstimatorSurvivesCorruptHistoryFile()
    {
        string file = Path.Combine(_dir, "hist.json");
        File.WriteAllText(file, "\0\0\0 {{{ corrompu");
        var e = new AutonomyEstimator(file);
        AssertSane(e.Apply(new[] { R("d", 50, T0) })[0]);
    }

    [Fact]
    public void EstimatorHandlesEmptyAndHugeReadingLists()
    {
        var e = NewEstimator();
        Assert.Empty(e.Apply(Array.Empty<BatteryReading>()));
        var many = Enumerable.Range(0, 300).Select(i => R("dev" + i, 50, T0)).ToList();
        Assert.Equal(300, e.Apply(many).Count);
    }

    [Fact]
    public void EstimatorChargeRoundTripNeverNegative()
    {
        var e = NewEstimator();
        BatteryReading r = null!;
        for (int m = 0; m <= 120; m += 3)
            r = e.Apply(new[] { R("d", Math.Min(100, 20 + m * 2 / 3), T0.AddMinutes(m), ChargeState.Charging) })[0];
        AssertSane(r);
        AssertSane(e.Apply(new[] { R("d", 100, T0.AddMinutes(130), ChargeState.Full) })[0]);
        AssertSane(e.Apply(new[] { R("d", 99, T0.AddMinutes(140)) })[0]);
    }

    [Fact]
    public void DisplayedDurationNeverRisesThroughEstimatorAndRatchetTogether()
    {
        // chaîne réelle : estimation puis garde-fou ; un niveau qui baisse ne doit jamais faire monter la durée affichée
        var e = NewEstimator();
        var ratchet = new AutonomyRatchet();
        var rnd = new Random(1234);
        double level = 90; double? previous = null; int? prevPercent = null; DateTime prevAt = T0;
        for (int m = 0; m < 24 * 60; m += 1)
        {
            // décharge à vitesse variable (usage irrégulier)
            level -= rnd.NextDouble() < 0.2 ? 0.05 : 0.01;
            if (level < 3) break;
            var at = T0.AddMinutes(m);
            var shown = ratchet.Apply(e.Apply(new[] { new BatteryReading("hs", "Logitech PRO X 2", DeviceKind.Headset, (int)Math.Round(level), null, null, at) }))[0];
            if (shown.AutonomyHours is double h && !shown.AutonomyLowerBound)
            {
                if (previous is double p && prevPercent is int pp && shown.Percent <= pp)
                    Assert.True(h <= p + 1e-9, $"la durée est remontée de {p:F2} à {h:F2} h à {m} min");
                previous = h; prevPercent = shown.Percent; prevAt = at;
            }
        }
        Assert.NotNull(previous);
    }

    // ------------------------------------------------------------------ traductions
    [Fact]
    public void EveryTranslationUsesTheSamePlaceholdersAsFrench()
    {
        var tablesField = typeof(Loc).GetField("Tables", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("Tables introuvable");
        var tables = (System.Collections.IDictionary)tablesField.GetValue(null)!;
        var fr = (IDictionary<string, string>)tables["fr"]!;
        var rx = new System.Text.RegularExpressions.Regex(@"\{(\d+)[^}]*\}");
        foreach (System.Collections.DictionaryEntry lang in tables)
        {
            var table = (IDictionary<string, string>)lang.Value!;
            foreach (var (key, frText) in fr)
            {
                if (!table.TryGetValue(key, out var text)) continue;
                var expected = rx.Matches(frText).Select(m => m.Groups[1].Value).ToHashSet();
                var actual = rx.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
                Assert.True(expected.SetEquals(actual), $"{lang.Key}/{key} : paramètres {{{string.Join(",", actual)}}} au lieu de {{{string.Join(",", expected)}}}");
                // le formatage ne doit jamais lever d'exception
                string formatted = string.Format(text, "A", "B", "C", "D", "E");
                Assert.False(string.IsNullOrWhiteSpace(formatted) && !string.IsNullOrWhiteSpace(frText));
            }
        }
    }

    [Fact]
    public void UnknownLanguageCodeFallsBackToFrench()
    {
        string before = Loc.Current;
        try { Loc.Set("klingon"); Assert.Equal("fr", Loc.Current); Loc.Set(""); Assert.Equal("fr", Loc.Current); }
        finally { Loc.Set(before); }
    }

    // ------------------------------------------------------------------ script de surveillance
    [Fact]
    public void SurveillanceScriptHasValidSyntaxAndDoesNothingWithoutExe()
    {
        string script = Path.Combine(_dir, "surveiller.vbs");
        File.WriteAllText(script, Watchdog.ScriptText(), System.Text.Encoding.Default);
        var psi = new ProcessStartInfo("cscript.exe", $"//nologo \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(20000), "le script ne se termine pas");
        Assert.True(p.ExitCode == 0, "erreur de script : " + err);
        Assert.False(File.Exists(Path.Combine(_dir, "BatteryTray.exe")));
    }
}
