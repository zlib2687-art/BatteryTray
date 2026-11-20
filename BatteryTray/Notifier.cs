namespace BatteryTray;

/// <summary>Destinataire des notifications (l'icône de la barre en production, un faux dans les tests).</summary>
interface IToastSink
{
    void Notify(string deviceId, string title, string text, bool warning);
}

/// <summary>
/// Deux notifications Windows : « batterie faible » et « charge terminée ». Chacune n'est envoyée
/// qu'UNE fois par passage de seuil (état mémorisé dans les réglages, donc pas de répétition après
/// un redémarrage). Réarmement : la batterie repasse 5 points au-dessus du seuil ou se met en
/// charge (faible) ; elle redescend sous 95 % ou cesse de charger (chargée).
/// </summary>
sealed class Notifier
{
    public const int Hysteresis = 5;
    readonly AppSettings _s;
    readonly IToastSink _sink;

    public Notifier(AppSettings settings, IToastSink sink)
    {
        _s = settings;
        _sink = sink;
    }

    public void Evaluate(IReadOnlyList<BatteryReading> readings)
    {
        bool dirty = false;
        foreach (var r in readings)
        {
            if (r.Percent is not int p || r.Offline) continue; // une valeur périmée ne déclenche aucune alerte
            bool plugged = r.Charge is ChargeState.Charging or ChargeState.Full;
            int threshold = _s.PrefFor(r.Id).LowThreshold ?? _s.LowThreshold;

            // ----- batterie faible -----
            if (!plugged && p <= threshold)
            {
                if (_s.LowNotified.Add(r.Id))
                {
                    dirty = true;
                    if (_s.NotifyLow)
                        _sink.Notify(r.Id, Loc.T("notif.low.title"), Loc.T("notif.low.body", r.Name, p), warning: true);
                }
            }
            else if ((plugged || p > threshold + Hysteresis) && _s.LowNotified.Remove(r.Id)) dirty = true;

            // ----- charge terminée (appareil branché et arrivé à 100 %) -----
            bool full = r.Charge == ChargeState.Full || (plugged && p >= 100);
            if (full)
            {
                if (_s.FullNotified.Add(r.Id))
                {
                    dirty = true;
                    if (_s.NotifyFull)
                        _sink.Notify(r.Id, Loc.T("notif.full.title"), Loc.T("notif.full.body", r.Name, p), warning: false);
                }
            }
            else if ((!plugged || p < 95) && _s.FullNotified.Remove(r.Id)) dirty = true;
        }
        if (dirty) _s.Save();
    }
}
