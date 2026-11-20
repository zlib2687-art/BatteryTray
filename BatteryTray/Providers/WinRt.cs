using Windows.Foundation;

namespace BatteryTray.Providers;

/// <summary>Attente d'une opération WinRT AVEC délai : un appareil Bluetooth qui ne répond pas ne doit pas bloquer toute la lecture.</summary>
static class WinRt
{
    public const int DefaultTimeoutSeconds = 10;

    public static T Wait<T>(IAsyncOperation<T> operation, int seconds = DefaultTimeoutSeconds)
    {
        var task = operation.AsTask();
        bool done;
        try { done = task.Wait(TimeSpan.FromSeconds(seconds)); }
        catch (AggregateException e) { throw e.InnerException ?? e; }

        if (!done)
        {
            try { operation.Cancel(); } catch { /* déjà terminée ou non annulable */ }
            throw new TimeoutException($"Opération Windows sans réponse après {seconds} s");
        }
        return task.Result;
    }
}

/// <summary>Libère plusieurs objets WinRT (services GATT…) à la fin d'un bloc, quel que soit le chemin de sortie.</summary>
sealed class DisposeAll : IDisposable
{
    readonly IEnumerable<IDisposable> _items;
    public DisposeAll(IEnumerable<IDisposable> items) => _items = items;

    public void Dispose()
    {
        foreach (var item in _items)
            try { item.Dispose(); } catch { /* déjà libéré */ }
    }
}
