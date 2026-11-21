namespace BatteryTray;

/// <summary>
/// Distingue clic simple et double-clic sur une icône. Windows envoie, pour un double-clic : relâchement du premier clic
/// (le flyout s'ouvre), double-clic, puis relâchement du second clic. Sans filtre, ce dernier relâchement referme ou
/// rouvre le flyout par-dessus la fenêtre des paramètres. Logique pure, testée.
/// </summary>
sealed class ClickGate
{
    /// <summary>Durée pendant laquelle un relâchement qui suit un double-clic est ignoré.</summary>
    public const int SuppressMs = 600;

    DateTime _lastDouble = DateTime.MinValue;

    public void MarkDouble(DateTime now) => _lastDouble = now;

    /// <summary>Vrai si ce relâchement de bouton est un vrai clic simple (et non la fin d'un double-clic).</summary>
    public bool AllowSingle(DateTime now) => (now - _lastDouble).TotalMilliseconds > SuppressMs;
}
