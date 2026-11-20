using Xunit;

namespace BatteryTray.Tests;

public class LevelTrendTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0);
    static AutonomyEstimator.Sample S(int minute, int level) => new(T0.AddMinutes(minute), level);

    [Fact]
    public void A_drop_between_two_readings_is_placed_at_their_midpoint()
    {
        var c = LevelTrend.Crossings(new[] { S(0, 80), S(4, 80), S(8, 79) }, falling: true);
        var only = Assert.Single(c);
        Assert.Equal(79, only.Level);
        Assert.Equal(T0.AddMinutes(6), only.T); // entre la lecture de la minute 4 et celle de la minute 8
    }

    [Fact]
    public void A_drop_of_several_points_gives_one_crossing_per_point_at_the_same_instant()
    {
        var c = LevelTrend.Crossings(new[] { S(0, 80), S(2, 77) }, falling: true);
        Assert.Equal(new[] { 79, 78, 77 }, c.Select(x => x.Level).ToArray());
        Assert.All(c, x => Assert.Equal(T0.AddMinutes(1), x.T));
    }

    [Fact]
    public void Changes_across_a_long_gap_have_no_known_instant_and_are_ignored()
    {
        var c = LevelTrend.Crossings(new[] { S(0, 80), S(300, 70) }, falling: true);
        Assert.Empty(c);
    }

    [Fact]
    public void Only_changes_in_the_requested_direction_count()
    {
        var samples = new[] { S(0, 50), S(2, 49), S(4, 50), S(6, 51) };
        Assert.Single(LevelTrend.Crossings(samples, falling: true));
        Assert.Equal(2, LevelTrend.Crossings(samples, falling: false).Count);
    }

    [Fact]
    public void Rate_of_evenly_spaced_changes_is_exact()
    {
        // un point toutes les 10 min = 6 points / heure
        var samples = Enumerable.Range(0, 13).Select(i => S(i * 10, 90 - i)).ToList();
        var rate = LevelTrend.Rate(LevelTrend.Crossings(samples, falling: true), minPoints: 3, minSpanHours: 0.5);
        Assert.NotNull(rate);
        Assert.Equal(6.0, rate!.Value, 1);
    }

    [Fact]
    public void Rate_needs_enough_points_and_enough_time()
    {
        var two = LevelTrend.Crossings(new[] { S(0, 80), S(10, 79), S(40, 78) }, falling: true);
        Assert.Null(LevelTrend.Rate(two, minPoints: 3, minSpanHours: 0.5));      // 2 changements seulement

        var tooShort = LevelTrend.Crossings(new[] { S(0, 80), S(2, 79), S(4, 78), S(6, 77) }, falling: true);
        Assert.Null(LevelTrend.Rate(tooShort, minPoints: 3, minSpanHours: 0.5)); // 3 changements mais sur 6 min
    }

    [Fact]
    public void Time_passing_without_a_new_change_does_not_change_the_measured_rate()
    {
        // le défaut de l'ancienne méthode : plus d'heures sans baisse => vitesse plus faible
        var samples = Enumerable.Range(0, 7).Select(i => S(i * 10, 90 - i)).ToList(); // 6 points/h pendant 1 h
        var withIdleTime = samples.Concat(new[] { S(62, 84), S(72, 84), S(82, 84), S(92, 84), S(102, 84) }).ToList();

        double? now = LevelTrend.Rate(LevelTrend.Crossings(samples, falling: true), 3, 0.5);
        double? later = LevelTrend.Rate(LevelTrend.Crossings(withIdleTime, falling: true), 3, 0.5);
        Assert.Equal(now!.Value, later!.Value, 6);
    }

    [Fact]
    public void True_level_counts_down_from_the_last_change_and_stays_inside_the_displayed_bin()
    {
        var last = new LevelTrend.Crossing(T0, 72);

        Assert.Equal(72.5, LevelTrend.TrueLevelNow(last, 3.0, T0, falling: true), 6);              // à l'instant du changement
        Assert.Equal(72.0, LevelTrend.TrueLevelNow(last, 3.0, T0.AddMinutes(10), falling: true), 6); // 0,5 point plus tard
        Assert.Equal(71.5, LevelTrend.TrueLevelNow(last, 3.0, T0.AddHours(5), falling: true), 6);  // jamais sous le bas de la plage

        Assert.Equal(71.5, LevelTrend.TrueLevelNow(last, 3.0, T0, falling: false), 6);             // en charge : sens inverse
        Assert.Equal(72.5, LevelTrend.TrueLevelNow(last, 3.0, T0.AddHours(5), falling: false), 6);
    }

    [Fact]
    public void Observed_hours_ignore_long_gaps()
    {
        var h = LevelTrend.ObservedHours(new[] { S(0, 80), S(10, 80), S(20, 80), S(500, 80), S(510, 80) });
        Assert.Equal(30 / 60.0, h, 6);
    }
}
