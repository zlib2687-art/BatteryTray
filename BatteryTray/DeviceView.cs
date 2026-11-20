namespace BatteryTray;

/// <summary>
/// Transforme les lectures brutes en ce que l'utilisateur voit : appareils masqués retirés, noms
/// personnalisés, ordre choisi, autonomie annoncée en dernier recours, et appareils déconnectés gardés en
/// gris avec leur dernière valeur. Logique pure (testable).
/// </summary>
public static class DeviceView
{
    /// <summary>Un appareil hors ligne reste affiché (en gris) pendant ce nombre de jours, puis disparaît.</summary>
    public const int OfflineDays = 7;

    /// <param name="changed">Vrai si les réglages ont été modifiés (nouvel appareil, nouvelle valeur) et doivent être sauvegardés.</param>
    public static List<BatteryReading> Prepare(IEnumerable<BatteryReading> readings, AppSettings s, out bool changed, DateTime? now = null)
    {
        changed = false;
        var clock = now ?? DateTime.Now;
        var shown = new List<BatteryReading>();
        var presentIds = new HashSet<string>();

        foreach (var r in readings)
        {
            presentIds.Add(r.Id);

            // mémorise l'appareil (nom d'origine) pour qu'il reste listé dans les paramètres
            if (!s.Known.TryGetValue(r.Id, out var known))
            {
                known = new KnownDevice { Name = r.Name, Kind = r.Kind };
                s.Known[r.Id] = known;
                if (!s.Order.Contains(r.Id)) s.Order.Add(r.Id);
                changed = true;
            }
            else if (known.Name != r.Name || known.Kind != r.Kind)
            {
                known.Name = r.Name;
                known.Kind = r.Kind;
                changed = true;
            }

            // dernière valeur connue (pour l'affichage hors ligne) ; on ne réécrit le fichier que si elle change
            // ou si elle date de plus de 10 min
            if (r.Percent is int pct)
            {
                bool stale = known.LastSeen is not DateTime seen || (r.At - seen).TotalMinutes > 10;
                if (known.LastPercent != pct || known.LastEstimated != r.PercentEstimated || stale) changed = true;
                known.LastPercent = pct;
                known.LastEstimated = r.PercentEstimated;
                known.LastSeen = r.At;
            }

            // un appareil qui ne donne aucun pourcentage est masqué d'office (modifiable dans les paramètres),
            // qu'il soit nouveau ou déjà connu mais sans préférence enregistrée
            if (r.Percent == null && !s.Devices.ContainsKey(r.Id))
            {
                s.Devices[r.Id] = new DevicePref { Hidden = true, AutoHidden = true };
                changed = true;
            }

            var pref = s.PrefFor(r.Id);
            if (pref is { AutoHidden: true, Hidden: true } && r.Percent != null)
            {
                // masqué d'office faute de niveau ; un niveau existe maintenant (lu ou estimé) : on l'affiche
                pref.Hidden = false;
                pref.AutoHidden = false;
                changed = true;
            }
            if (pref.Hidden) continue;

            var view = r with { Name = string.IsNullOrWhiteSpace(pref.Name) ? r.Name : pref.Name.Trim() };

            // pas encore de mesure : autonomie annoncée saisie par l'utilisateur, sinon celle connue pour ce modèle
            double? ratedHours = pref.RatedHours ?? DeviceDefaults.RatedHoursFor(r.Name);
            if (view.AutonomyPending && view.Percent is int p && ratedHours is double rated && rated > 0)
                view = view with { AutonomyPending = false, AutonomyHours = p / 100.0 * rated, AutonomyFromRating = true };

            shown.Add(view);
        }

        // appareils vus récemment mais absents de cette lecture : gardés en gris avec leur dernière valeur
        foreach (var (id, known) in s.Known)
        {
            if (presentIds.Contains(id)) continue;
            if (known.LastPercent is not int last || known.LastSeen is not DateTime seen) continue;
            if ((clock - seen).TotalDays > OfflineDays) continue;
            var pref = s.PrefFor(id);
            if (pref.Hidden) continue;

            shown.Add(new BatteryReading(id, string.IsNullOrWhiteSpace(pref.Name) ? known.Name : pref.Name.Trim(),
                known.Kind, last, null, null, seen)
            {
                Offline = true,
                PercentEstimated = known.LastEstimated,
            });
        }

        // ordre choisi ; les appareils non classés viennent ensuite, triés par type puis par nom
        int Rank(BatteryReading r)
        {
            int i = s.Order.IndexOf(r.Id);
            return i < 0 ? int.MaxValue : i;
        }
        return shown.OrderBy(Rank).ThenBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
