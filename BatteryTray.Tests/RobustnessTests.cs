using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using BatteryTray.Providers;
using Xunit;

namespace BatteryTray.Tests;

// ---------------------------------------------------------------- une lecture bloquée ne fige pas l'application
public class RefreshGateTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    [Fact]
    public void Only_one_refresh_runs_at_a_time()
    {
        var gate = new RefreshGate(TimeSpan.FromMinutes(2));
        long first = gate.TryEnter(T0);
        Assert.True(first > 0);
        Assert.Equal(0, gate.TryEnter(T0.AddSeconds(20)));     // une lecture saine est en cours

        gate.Exit(first);
        Assert.True(gate.TryEnter(T0.AddSeconds(30)) > 0);      // libérée : on peut recommencer
    }

    [Fact]
    public void A_refresh_stuck_for_too_long_is_abandoned_and_a_new_one_can_start()
    {
        var gate = new RefreshGate(TimeSpan.FromMinutes(2));
        long stuck = gate.TryEnter(T0);
        Assert.Equal(0, gate.TryEnter(T0.AddMinutes(1)));

        long fresh = gate.TryEnter(T0.AddMinutes(3));           // bloquée depuis plus de 2 min
        Assert.True(fresh > stuck);
    }

    [Fact]
    public void A_stuck_refresh_that_finally_ends_does_not_free_the_place_of_a_newer_one()
    {
        var gate = new RefreshGate(TimeSpan.FromMinutes(2));
        long stuck = gate.TryEnter(T0);
        long fresh = gate.TryEnter(T0.AddMinutes(3));

        gate.Exit(stuck);                                        // l'ancienne lecture se termine enfin
        Assert.True(gate.IsRunning);                             // la plus récente est toujours en cours
        Assert.Equal(0, gate.TryEnter(T0.AddMinutes(3).AddSeconds(5)));

        gate.Exit(fresh);
        Assert.False(gate.IsRunning);
    }
}

// ---------------------------------------------------------------- fichier de réglages abîmé
public class SettingsRobustnessTests : IDisposable
{
    readonly string _savedDir = AppSettings.DataDir;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-robust-" + Guid.NewGuid().ToString("N"));

    public SettingsRobustnessTests() => AppSettings.DataDir = _dir;

    public void Dispose()
    {
        AppSettings.DataDir = _savedDir;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Null_lists_and_absurd_values_in_the_file_are_repaired_instead_of_crashing_the_start()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppSettings.FilePath, """
            { "Language": null, "PollMinutes": 100000, "LowThreshold": -5, "OrangeBelow": 500, "RedBelow": 900,
              "FlyoutScale": 9, "Devices": null, "Known": null, "Order": null, "LowNotified": null, "FullNotified": null }
            """);

        var s = AppSettings.Load();
        Assert.Equal(60, s.PollMinutes);                  // 100 000 min faisait déborder la minuterie au démarrage
        Assert.Equal(1, s.LowThreshold);
        Assert.True(s.OrangeBelow <= 100 && s.RedBelow < s.OrangeBelow);
        Assert.Equal(70, s.FlyoutScale);
        Assert.Equal("", s.Language);
        Assert.NotNull(s.Devices); Assert.NotNull(s.Known); Assert.NotNull(s.Order);
        Assert.NotNull(s.LowNotified); Assert.NotNull(s.FullNotified);
        Assert.True(TimeSpan.FromMinutes(s.PollMinutes).TotalMilliseconds < int.MaxValue); // valide pour une minuterie
    }

    [Fact]
    public void Null_entries_inside_the_dictionaries_are_dropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppSettings.FilePath, """{ "Devices": { "a": null, "b": { "Hidden": true } }, "Known": { "c": null } }""");

        var s = AppSettings.Load();
        Assert.False(s.Devices.ContainsKey("a"));
        Assert.True(s.Devices["b"].Hidden);
        Assert.Empty(s.Known);
        Assert.False(s.PrefFor("a").Hidden); // aucune exception sur un appareil sans préférence
    }

    [Fact]
    public void A_file_that_is_not_json_gives_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppSettings.FilePath, "\0\0\0 pas du json");
        Assert.Equal(20, AppSettings.Load().LowThreshold);
    }
}

// ---------------------------------------------------------------- « OK » dans les paramètres
public class SettingsCommitTests
{
    [Fact]
    public void Clicking_OK_keeps_an_automatic_hiding_automatic()
    {
        // cas réel : la G502 est masquée d'office (aucun niveau), la case est décochée, l'utilisateur clique sur OK
        var old = new DevicePref { Hidden = true, AutoHidden = true };
        var kept = SettingsForm.BuildPref(old, show: false, typedName: "", originalName: "G502", lowText: "", ratedText: "");
        Assert.NotNull(kept);
        Assert.True(kept!.Hidden);
        Assert.True(kept.AutoHidden);   // l'appareil réapparaîtra tout seul dès qu'il aura un niveau

        // appliqué à la lecture suivante
        var s = new AppSettings();
        s.Known["g"] = new KnownDevice { Name = "G502", Kind = DeviceKind.Mouse };
        s.Devices["g"] = kept;
        var view = DeviceView.Prepare(new[] { new BatteryReading("g", "G502", DeviceKind.Mouse, 99, null, null, DateTime.Now) }, s, out _);
        Assert.Single(view);
    }

    [Fact]
    public void A_hiding_chosen_by_the_user_stays_final()
    {
        var old = new DevicePref { Hidden = false };               // visible jusqu'ici
        var chosen = SettingsForm.BuildPref(old, show: false, "", "Souris", "", "");
        Assert.True(chosen!.Hidden);
        Assert.False(chosen.AutoHidden);                            // décision de l'utilisateur : jamais défaite automatiquement
    }

    [Fact]
    public void Ticking_an_automatically_hidden_device_shows_it()
    {
        var old = new DevicePref { Hidden = true, AutoHidden = true };
        Assert.Null(SettingsForm.BuildPref(old, show: true, "", "G502", "", "")); // visible et sans réglage : préférence supprimée
    }

    [Fact]
    public void Name_threshold_and_rated_hours_are_read_from_the_row()
    {
        var pref = SettingsForm.BuildPref(new DevicePref(), show: true, "  Ma souris ", "G502", "30", "25,5");
        Assert.Equal("Ma souris", pref!.Name);
        Assert.Equal(30, pref.LowThreshold);
        Assert.Equal(25.5, pref.RatedHours);

        Assert.Null(SettingsForm.BuildPref(new DevicePref(), true, "G502", "G502", "abc", "-3")); // nom d'origine, valeurs invalides
    }
}

// ---------------------------------------------------------------- écritures disque de l'historique
public class EstimatorWriteTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-write-" + Guid.NewGuid().ToString("N"));
    readonly DateTime _t0 = new(2026, 11, 20, 10, 0, 0);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static BatteryReading R(int p, DateTime at) => new("dev", "Test", DeviceKind.Headset, p, null, null, at);

    [Fact]
    public void The_history_is_not_rewritten_on_every_reading_but_is_written_on_flush()
    {
        string file = Path.Combine(_dir, "historique.json");
        var e = new AutonomyEstimator(file);

        e.Apply(new[] { R(80, _t0) });
        Assert.True(File.Exists(file));                         // première écriture immédiate
        string afterFirst = File.ReadAllText(file);

        for (int m = 2; m <= 20; m += 2) e.Apply(new[] { R(80, _t0.AddMinutes(m)) });
        Assert.Equal(afterFirst, File.ReadAllText(file));       // 10 lectures de plus : le fichier n'a pas été réécrit

        e.Flush();                                              // fermeture de l'application
        Assert.NotEqual(afterFirst, File.ReadAllText(file));
        Assert.Contains("\"Version\":2", File.ReadAllText(file));
    }

    [Fact]
    public void Flush_without_changes_does_nothing()
    {
        string file = Path.Combine(_dir, "h.json");
        var e = new AutonomyEstimator(file);
        e.Flush();
        Assert.False(File.Exists(file));
    }
}

// ---------------------------------------------------------------- un appareil Windows qui ne répond pas
public class WinRtWaitTests
{
    [Fact]
    public void A_fast_operation_returns_its_value()
    {
        var op = Task.FromResult(42).AsAsyncOperation();
        Assert.Equal(42, WinRt.Wait(op, seconds: 5));
    }

    [Fact]
    public void An_operation_that_never_answers_times_out_instead_of_blocking_forever()
    {
        var op = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => 1).AsAsyncOperation();
        var sw = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => WinRt.Wait(op, seconds: 1));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 5);
    }

    [Fact]
    public void A_failing_operation_surfaces_its_real_exception()
    {
        var op = Task.FromException<int>(new InvalidOperationException("raté")).AsAsyncOperation();
        Assert.Throws<InvalidOperationException>(() => WinRt.Wait(op, seconds: 5));
    }
}
