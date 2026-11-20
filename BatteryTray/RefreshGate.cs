namespace BatteryTray;

/// <summary>
/// Une seule lecture à la fois, MAIS une lecture bloquée (appareil qui ne répond plus) ne doit pas figer l'application
/// pour toujours : passé <c>stuckAfter</c>, une nouvelle lecture peut démarrer. Un « ticket » évite qu'une lecture
/// bloquée, qui se termine enfin, libère la place d'une lecture plus récente. Logique pure, testée.
/// </summary>
public sealed class RefreshGate
{
    readonly TimeSpan _stuckAfter;
    readonly object _sync = new();
    long _ticket;
    long _current;          // ticket de la lecture en cours, 0 = aucune
    DateTime _startedAt;

    public RefreshGate(TimeSpan stuckAfter) => _stuckAfter = stuckAfter;

    /// <summary>Retourne un ticket (> 0) si une lecture peut démarrer, 0 si une lecture saine est déjà en cours.</summary>
    public long TryEnter(DateTime now)
    {
        lock (_sync)
        {
            if (_current != 0 && now - _startedAt < _stuckAfter) return 0;
            _current = ++_ticket;
            _startedAt = now;
            return _current;
        }
    }

    /// <summary>Fin de la lecture : sans effet si une lecture plus récente a pris la place entre-temps.</summary>
    public void Exit(long ticket)
    {
        lock (_sync)
        {
            if (_current == ticket) _current = 0;
        }
    }

    public bool IsRunning { get { lock (_sync) return _current != 0; } }
}
