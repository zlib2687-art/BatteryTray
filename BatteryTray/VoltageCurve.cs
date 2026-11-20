namespace BatteryTray;

/// <summary>
/// Convertit la tension d'une batterie lithium (une cellule) en pourcentage APPROXIMATIF, pour les appareils
/// qui ne donnent que leur tension (ex. Logitech G502 LIGHTSPEED, HID++ feature 0x1001).
///
/// C'est une estimation : la courbe est celle d'une cellule Li-ion typique au repos et varie selon la charge,
/// la température et le vieillissement. Le résultat est donc toujours affiché avec « ~ ».
/// À comparer avec le logiciel du fabricant (G HUB) avant de s'y fier.
/// </summary>
public static class VoltageCurve
{
    // (millivolts, %) du plus haut au plus bas
    static readonly (int Mv, int Pct)[] Points =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40),
        (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    public static int Estimate(int millivolts)
    {
        if (millivolts >= Points[0].Mv) return 100;
        if (millivolts <= Points[^1].Mv) return 0;

        for (int i = 0; i < Points.Length - 1; i++)
        {
            var (hiMv, hiPct) = Points[i];
            var (loMv, loPct) = Points[i + 1];
            if (millivolts <= hiMv && millivolts >= loMv)
            {
                double t = (millivolts - loMv) / (double)(hiMv - loMv);
                return (int)Math.Round(loPct + t * (hiPct - loPct));
            }
        }
        return 0;
    }

    /// <summary>Médiane des dernières tensions : la tension d'une batterie sautille de quelques mV d'une lecture à l'autre.</summary>
    public sealed class Smoother
    {
        readonly int _size;
        readonly Dictionary<string, Queue<int>> _recent = new();

        public Smoother(int size = 5) => _size = size;

        public int Add(string deviceId, int millivolts)
        {
            lock (_recent)
            {
                if (!_recent.TryGetValue(deviceId, out var q)) _recent[deviceId] = q = new Queue<int>();
                q.Enqueue(millivolts);
                while (q.Count > _size) q.Dequeue();

                var sorted = q.OrderBy(v => v).ToArray();
                return sorted[sorted.Length / 2];
            }
        }
    }
}
