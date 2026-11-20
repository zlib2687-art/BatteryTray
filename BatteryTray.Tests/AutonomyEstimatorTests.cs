using Xunit;

namespace BatteryTray.Tests;

public class AutonomyEstimatorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "batterytray-tests-" + Guid.NewGuid().ToString("N"));
    readonly DateTime _t0 = new(2026, 11, 20, 10, 0, 0);

    string HistoryFile => Path.Combine(_dir, "historique.json");
    AutonomyEstimator NewEstimator() => new(HistoryFile);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static BatteryReading Reading(string id, int percent, DateTime at, string? charge = null) =>
        new(id, "Test", DeviceKind.Headset, percent, charge, null, at);

    /// <summary>Une lecture toutes les `stepMin` minutes ; le niveau réel baisse de `dropPerHour` points par heure (affiché arrondi).</summary>
    BatteryReading Simulate(AutonomyEstimator e, string id, double startPercent, double dropPerHour, int minutes, int stepMin = 2)
    {
        BatteryReading last = null!;
        for (int m = 0; m <= minutes; m += stepMin)
        {
            int p = (int)Math.Round(startPercent - dropPerHour * m / 60.0);
            last = e.Apply(new[] { Reading(id, p, _t0.AddMinutes(m)) })[0];
        }
        return last;
    }

    [Fact]
    public void FewChangesDuringIdleStayCloseToRatedAutonomy()
    {
        // Casque resté au repos : 3 changements espacés donnaient ~71 h pour 68 % (annoncé : 50 h pleine charge).
        var e = new AutonomyEstimator(Path.Combine(_dir, "idle.json"));
        BatteryReading last = null!;
        for (int m = 0; m <= 126; m += 2)
        {
            int p = m < 11 ? 71 : m < 36 ? 70 : m < 124 ? 69 : 68;
            last = e.Apply(new[] { new BatteryReading("hs", "Logitech PRO X 2", DeviceKind.Headset, p, null, null, _t0.AddMinutes(m)) })[0];
        }
        Assert.InRange(last.AutonomyHours!.Value, 30, 52);
    }

    [Fact]
    public void Steady_discharge_gives_estimate_close_to_truth()
    {
        // 6 points par heure depuis 80 % pendant 1 h -> reste ~74 %, donc ~12 h 20 min
        var r = Simulate(NewEstimator(), "dev", 80, 6, 60);
        Assert.NotNull(r.AutonomyHours);
        Assert.False(r.AutonomyLowerBound);
        Assert.InRange(r.AutonomyHours!.Value, 10.5, 14.5);
    }

    [Fact]
    public void The_estimate_never_rises_while_the_displayed_level_stays_the_same()
    {
        // Défaut de l'ancienne méthode : entre deux baisses, la durée remontait toute seule.
        var e = NewEstimator();
        double? previous = null;
        int? previousLevel = null;
        for (int m = 0; m <= 360; m += 2)
        {
            int level = (int)Math.Round(90 - 3.5 * m / 60.0);
            var r = e.Apply(new[] { Reading("dev", level, _t0.AddMinutes(m)) })[0];
            if (r.AutonomyHours is double hours && !r.AutonomyLowerBound)
            {
                if (previous is double before && previousLevel == level)
                    Assert.True(hours <= before + 1e-6, $"minute {m}, niveau {level} : {hours:F2} h > {before:F2} h");
                previous = hours;
                previousLevel = level;
            }
        }
        Assert.NotNull(previous); // une estimation a bien été donnée
    }

    [Fact]
    public void The_estimate_stays_close_to_the_true_remaining_time_once_enough_data_exists()
    {
        // vitesse réelle 3,5 points/h : on contrôle la durée restante réelle à partir de 2 h d'observation
        var e = NewEstimator();
        for (int m = 0; m <= 420; m += 2)
        {
            int level = (int)Math.Round(90 - 3.5 * m / 60.0);
            var r = e.Apply(new[] { Reading("dev", level, _t0.AddMinutes(m)) })[0];
            if (m >= 120 && r.AutonomyHours is double hours)
            {
                double trueRemaining = (90 - 3.5 * m / 60.0) / 3.5;
                Assert.InRange(hours / trueRemaining, 0.75, 1.3);
            }
        }
    }

    [Fact]
    public void No_drop_for_a_while_gives_a_lower_bound_never_a_fake_value()
    {
        // 20 % sans aucune baisse pendant 30 min : la vitesse est < 1 point / 0,5 h => plus de 20 × 0,5 = 10 h
        var r = Simulate(NewEstimator(), "dev", 20, 0, 30);
        Assert.True(r.AutonomyLowerBound);
        Assert.InRange(r.AutonomyHours!.Value, 9, 10.5);
    }

    [Fact]
    public void The_lower_bound_is_capped_at_24_hours_and_stops_growing()
    {
        // 100 % sans aucune baisse : l'ancienne version affichait « plus de 3 j » en grandissant à chaque lecture
        double previous = 0;
        for (int minutes = 30; minutes <= 300; minutes += 30)
        {
            var r = Simulate(new AutonomyEstimator(Path.Combine(_dir, $"h{minutes}.json")), "dev", 100, 0, minutes);
            Assert.True(r.AutonomyLowerBound);
            Assert.True(r.AutonomyHours <= AutonomyEstimator.MaxLowerBoundHours);
            Assert.True(r.AutonomyHours >= previous);
            previous = r.AutonomyHours!.Value;
        }
        Assert.Equal(24, previous);
    }

    [Fact]
    public void A_single_lost_point_is_not_enough_to_announce_a_duration()
    {
        // 80 -> 79 en une heure : un seul changement à un instant inconnu, la vitesse est trop incertaine
        var r = Simulate(NewEstimator(), "dev", 80, 1.2, 60);
        Assert.True(r.AutonomyPending);
        Assert.Null(r.AutonomyHours);
    }

    [Fact]
    public void Several_changes_over_half_an_hour_give_a_first_duration()
    {
        var r = Simulate(NewEstimator(), "dev", 80, 8, 45);
        Assert.NotNull(r.AutonomyHours);
        Assert.False(r.AutonomyLowerBound);
    }

    [Fact]
    public void Too_little_data_stays_pending()
    {
        var r = Simulate(NewEstimator(), "dev", 73, 0, 6);
        Assert.True(r.AutonomyPending);
        Assert.Null(r.AutonomyHours);
    }

    [Fact]
    public void Charging_has_no_autonomy_and_resets_history()
    {
        var e = NewEstimator();
        Simulate(e, "dev", 80, 6, 40);
        var charging = e.Apply(new[] { Reading("dev", 60, _t0.AddMinutes(45), ChargeState.Charging) })[0];
        Assert.Null(charging.AutonomyHours);
        Assert.False(charging.AutonomyPending);

        // après la charge, on repart de zéro : une seule lecture => en attente
        var after = e.Apply(new[] { Reading("dev", 61, _t0.AddMinutes(47)) })[0];
        Assert.True(after.AutonomyPending);
    }

    [Fact]
    public void Level_going_up_restarts_the_measurement()
    {
        var e = NewEstimator();
        Simulate(e, "dev", 80, 6, 40);
        var r = e.Apply(new[] { Reading("dev", 95, _t0.AddMinutes(44)) })[0];
        Assert.True(r.AutonomyPending);
    }

    [Fact]
    public void Long_gaps_are_ignored_so_a_stopped_app_does_not_skew_the_rate()
    {
        var e = NewEstimator();
        e.Apply(new[] { Reading("dev", 80, _t0) });
        // 5 h plus tard (application arrêtée), le niveau a baissé de 15 points : l'instant des changements est inconnu
        var r = e.Apply(new[] { Reading("dev", 65, _t0.AddHours(5)) })[0];
        Assert.True(r.AutonomyPending);
    }

    [Fact]
    public void Learned_rate_survives_a_restart()
    {
        var first = NewEstimator();
        Simulate(first, "dev", 90, 5, 150); // 5 changements ou plus sur plus de 1 h 30 => vitesse apprise
        first.Flush();                       // l'application écrit l'historique à la fermeture

        var second = NewEstimator(); // nouveau démarrage, même fichier
        var r = second.Apply(new[] { Reading("dev", 70, _t0.AddDays(1)) })[0];
        Assert.NotNull(r.AutonomyHours);
        Assert.False(r.AutonomyPending);
        Assert.InRange(r.AutonomyHours!.Value, 10, 20); // 70 % à ~5 points/h = ~14 h
    }

    [Fact]
    public void Rates_learned_by_the_old_biased_method_are_discarded()
    {
        Directory.CreateDirectory(_dir);
        // fichier de l'ancienne version : pas de numéro de version, vitesse apprise absurde (50 points/h)
        File.WriteAllText(HistoryFile, "{\"History\":{},\"Rates\":{\"dev\":50.0},\"ChargeHistory\":{},\"ChargeRates\":{\"dev\":50.0}}");

        var r = NewEstimator().Apply(new[] { Reading("dev", 70, _t0) })[0];
        Assert.True(r.AutonomyPending);            // pas de 70 / 50 = 1,4 h
        Assert.Null(r.AutonomyHours);
    }

    [Fact]
    public void Readings_without_percentage_are_passed_through_untouched()
    {
        var e = NewEstimator();
        var r = e.Apply(new[] { new BatteryReading("g502", "G502", DeviceKind.Mouse, null, null, "4209 mV", _t0) })[0];
        Assert.Null(r.Percent);
        Assert.Null(r.AutonomyHours);
        Assert.False(r.AutonomyPending);
    }

    [Fact]
    public void Devices_are_tracked_independently()
    {
        var e = NewEstimator();
        BatteryReading[]? res = null;
        for (int m = 0; m <= 90; m += 2)
        {
            res = e.Apply(new[]
            {
                Reading("fast", (int)Math.Round(90 - 12 * m / 60.0), _t0.AddMinutes(m)),
                Reading("slow", (int)Math.Round(90 - 4 * m / 60.0), _t0.AddMinutes(m)),
            }).ToArray();
        }
        Assert.NotNull(res![0].AutonomyHours);
        Assert.NotNull(res[1].AutonomyHours);
        Assert.True(res[1].AutonomyHours > res[0].AutonomyHours * 2);
    }
}
