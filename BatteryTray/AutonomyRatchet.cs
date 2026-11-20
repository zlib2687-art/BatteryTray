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
                    if (p > prev.Percent) { /* niveau remonté : nouvelle période */ }
                    else
                    {
                        double elapsed = Math.Max(0, (r.At - prev.At).TotalHours);
                        shown = Math.Min(hours, Math.Max(0, prev.Hours - elapsed));
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
            File.WriteAllText(_path, JsonSerializer.Serialize(_last));
        }
        catch { /* sans gravité : l'état sera réécrit à la prochaine lecture */ }
    }

    static Dictionary<string, Entry> Load(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }
}
