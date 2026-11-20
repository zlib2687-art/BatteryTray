namespace BatteryTray;

/// <summary>Calcul des points de la courbe de niveau du flyout (logique pure, testée).</summary>
public static class ChartMath
{
    /// <summary>
    /// Place chaque mesure dans <paramref name="area"/> : le temps en abscisse (de la première à la dernière
    /// mesure), le niveau en ordonnée (0 % en bas, 100 % en haut). Une seule mesure est centrée.
    /// </summary>
    public static PointF[] Map(IReadOnlyList<AutonomyEstimator.Sample> samples, RectangleF area, int lo = 0, int hi = 100)
    {
        if (samples.Count == 0) return Array.Empty<PointF>();
        if (hi <= lo) { lo = 0; hi = 100; }

        var t0 = samples[0].T;
        double span = (samples[^1].T - t0).TotalSeconds;
        var points = new PointF[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            float x = span <= 0
                ? area.X + area.Width / 2f
                : area.X + (float)((samples[i].T - t0).TotalSeconds / span) * area.Width;
            float y = area.Bottom - (Math.Clamp(samples[i].P, lo, hi) - lo) / (float)(hi - lo) * area.Height;
            points[i] = new PointF(x, y);
        }
        return points;
    }

    /// <summary>
    /// Échelle verticale adaptée aux mesures : bornes arrondies à la dizaine, 20 points d'écart au minimum,
    /// toujours entre 0 et 100 % — une baisse de 8 points reste lisible au lieu de paraître plate.
    /// </summary>
    public static (int Lo, int Hi) Range(IReadOnlyList<AutonomyEstimator.Sample> samples)
    {
        if (samples.Count == 0) return (0, 100);

        int min = samples.Min(s => s.P), max = samples.Max(s => s.P);
        int lo = Math.Max(0, (int)Math.Floor((min - 3) / 10.0) * 10);
        int hi = Math.Min(100, (int)Math.Ceiling((max + 3) / 10.0) * 10);
        while (hi - lo < 20)
        {
            if (hi < 100) hi += 10;
            else if (lo > 0) lo -= 10;
            else break;
        }
        return (lo, hi);
    }
}
