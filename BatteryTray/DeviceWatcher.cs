namespace BatteryTray;

/// <summary>
/// Quand relancer une lecture après un évènement matériel : on attend 2 s (les évènements arrivent par rafales),
/// et jamais moins de 20 s après le début de la lecture précédente (nos propres lectures Bluetooth peuvent
/// elles-mêmes provoquer des évènements : sans cette garde, on tournerait en boucle). Logique pure, testée.
/// </summary>
public static class RefreshPolicy
{
    public const int DebounceMs = 2000;
    public const int MinSpacingMs = 20000;

    public static int DelayMs(DateTime now, DateTime lastRefreshStart)
    {
        double sinceLast = (now - lastRefreshStart).TotalMilliseconds;
        return (int)Math.Max(DebounceMs, MinSpacingMs - sinceLast);
    }
}

/// <summary>
/// Fenêtre invisible qui écoute les messages Windows signalant qu'un périphérique a été branché, débranché
/// ou que le PC sort de veille, pour actualiser les niveaux tout de suite au lieu d'attendre l'actualisation
/// périodique. (Une fenêtre de haut niveau reçoit ces messages même cachée ; un simple contrôle, non.)
/// </summary>
sealed class DeviceWatcher : Form
{
    const int WM_DEVICECHANGE = 0x0219, WM_POWERBROADCAST = 0x0218;
    const int DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int PBT_APMRESUMESUSPEND = 0x0007, PBT_APMRESUMEAUTOMATIC = 0x0012;

    public event Action? Changed;

    public DeviceWatcher()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        _ = Handle; // crée la fenêtre sans jamais l'afficher
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int what = (int)(m.WParam.ToInt64() & 0xFFFF);
            if (what is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) Changed?.Invoke();
        }
        else if (m.Msg == WM_POWERBROADCAST)
        {
            int what = (int)(m.WParam.ToInt64() & 0xFFFF);
            if (what is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND) Changed?.Invoke();
        }
        base.WndProc(ref m);
    }
}
