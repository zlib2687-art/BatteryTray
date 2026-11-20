using Windows.Devices.Enumeration;

namespace BatteryTray.Providers;

/// <summary>
/// Source GÉNÉRIQUE (toutes marques) : balaie tous les périphériques actuellement présents et retient ceux pour
/// lesquels Windows a renseigné la propriété standard DEVPKEY_Device_BatteryLevel (casques et écouteurs Bluetooth,
/// claviers et souris Bluetooth, téléphones, etc. — quelle que soit la marque). Le balayage est plus lourd que les
/// autres sources : il n'est refait que toutes les 5 minutes au plus, le résultat précédent est réutilisé entre-temps.
/// </summary>
public sealed class PnpBatteryProvider : IBatteryProvider
{
    public string Name => "Périphériques Windows";

    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string PresentFilter = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
    static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>Nombre de périphériques examinés lors du dernier balayage (diagnostic : 0 signalerait un filtre sans effet).</summary>
    public int LastScanned { get; private set; }

    DateTime _lastScan = DateTime.MinValue;
    IReadOnlyList<BatteryReading> _cached = Array.Empty<BatteryReading>();

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        if (DateTime.Now - _lastScan < MinInterval) return Refresh(_cached);
        _lastScan = DateTime.Now;

        var list = new List<BatteryReading>();
        var infos = Wait(DeviceInformation.FindAllAsync(PresentFilter, new[] { BatteryKey }, DeviceInformationKind.Device));
        LastScanned = infos.Count;
        foreach (var info in infos)
        {
            if (!info.Properties.TryGetValue(BatteryKey, out var value) || value == null) continue;
            int pct;
            try { pct = Convert.ToInt32(value); } catch { continue; }
            if (pct is < 0 or > 100 || string.IsNullOrWhiteSpace(info.Name)) continue;

            list.Add(new BatteryReading("pnp-" + Hash(info.Id), info.Name, DeviceNames.Guess(info.Name), pct, null, null, DateTime.Now));
        }
        _cached = list;
        return list;
    }

    /// <summary>Entre deux balayages : mêmes appareils et mêmes valeurs, horodatage mis à jour.</summary>
    static IReadOnlyList<BatteryReading> Refresh(IReadOnlyList<BatteryReading> cached) =>
        cached.Select(r => r with { At = DateTime.Now }).ToList();

    static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) => WinRt.Wait(op, seconds: 20); // balayage plus long
}
