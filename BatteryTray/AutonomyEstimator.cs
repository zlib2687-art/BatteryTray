using System.Text.Json;

namespace BatteryTray;

/// <summary>
/// Estime, pour chaque appareil, l'autonomie restante (en décharge) ou le temps avant charge complète (en charge),
/// à partir de SA vitesse observée (historique local dans %AppData%, jamais envoyé nulle part).
///
/// La vitesse vient des instants où le niveau change (voir <see cref="LevelTrend"/>), pas du quotient
/// « points ÷ heures » qui faisait grimper la durée entre deux baisses. Règles :
///  - décharge : au moins 3 franchissements répartis sur au moins 30 min avant d'annoncer « ≈ … » ;
///  - sans mesure suffisante : vitesse apprise lors des sessions précédentes, sinon borne « plus de… » (si le niveau
///    n'a pas bougé pendant T, la vitesse est inférieure à 1 point par T), plafonnée à 24 h, sinon « calcul en cours… » ;
///  - la durée restante est un compte à rebours : elle décroît entre deux baisses au lieu de remonter ;
///  - charge : même principe avec la montée du niveau.
/// Résultat à la minute près, mais toujours indicatif.
/// </summary>
public sealed class AutonomyEstimator
{
    public sealed record Sample(DateTime T, int P);

    /// <summary>Version du calcul. Les vitesses apprises avec l'ancienne méthode (biaisée) sont abandonnées.</summary>
    public const int CurrentVersion = 2;

    public sealed class Store
    {
        public int Version { get; set; }
        public Dictionary<string, List<Sample>> History { get; set; } = new();
        /// <summary>Vitesse de décharge apprise, en % par heure.</summary>
        public Dictionary<string, double> Rates { get; set; } = new();
        public Dictionary<string, List<Sample>> ChargeHistory { get; set; } = new();
        /// <summary>Vitesse de charge apprise, en % par heure.</summary>
        public Dictionary<string, double> ChargeRates { get; set; } = new();

        /// <summary>Rend utilisable un fichier abîmé ou modifié à la main : listes manquantes, niveaux hors 0–100, vitesses absurdes.</summary>
        public void Normalize()
        {
            History ??= new(); ChargeHistory ??= new(); Rates ??= new(); ChargeRates ??= new();
            foreach (var map in new[] { History, ChargeHistory })
            {
                foreach (var key in map.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) map.Remove(key);
                foreach (var list in map.Values) list.RemoveAll(s => s == null || s.P < 0 || s.P > 100);
            }
            foreach (var rates in new[] { Rates, ChargeRates })
                foreach (var key in rates.Where(kv => !double.IsFinite(kv.Value) || kv.Value <= 0 || kv.Value > 1000).Select(kv => kv.Key).ToList())
                    rates.Remove(key);
        }
    }

    // décharge
    const int DischargeMinPoints = 3;
    const double DischargeMinSpanHours = 0.5;      // 30 min entre le premier et le dernier changement
    const double LearnMinSpanHours = 1.5;          // une mesure est « solide » à partir de 5 changements sur 1 h 30
    const int LearnMinPoints = 5;
    const double PriorWeight = 6;                  // poids de l'autonomie annoncée, en « changements équivalents »
    const double LowerBoundHours = 0.25;          // 15 min sans baisse : première borne « plus de… »

    // charge (une charge est plus courte : seuils plus bas)
    const int ChargeMinPoints = 3;
    const double ChargeMinSpanHours = 0.25;

    /// <summary>La borne « plus de… » est plafonnée : sans cela elle grandissait à chaque lecture (« plus de 3 j »).</summary>
    public const double MaxLowerBoundHours = 24;

    /// <summary>Remontée (en points) tolérée comme bruit de mesure en décharge ; au-delà, c'est une recharge.</summary>
    public const int NoiseTolerance = 2;

    readonly object _sync = new(); // Apply tourne sur un thread de travail, SamplesFor sur le thread d'interface
    readonly string _path;
    readonly Store _store;

    public AutonomyEstimator(string path)
    {
        _path = path;
        _store = Load(path);
    }

    /// <summary>Mesures de la période de décharge en cours (depuis la dernière charge), pour la courbe du flyout.</summary>
    public IReadOnlyList<Sample> SamplesFor(string id)
    {
        lock (_sync)
            return _store.History.TryGetValue(id, out var list) ? list.ToList() : new List<Sample>();
    }

    public List<BatteryReading> Apply(IEnumerable<BatteryReading> readings)
    {
        lock (_sync)
        {
            var result = new List<BatteryReading>();
            foreach (var r in readings)
            {
                if (r.Percent is not int p) { result.Add(r); continue; }
                // un niveau hors 0–100 vient d'un appareil ou d'un pilote défectueux : on ne s'en sert pas pour calculer
                // (et surtout pas pour compter des « franchissements » entre -2 milliards et +2 milliards)
                if (p is < 0 or > 100 || r.Id == null) { result.Add(r); continue; }

                if (!_store.History.TryGetValue(r.Id, out var list)) _store.History[r.Id] = list = new List<Sample>();
                bool charging = r.Charge == ChargeState.Charging;
                bool plugged = charging || r.Charge == ChargeState.Full;

                // Bruit de mesure : certains appareils (souris et casques Razer, jauges en tension) oscillent de 1 à 2 points
                // autour du vrai niveau. Une remontée de NoiseTolerance points ou moins n'est PAS une recharge : on garde
                // l'historique et le niveau le plus bas. Sans cela, chaque oscillation effaçait la mesure et l'autonomie remontait.
                if (!plugged && list.Count > 0 && p > list[^1].P && p - list[^1].P <= NoiseTolerance)
                    p = list[^1].P;

                if (plugged || (list.Count > 0 && p > list[^1].P))
                    list.Clear(); // en charge, ou niveau vraiment remonté : nouvelle période de décharge

                if (list.Count == 0 || (r.At - list[^1].T).TotalSeconds >= 45)
                    list.Add(new Sample(r.At, p));
                list.RemoveAll(s => (r.At - s.T).TotalHours > 48);

                if (charging) { result.Add(EstimateCharge(r, p)); continue; }

                _store.ChargeHistory.Remove(r.Id); // plus en charge : la prochaine charge repart de zéro
                if (plugged) { result.Add(r); continue; }

                result.Add(EstimateDischarge(r, list, p));
            }
            SaveThrottled();
            return result;
        }
    }

    /// <summary>
    /// L'historique n'est pas réécrit à chaque lecture (toutes les 20 s, l'ensemble du fichier représentait plusieurs Go
    /// par jour sur le disque) : au plus toutes les 5 minutes, et en entier à la fermeture (voir <see cref="Flush"/>).
    /// </summary>
    static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    DateTime _lastSave = DateTime.MinValue;
    bool _dirty;

    void SaveThrottled()
    {
        _dirty = true;
        if (DateTime.Now - _lastSave >= SaveInterval) Flush();
    }

    /// <summary>Écrit l'historique tout de suite (à la fermeture de l'application).</summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (!_dirty) return;
            Save();
            _dirty = false;
            _lastSave = DateTime.Now;
        }
    }

    // ------------------------------------------------------------------ décharge
    BatteryReading EstimateDischarge(BatteryReading r, List<Sample> samples, int percent)
    {
        var crossings = LevelTrend.Crossings(samples, falling: true);
        double? measured = LevelTrend.Rate(crossings, DischargeMinPoints, DischargeMinSpanHours);
        bool hasLearned = _store.Rates.TryGetValue(r.Id, out double learned) && learned > 0;

        // mesure solide, mise à jour seulement quand un nouveau changement vient d'arriver (sinon on compterait la même chose plusieurs fois)
        bool newCrossing = samples.Count >= 2 && samples[^1].P < samples[^2].P;
        if (newCrossing && LevelTrend.Rate(crossings, LearnMinPoints, LearnMinSpanHours) is double solid)
            _store.Rates[r.Id] = hasLearned ? 0.7 * learned + 0.3 * solid : solid;

        double? rate = measured ?? (hasLearned ? learned : null);

        // Peu de changements = mesure fragile (un casque resté au repos une heure donnait « 71 h » au lieu de ~35 h).
        // Tant qu'on connaît l'autonomie annoncée du modèle, elle pèse comme PriorWeight changements imaginaires.
        if (measured is double m && DeviceDefaults.RatedHoursFor(r.Name) is double ratedFull && ratedFull > 0)
        {
            double n = crossings.Count;
            rate = (n * m + PriorWeight * (100.0 / ratedFull)) / (n + PriorWeight);
        }

        if (rate is double perHour)
        {
            // compte à rebours depuis le dernier changement, sinon niveau affiché tel quel
            double level = crossings.Count > 0
                ? LevelTrend.TrueLevelNow(crossings[^1], perHour, r.At, falling: true)
                : percent;
            return r with { AutonomyHours = level / perHour };
        }

        if (crossings.Count == 0 && LevelTrend.ObservedHours(samples) is double hours && hours >= LowerBoundHours)
        {
            // Aucun changement sur `hours` : la vitesse réelle est forcément < 1 point / hours,
            // donc l'autonomie dépasse percent × hours. On l'affiche comme « plus de … », plafonné.
            return r with { AutonomyHours = Math.Min(percent * hours, MaxLowerBoundHours), AutonomyLowerBound = true };
        }
        return r with { AutonomyPending = true };
    }

    // ------------------------------------------------------------------ charge
    BatteryReading EstimateCharge(BatteryReading r, int percent)
    {
        if (!_store.ChargeHistory.TryGetValue(r.Id, out var samples)) _store.ChargeHistory[r.Id] = samples = new List<Sample>();
        if (samples.Count > 0 && percent < samples[^1].P) samples.Clear(); // le niveau a baissé : ce n'est plus la même charge
        if (samples.Count == 0 || (r.At - samples[^1].T).TotalSeconds >= 45) samples.Add(new Sample(r.At, percent));
        samples.RemoveAll(s => (r.At - s.T).TotalHours > 12);

        if (percent >= 100) return r; // plein : rien à attendre

        var crossings = LevelTrend.Crossings(samples, falling: false);
        double? measured = LevelTrend.Rate(crossings, ChargeMinPoints, ChargeMinSpanHours);
        bool hasLearned = _store.ChargeRates.TryGetValue(r.Id, out double learned) && learned > 0;

        bool newCrossing = samples.Count >= 2 && samples[^1].P > samples[^2].P;
        if (newCrossing && LevelTrend.Rate(crossings, LearnMinPoints, ChargeMinSpanHours * 2) is double solid)
            _store.ChargeRates[r.Id] = hasLearned ? 0.7 * learned + 0.3 * solid : solid;

        double? rate = measured ?? (hasLearned ? learned : null);
        if (rate is not double perHour) return r with { ChargeEtaPending = true };

        double level = crossings.Count > 0
            ? LevelTrend.TrueLevelNow(crossings[^1], perHour, r.At, falling: false)
            : percent;
        return r with { ChargeEtaHours = Math.Max(0, (100 - level) / perHour) };
    }

    static Store Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path));
                if (store is { Version: CurrentVersion }) { store.Normalize(); return store; }
            }
        }
        catch { /* fichier absent ou corrompu : on repart de zéro */ }
        return new Store { Version = CurrentVersion }; // aussi pour un fichier de l'ancienne méthode : mesures repartent de zéro
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafeFile.WriteAllText(_path, JsonSerializer.Serialize(_store));
        }
        catch { /* l'historique est facultatif */ }
    }
}
