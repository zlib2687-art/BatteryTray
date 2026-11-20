using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTray.Providers;

/// <summary>
/// Appareils Bluetooth appairés ET connectés, quelle que soit la marque (API officielles Windows) :
///  - Bluetooth LE : service GATT Battery (0x180F), caractéristique Battery Level (0x2A19).
///  - Bluetooth classique : propriété PnP DEVPKEY_Device_BatteryLevel, renseignée par Windows
///    quand l'appareil la communique (ex. profil mains libres des casques).
/// Si l'appareil ne communique pas sa batterie, il n'apparaît pas : aucune valeur n'est inventée.
/// </summary>
public sealed class BluetoothProvider : IBatteryProvider
{
    public string Name => "Bluetooth";

    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ConnectedKey = "System.Devices.Aep.IsConnected";

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var list = new List<BatteryReading>();
        list.AddRange(ReadClassic());
        list.AddRange(ReadLowEnergy());
        return list;
    }

    // ---------- Bluetooth classique ----------
    static IEnumerable<BatteryReading> ReadClassic()
    {
        var infos = Wait(DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true), new[] { BatteryKey, ConnectedKey }));
        foreach (var info in infos)
        {
            if (info.Properties.TryGetValue(ConnectedKey, out var c) && c is bool b && !b) continue;
            if (!info.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;

            int pct;
            try { pct = Convert.ToInt32(v); } catch { continue; }
            if (pct is < 0 or > 100) continue;

            yield return new BatteryReading("bt-" + Short(info.Id), info.Name, KindFromName(info.Name), pct, null, null, DateTime.Now);
        }
    }

    // ---------- Bluetooth LE ----------
    static IEnumerable<BatteryReading> ReadLowEnergy()
    {
        var infos = Wait(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
        foreach (var info in infos)
        {
            BatteryReading? reading = null;
            try
            {
                using var dev = Wait(BluetoothLEDevice.FromIdAsync(info.Id));
                if (dev == null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                var svcs = Wait(dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached));
                if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) continue;
                using var releaseServices = new DisposeAll(svcs.Services); // libère les services GATT quelle que soit la sortie

                // Un appareil à plusieurs batteries (écouteurs : gauche, droit, boîtier) expose le service
                // Batterie en plusieurs exemplaires ; chacun est lu séparément.
                var parts = new List<BatteryPart>();
                foreach (var svc in svcs.Services)
                {
                    var chars = Wait(svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached));
                    if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) continue;

                    var ch = chars.Characteristics[0];
                    var read = Wait(ch.ReadValueAsync(BluetoothCacheMode.Uncached));
                    if (read.Status != GattCommunicationStatus.Success) continue;

                    int pct = DataReader.FromBuffer(read.Value).ReadByte();
                    if (pct > 100) continue;
                    parts.Add(new BatteryPart(LabelOf(ch), pct));
                }
                if (parts.Count == 0) continue;

                reading = new BatteryReading("ble-" + Short(info.Id), info.Name, KindFromName(info.Name),
                    BatteryParts.Aggregate(parts), ReadChargeState(svcs.Services[0]), null, DateTime.Now)
                {
                    Parts = parts.Count > 1 ? parts : null,
                };
            }
            catch { /* appareil en veille, accès refusé, hors de portée : on passe au suivant */ }
            if (reading != null) yield return reading;
        }
    }

    /// <summary>Lit le descripteur « Presentation Format » (0x2904) : octets 5-6 = description (gauche, droite…).</summary>
    static string LabelOf(GattCharacteristic characteristic)
    {
        try
        {
            var ds = Wait(characteristic.GetDescriptorsForUuidAsync(GattDescriptorUuids.CharacteristicPresentationFormat, BluetoothCacheMode.Uncached));
            if (ds.Status != GattCommunicationStatus.Success || ds.Descriptors.Count == 0) return BatteryParts.Other;
            var v = Wait(ds.Descriptors[0].ReadValueAsync(BluetoothCacheMode.Uncached));
            if (v.Status != GattCommunicationStatus.Success) return BatteryParts.Other;

            var reader = DataReader.FromBuffer(v.Value);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return bytes.Length >= 7 ? BatteryParts.LabelFromDescription((ushort)(bytes[5] | (bytes[6] << 8))) : BatteryParts.Other;
        }
        catch { return BatteryParts.Other; }
    }

    /// <summary>Lit, si l'appareil la fournit, la caractéristique « Battery Power State » (0x2A1A) : état de charge.</summary>
    static string? ReadChargeState(GattDeviceService service)
    {
        try
        {
            var chars = Wait(service.GetCharacteristicsForUuidAsync(BluetoothUuidHelper.FromShortId(0x2A1A), BluetoothCacheMode.Uncached)); // Battery Power State
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;
            var read = Wait(chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached));
            return read.Status == GattCommunicationStatus.Success
                ? BatteryParts.ChargeFromPowerState(DataReader.FromBuffer(read.Value).ReadByte())
                : null;
        }
        catch { return null; } // caractéristique facultative : absente chez la plupart des appareils
    }

    // ---------- utilitaires ----------
    internal static DeviceKind KindFromName(string name) => DeviceNames.Guess(name);

    static string Short(string id) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..8];

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op) => WinRt.Wait(op);
}
