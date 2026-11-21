// Un seul observateur d'annonces BLE (BluetoothLEAdvertisementWatcher de WinRT, balayage passif) partagé par les pilotes qui lisent des niveaux de batterie dans des diffusions : messages de proximité AirPods et diffusions
// de batterie Fast Pair. Il ne fait qu'écouter ; rien n'est jamais envoyé. Les messages reçus sont remis aux analyseurs sur un fil du pool WinRT : ils verrouillent donc ce qu'ils stockent.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/ble_adverts.cpp et .h, porté en C# — voir NOTICE.md (la projection WinRT de .NET remplace les déclarations ABI manuelles du code C++).
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace BatteryTray.Providers.Hid;

public static class BleAdverts
{
    const byte ServiceData16 = 0x16; // type AD « Service Data - 16-bit UUID »
    static BluetoothLEAdvertisementWatcher? _watcher;
    static readonly object Gate = new();

    /// <summary>Démarre l'observateur, ou le redémarre après coupure de la radio. Renvoie vrai s'il vient d'être démarré, pour laisser aux appareils le temps d'être entendus (ils diffusent toutes les 1-2 s).</summary>
    public static bool Ensure()
    {
        lock (Gate)
        {
            try
            {
                if (_watcher == null)
                {
                    _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                    _watcher.Received += OnReceived;
                    _watcher.Start();
                    return true;
                }
                if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted) { _watcher.Start(); return true; }
            }
            catch { _watcher = null; } // radio absente ou refusée : aucune diffusion à lire
            return false;
        }
    }

    static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        try
        {
            ulong addr = e.BluetoothAddress; int rssi = e.RawSignalStrengthInDBm;
            foreach (var md in e.Advertisement.GetManufacturerDataByCompanyId(0x004C))
                AirPodsDriver.OnAdvert(addr, rssi, md.Data.ToArray());
            foreach (var sec in e.Advertisement.GetSectionsByType(ServiceData16))
            {
                var d = sec.Data.ToArray();
                // Les données de service commencent par l'UUID de 16 bits, petit-boutiste : 2C FE = Google Fast Pair.
                if (d.Length > 2 && d[0] == 0x2C && d[1] == 0xFE) FastPairDriver.OnAdvert(addr, d[2..]);
            }
        }
        catch { /* une annonce illisible ne doit jamais arrêter l'écoute */ }
    }
}
