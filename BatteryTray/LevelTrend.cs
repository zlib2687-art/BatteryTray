namespace BatteryTray;

/// <summary>
/// Calcul de la vitesse de décharge (ou de charge) à partir des MOMENTS où le niveau affiché change.
///
/// Pourquoi pas « points perdus ÷ heures écoulées » (ancienne méthode) : un niveau entier ne change que par
/// paliers. Entre deux baisses, les heures s'accumulent mais pas les points : le quotient diminuait donc en continu
/// et la durée restante AUGMENTAIT toute seule, avant de retomber à la baisse suivante.
///
/// Ici on ne retient que les instants de changement (« franchissements »), qui sont des mesures ponctuelles :
/// la vitesse est la pente de la droite niveau = f(temps) passant au mieux par ces points. Entre deux
/// franchissements, la durée restante décroît régulièrement (compte à rebours) au lieu de remonter.
/// Logique pure, testée.
/// </summary>
public static class LevelTrend
{
    /// <summary>Au-delà de cet écart entre deux lectures, l'instant exact d'un changement est inconnu : on l'ignore.</summary>
    public const double MaxGapMinutes = 12;

    /// <summary>Un franchissement : à l'instant T, le niveau affiché est passé à <paramref name="Level"/>.</summary>
    public readonly record struct Crossing(DateTime T, int Level);

    /// <summary>
    /// Franchissements d'une série de lectures. Le changement a eu lieu entre deux lectures : on le place au milieu.
    /// Une baisse de plusieurs points d'un coup donne un franchissement par point, au même instant.
    /// </summary>
    public static List<Crossing> Crossings(IReadOnlyList<AutonomyEstimator.Sample> samples, bool falling)
    {
        var result = new List<Crossing>();
        for (int i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];
            if ((b.T - a.T).TotalMinutes is <= 0 or > MaxGapMinutes) continue;

            var t = a.T + (b.T - a.T) / 2;
            if (falling && b.P < a.P)
                for (int level = a.P - 1; level >= b.P; level--) result.Add(new Crossing(t, level));
            else if (!falling && b.P > a.P)
                for (int level = a.P + 1; level <= b.P; level++) result.Add(new Crossing(t, level));
        }
        return result;
    }

    /// <summary>
    /// Vitesse en points par heure (toujours positive), ou null si les données ne suffisent pas :
    /// au moins <paramref name="minPoints"/> franchissements répartis sur au moins <paramref name="minSpanHours"/>.
    /// </summary>
    public static double? Rate(IReadOnlyList<Crossing> crossings, int minPoints, double minSpanHours)
    {
        if (crossings.Count < minPoints) return null;
        double span = (crossings[^1].T - crossings[0].T).TotalHours;
        if (span < minSpanHours || span <= 0) return null;

        // régression linéaire niveau = a + pente × temps
        var t0 = crossings[0].T;
        double n = crossings.Count;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var c in crossings)
        {
            double x = (c.T - t0).TotalHours, y = c.Level;
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-12) return null;

        double slope = (n * sxy - sx * sy) / denominator;
        double rate = Math.Abs(slope);
        // la pente doit aller dans le bon sens : sinon les points sont du bruit
        bool rightDirection = crossings[^1].Level != crossings[0].Level
            ? (slope < 0) == (crossings[^1].Level < crossings[0].Level)
            : false;
        return rightDirection && rate > 0 ? rate : null;
    }

    /// <summary>
    /// Niveau « réel » estimé maintenant, avec décimales. Au dernier franchissement vers L, le niveau réel venait de
    /// passer sous L + ½ (décharge) ou au-dessus de L − ½ (charge) ; depuis, il a évolué à la vitesse mesurée.
    /// Il ne peut pas sortir de la plage de L : sinon le niveau affiché aurait déjà changé.
    /// </summary>
    public static double TrueLevelNow(Crossing last, double ratePerHour, DateTime now, bool falling)
    {
        double hours = Math.Max(0, (now - last.T).TotalHours);
        return falling
            ? Math.Clamp(last.Level + 0.5 - ratePerHour * hours, last.Level - 0.5, last.Level + 0.5)
            : Math.Clamp(last.Level - 0.5 + ratePerHour * hours, last.Level - 0.5, last.Level + 0.5);
    }

    /// <summary>Durée cumulée (h) des intervalles rapprochés entre lectures : sert aux bornes « plus de… ».</summary>
    public static double ObservedHours(IReadOnlyList<AutonomyEstimator.Sample> samples)
    {
        double hours = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            double dt = (samples[i].T - samples[i - 1].T).TotalMinutes;
            if (dt is > 0 and <= MaxGapMinutes) hours += dt / 60.0;
        }
        return hours;
    }
}
