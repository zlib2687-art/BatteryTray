// Manette GameSir Cyclone Pro (T4cp) sur son récepteur 2,4 GHz (3537:1015).
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/gamesir_ble.cpp et gamesir_ble_protocol.h, porté en C# (GATT WinRT à la place de l'API Win32 bluetoothleapis) — voir NOTICE.md.
// Le récepteur est pour Windows une manette Xbox 360 filaire (XInput « Full », sans vrai niveau), mais la manette continue d'annoncer en Bluetooth LE « GameSir-Cyclone Pro » et répond à la requête « infos de base » de l'application
// mobile de GameSir : service 0x8650, caractéristique 0x865F : activer les notifications, écrire 01 01 ; la manette notifie un paquet de 19 octets dont le dernier est la batterie. 01 01 est une requête « lire les infos de base » en
// lecture seule (aucun réglage modifié) ; les notifications sont ensuite désactivées. Le service 0xFF10 de la même manette est le canal de mise à jour du micrologiciel : jamais touché.
// Référence du protocole : bpbp-boop/GameSirBattery (déduit de l'application Android de GameSir, BluetoothLeService) ; testé sur un vrai Cyclone Pro sur son récepteur. La manette doit être appairée une fois avec Windows (Paramètres > Bluetooth).
// Paquet : [0..5] MAC [6..7] version matériel [8..9] version BT [10..11] version micrologiciel [12..17] date de build [18] batterie : 0..100 %, ou 255 en charge ; certains micrologiciels préfixent le paquet de C9.
// La connexion peut prendre des secondes (ou expirer manette éteinte) : la requête tourne donc sur son propre fil, au plus toutes les deux minutes, et la lecture régulière ne rapporte que le résultat en cache. Tant que la manette est
// connue, le récepteur est « pris » pour que le repli XInput ne l'affiche pas aussi comme une « manette Xbox » pleine.
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using System.Runtime.InteropServices.WindowsRuntime;

namespace BatteryTray.Providers.Hid;

public static class GameSirBleDriver
{
    const ushort Vid = 0x3537, DonglePid = 0x1015;
    static readonly Guid InfoService = new("00008650-0000-1000-8000-00805f9b34fb"), InfoChar = new("0000865f-0000-1000-8000-00805f9b34fb");
    const long QueryEveryMs = 120_000, RetryMs = 60_000, StaleMs = 6 * 60_000;

    public sealed record Reading(int Percent, bool Charging);

    /// <summary>Paquet « infos de base » : null si ce n'en est pas un ; (-1, vrai) en charge (255).</summary>
    public static Reading? Parse(byte[] p)
    {
        int off = 0, n = p.Length;
        if (n == 20 && p[0] == 0xC9) { off = 1; n--; }
        if (n != 19) return null;
        byte v = p[off + 18];
        if (v == 0xFF) return new Reading(-1, true);
        return v > 100 ? null : new Reading(v, false);
    }

    static readonly object Lock = new();
    static bool _running; static long _lastAttempt, _lastOk; static Reading? _reading; static string _address = "";

    static async Task<Reading?> QueryAsync(ulong address)
    {
        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (dev == null) return null;
        var svcs = await dev.GetGattServicesForUuidAsync(InfoService, BluetoothCacheMode.Uncached);
        if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) return null;
        using var svc = svcs.Services[0];
        var chars = await svc.GetCharacteristicsForUuidAsync(InfoChar, BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;
        var ch = chars.Characteristics[0];
        if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) return null;
        var tcs = new TaskCompletionSource<Reading?>();
        void Handler(GattCharacteristic s, GattValueChangedEventArgs e) { if (Parse(e.CharacteristicValue.ToArray()) is { } r) tcs.TrySetResult(r); }
        ch.ValueChanged += Handler;
        try
        {
            if (await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify) != GattCommunicationStatus.Success) return null;
            var write = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            await ch.WriteValueAsync(new byte[] { 0x01, 0x01 }.AsBuffer(), write);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            ch.ValueChanged -= Handler;
            try { await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
        }
    }

    static async Task<(ulong Address, string AddressText)?> FindPadAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in infos)
        {
            if (!info.Name.StartsWith("GameSir-Cyclone", StringComparison.OrdinalIgnoreCase)) continue;
            using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (dev != null) return (dev.BluetoothAddress, dev.BluetoothAddress.ToString("X12"));
        }
        return null;
    }

    static void QueryNow(ulong address)
    {
        Reading? r = null;
        try { r = QueryAsync(address).GetAwaiter().GetResult() ?? Task.Delay(1500).ContinueWith(_ => QueryAsync(address).GetAwaiter().GetResult()).GetAwaiter().GetResult(); } catch { } // les premiers appels GATT après la liaison échouent souvent : une reprise suffit
        lock (Lock)
        {
            if (r != null)
            {
                if (r.Charging && _lastOk != 0 && _reading != null) r = new Reading(_reading.Percent, true); // en charge, aucun niveau : on garde le dernier pourcentage connu avec l'éclair
                _reading = r; _lastOk = Environment.TickCount64;
            }
            _running = false;
        }
    }

    public static void Poll(PollContext ctx)
    {
        (ulong Address, string AddressText)? pad;
        try { pad = Task.Run(FindPadAsync).GetAwaiter().GetResult(); } catch { return; }
        if (pad is not { } p) return; // un seul Cyclone Pro par PC ; un second partagerait de toute façon l'identifiant du récepteur
        bool dongle = ctx.Colls.Any(c => c.Vid == Vid && c.Pid == DonglePid);
        Task? first = null;
        lock (Lock)
        {
            long now = Environment.TickCount64;
            if (_address != p.AddressText) { _address = p.AddressText; _lastOk = 0; _lastAttempt = 0; _reading = null; } // une autre manette appairée : on oublie l'ancienne lecture
            long wait = _lastOk != 0 && _lastOk >= _lastAttempt ? QueryEveryMs : RetryMs;
            if (!_running && (_lastAttempt == 0 || now - _lastAttempt >= wait))
            {
                bool isFirst = _lastAttempt == 0;
                _lastAttempt = now; _running = true;
                var t = Task.Run(() => QueryNow(p.Address));
                if (isFirst) first = t;
            }
        }
        // Une échéance absolue de 4 s, une fois par exécution : la première lecture (et --list) peut montrer le niveau ; une manette éveillée répond en 2-3 s. Les lectures suivantes n'attendent jamais.
        first?.Wait(4000);
        if (dongle) ctx.Claim(Vid, DonglePid); // le niveau XInput du récepteur est sans valeur (« Full ») : on l'écarte des replis
        Reading? reading; long lastOk;
        lock (Lock) { reading = _reading; lastOk = _lastOk; }
        if (reading == null || lastOk == 0 || Environment.TickCount64 - lastOk >= StaleMs) return;
        ctx.Add(new DeviceStatus
        {
            Key = "gamesir-ble:" + p.AddressText, Name = "GameSir Cyclone Pro", Kind = DeviceKind.Gamepad,
            Via = dongle ? "2.4 GHz receiver (battery over Bluetooth LE)" : "Bluetooth LE", Percent = reading.Percent, Charging = reading.Charging,
        }, Vid, dongle ? DonglePid : (ushort)0);
    }
}
