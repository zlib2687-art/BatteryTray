using System.Text.Json;

namespace BatteryTray;

/// <summary>
/// Garantit qu'en décharge la durée affichée ne remonte jamais : tant que le niveau ne remonte pas (pas de charge),
/// la durée est au plus celle affichée précédemment moins le temps écoulé. Quand le niveau baisse, la durée baisse.
/// Sans cela, une nouvelle estimation de la vitesse (ou le passage de « annoncée » à « mesurée ») pouvait faire
/// grimper la durée alors que le pourcentage baissait. L'état est conservé entre deux lancements.
/// </summary>
public sealed class AutonomyRatchet
{
    public sealed record Entry(int Percent, double Hours, DateTime At);

    /// <summary>Au-delà de cet écart entre deux lectures, le garde-fou n'est plus fiable : la valeur précédente est oubliée.</summary>
    public const double MaxGapHours = 1;

    readonly object _sync = new();
    readonly string? _path;
    readonly Dictionary<string, Entry> _last;
    DateTime _lastSave = DateTime.MinValue;

    public AutonomyRatchet(string? path = null)
    {
        _path = path;
        _last = Load(path);
    }

    public List<BatteryReading> Apply(IEnumerable<BatteryReading> readings)
    {
        lock (_sync)
        {
            var result = new List<BatteryReading>();
            bool changed = false;
            foreach (var r in readings)
            {
                if (r.Offline || r.Percent is not int p) { result.Add(r); continue; }

                bool discharging = r.Charge != ChargeState.Charging && r.Charge != ChargeState.Full;
                if (!discharging) { changed |= _last.Remove(r.Id); result.Add(r); continue; }          // charge : on repart de zéro
                if (r.AutonomyHours is not double hours || r.AutonomyLowerBound) { result.Add(r); continue; }

                var shown = hours;
                if (_last.TryGetValue(r.Id, out var prev))
                {
                    double gap = (r.At - prev.At).TotalHours;
                    // niveau remonté (charge) ou longue absence (PC éteint, application fermée) : on repart de la nouvelle
                    // estimation. Sans cela, 48 h d'absence retiraient 48 h à la durée et affichaient « 0 h » jusqu'à la charge.
                    // Une remontée de AutonomyEstimator.NoiseTolerance points ou moins est du bruit de mesure (Razer…) : le garde-fou reste actif.
                    if (p > prev.Percent + AutonomyEstimator.NoiseTolerance || gap > MaxGapHours || gap < 0) { }
                    else
                    {
                        double elapsed = Math.Max(0, (r.At - prev.At).TotalHours);
                        shown = Math.Min(hours, Math.Max(0, prev.Hours - elapsed));
                        p = Math.Min(p, prev.Percent); // pas de dérive : on mémorise le niveau le plus bas
                    }
                }
                _last[r.Id] = new Entry(p, shown, r.At);
                changed = true;
                result.Add(shown == hours ? r : r with { AutonomyHours = shown });
            }
            if (changed && DateTime.Now - _lastSave >= TimeSpan.FromMinutes(1)) Save();
            return result;
        }
    }

    public void Flush() { lock (_sync) Save(); }

    void Save()
    {
        _lastSave = DateTime.Now;
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafeFile.WriteAllText(_path, JsonSerializer.Serialize(_last));
        }
        catch { /* sans gravité : l'état sera réécrit à la prochaine lecture */ }
    }

    static Dictionary<string, Entry> Load(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
                // un fichier abîmé ou modifié à la main ne doit pas faire échouer les lectures suivantes
                return loaded.Where(kv => kv.Value is { Percent: >= 0 and <= 100 } e && double.IsFinite(e.Hours) && e.Hours >= 0)
                             .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
        catch { }
        return new();
    }
}
