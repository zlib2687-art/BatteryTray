using System.Text.Json;

namespace BatteryTray;

/// <summary>
/// Historique du niveau de batterie sur 30 jours, pour la visionneuse. Enregistré localement (jamais envoyé nulle part).
/// Règles : seuls les niveaux réellement lus (appareil connecté, éveillé, pourcentage lu et non estimé d'après la tension) sont
/// gardés ; au plus une mesure toutes les 30 min par appareil, sauf changement d'état de charge (60 s minimum entre deux mesures) ;
/// 30 jours de conservation ; taille bornée. Logique pure, testée.
/// </summary>
public sealed class BatteryHistory
{
    public sealed record Point(DateTime T, int P, bool Charging);

    public sealed class Store
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, List<Point>> Devices { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new();

        /// <summary>Rend utilisable un fichier abîmé : listes manquantes, niveaux hors 0–100, ordre chronologique.</summary>
        public void Normalize()
        {
            Devices ??= new(); Names ??= new();
            foreach (var key in Devices.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Devices.Remove(key);
            foreach (var list in Devices.Values)
            {
                list.RemoveAll(p => p == null || p.P is < 0 or > 100);
                list.Sort((a, b) => a.T.CompareTo(b.T));
            }
            foreach (var key in Names.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Names.Remove(key);
        }
    }

    public const int RetentionDays = 30;
    public const int MinIntervalMinutes = 30;
    public const int MinChargeChangeSeconds = 60;
    public const int MaxPointsPerDevice = 3000;   // 30 j × 48 mesures = 1440 : large marge
    public const int MaxDevices = 40;

    readonly object _sync = new();
    readonly string? _path;
    readonly Store _store;
    DateTime _lastSave = DateTime.MinValue;
    bool _dirty;

    public BatteryHistory(string? path = null)
    {
        _path = path;
        _store = Load(path);
    }

    /// <summary>Enregistre, parmi ces lectures, celles qui respectent les règles. Renvoie le nombre de mesures ajoutées.</summary>
    public int Record(IEnumerable<BatteryReading> readings, DateTime now)
    {
        int added = 0;
        lock (_sync)
        {
            foreach (var r in readings)
            {
                if (r == null || r.Offline || r.PercentEstimated || string.IsNullOrEmpty(r.Id)) continue;
                if (r.Percent is not int p || p is < 0 or > 100) continue;

                bool charging = r.IsPlugged;
                if (!_store.Devices.TryGetValue(r.Id, out var list))
                {
                    if (_store.Devices.Count >= MaxDevices) continue;
                    _store.Devices[r.Id] = list = new List<Point>();
                }
                _store.Names[r.Id] = r.Name ?? r.Id;

                if (list.Count > 0)
                {
                    var last = list[^1];
                    double minutes = (now - last.T).TotalMinutes;
                    bool chargeChanged = last.Charging != charging;
                    bool due = Math.Abs(minutes) >= MinIntervalMinutes || (chargeChanged && Math.Abs(minutes) * 60 >= MinChargeChangeSeconds);
                    if (!due) continue;
                }
                list.Add(new Point(now, p, charging));
                added++;
                if (list.Count > MaxPointsPerDevice) list.RemoveRange(0, list.Count - MaxPointsPerDevice);
            }

            var limit = now.AddDays(-RetentionDays);
            foreach (var l in _store.Devices.Values) l.RemoveAll(pt => pt.T < limit);
            if (added > 0) { _dirty = true; if (DateTime.Now - _lastSave >= TimeSpan.FromMinutes(5)) Save(); }
        }
        return added;
    }

    /// <summary>Appareils ayant des mesures, avec leur dernier nom connu (triés par nom).</summary>
    public IReadOnlyList<(string Id, string Name)> Devices()
    {
        lock (_sync)
            return _store.Devices.Where(kv => kv.Value.Count > 0)
                .Select(kv => (kv.Key, _store.Names.TryGetValue(kv.Key, out var n) ? n : kv.Key))
                .OrderBy(d => d.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Mesures des dernières <paramref name="range"/> pour un appareil.</summary>
    public IReadOnlyList<Point> Points(string id, TimeSpan range, DateTime now)
    {
        lock (_sync)
        {
            if (!_store.Devices.TryGetValue(id, out var list)) return Array.Empty<Point>();
            var from = now - range;
            return list.Where(p => p.T >= from && p.T <= now.AddMinutes(1)).ToList();
        }
    }

    public sealed record Summary(int Count, Point? Latest, int Lowest, int Highest, int Change);

    /// <summary>Dernière mesure, minimum, maximum et variation (dernière − première) d'une série.</summary>
    public static Summary Summarize(IReadOnlyList<Point> points) =>
        points.Count == 0
            ? new Summary(0, null, 0, 0, 0)
            : new Summary(points.Count, points[^1], points.Min(p => p.P), points.Max(p => p.P), points[^1].P - points[0].P);

    /// <summary>
    /// Découpe une série en tronçons continus : un trou de plus de <paramref name="maxGap"/> (appareil éteint, PC arrêté) interrompt la ligne,
    /// au lieu d'être relié par une droite qui inventerait des valeurs. Logique pure, testée.
    /// </summary>
    public static List<List<Point>> Segments(IReadOnlyList<Point> points, TimeSpan maxGap)
    {
        var result = new List<List<Point>>();
        List<Point>? current = null;
        foreach (var p in points)
        {
            if (current == null || p.T - current[^1].T > maxGap) result.Add(current = new List<Point>());
            current.Add(p);
        }
        return result;
    }

    public void Clear(string id)
    {
        lock (_sync)
        {
            _store.Devices.Remove(id);
            _store.Names.Remove(id);
            _dirty = true;
            Save();
        }
    }

    public void Flush() { lock (_sync) { if (_dirty) Save(); } }

    void Save()
    {
        _lastSave = DateTime.Now;
        _dirty = false;
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafeFile.WriteAllText(_path, JsonSerializer.Serialize(_store));
        }
        catch { /* l'historique est facultatif */ }
    }

    static Store Load(string? path)
    {
        try
        {
            if (path != null && File.Exists(path) && JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) is { } store)
            {
                store.Normalize();
                return store;
            }
        }
        catch { /* absent ou corrompu : on repart d'un historique vide */ }
        return new Store();
    }
}
