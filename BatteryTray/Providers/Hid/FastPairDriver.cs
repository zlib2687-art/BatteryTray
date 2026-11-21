// Diffusions de batterie Google Fast Pair : niveaux gauche, droit et boîtier d'écouteurs en Bluetooth.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/fastpair.cpp (et la partie Fast Pair de generic.cpp), porté en C# — voir NOTICE.md.
// Les écouteurs Fast Pair (Jabra Elite, Pixel Buds, nombreux Sony / JBL / Bose…) placent leur batterie dans l'annonce Fast Pair non découvrable, en clair, pour la fenêtre de batterie d'Android. Windows ne lit qu'un niveau
// (service Batterie standard) : cette annonce sert au détail gauche / droit / boîtier. Données de service de l'UUID 16 bits 0xFE2C, après l'UUID :
//   [0] version / drapeaux (0x00), puis des champs à octet d'en-tête 0bLLLLTTTT (L = longueur, T = type) : T 0 / 2 filtre de clé de compte, T 1 sel, T 3 / 4 batterie (UI affichée / masquée) : un octet par composant, dans l'ordre
//   gauche, droite, boîtier ; bit 7 = en charge, bits 0-6 = niveau 0-100, 0x7F = inconnu ; T 5 temps de batterie restant.
// Les annonces en mode découvrable ne portent que l'identifiant de modèle de 3 octets (sans octet de version) et sont ignorées. Disposition d'après la spécification Fast Pair (extension « batterynotification »), vérifiée sur un
// Jabra Elite 8 Active contre l'application Sound+. Les messages sont associés à l'appareil appairé par adresse Bluetooth : c'est l'adresse publique avec laquelle Windows l'a appairé (un appareil qui diffuse depuis une adresse
// aléatoire tournante n'est pas reconnu).
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryTray.Providers.Hid;

public static class FastPairDriver
{
    public sealed class Battery { public int Left = -1, Right = -1, Box = -1; public bool LeftChg, RightChg, BoxChg; public long Tick; }

    static readonly object Lock = new();
    static readonly Dictionary<ulong, Battery> Batteries = new();

    static int Component(byte b, out bool charging)
    {
        int level = b & 0x7F;
        charging = level <= 100 && (b & 0x80) != 0;
        return level <= 100 ? level : -1;
    }

    /// <summary>Analyse les données de service (octets après l'UUID) ; null si version non nulle (dont l'identifiant de modèle découvrable) ou sans champ de batterie exploitable.</summary>
    public static Battery? Parse(byte[] d)
    {
        if (d.Length < 2 || d[0] != 0x00) return null;
        for (int i = 1; i < d.Length;)
        {
            int len = d[i] >> 4, type = d[i] & 0x0F;
            if (i + 1 + len > d.Length) return null;
            if ((type == 3 || type == 4) && len >= 1)
            {
                var b = new Battery { Left = Component(d[i + 1], out bool lc) };
                b.LeftChg = lc;
                if (len >= 2) { b.Right = Component(d[i + 2], out bool rc); b.RightChg = rc; }
                if (len >= 3) { b.Box = Component(d[i + 3], out bool xc); b.BoxChg = xc; }
                return b.Left >= 0 || b.Right >= 0 || b.Box >= 0 ? b : null;
            }
            i += 1 + len;
        }
        return null;
    }

    public static void OnAdvert(ulong addr, byte[] data)
    {
        if (Parse(data) is not { } b) return;
        b.Tick = Environment.TickCount64;
        lock (Lock) Batteries[addr] = b;
    }

    /// <summary>Dernière diffusion de l'appareil de cette adresse Bluetooth, si entendue depuis moins de 5 minutes.</summary>
    public static Battery? Lookup(ulong addr)
    {
        lock (Lock) return Batteries.TryGetValue(addr, out var b) && Environment.TickCount64 - b.Tick < 5 * 60 * 1000 ? b : null;
    }

    static string Pct(int v) => v < 0 ? "?" : v + "%";

    /// <summary>Détail « L 80%, R 75%, case 60% » (⚡ pour une charge). Pourcentage : le plus bas des écouteurs en décharge (celui qui sert), sinon le plus bas en charge ; sinon le boîtier.</summary>
    public static (int Percent, bool Charging, string Detail) Summarise(Battery b)
    {
        int disLo = -1, chgLo = -1;
        foreach (var (pct, chg) in new[] { (b.Left, b.LeftChg), (b.Right, b.RightChg) })
        {
            if (pct < 0) continue;
            if (chg) { if (chgLo < 0 || pct < chgLo) chgLo = pct; } else if (disLo < 0 || pct < disLo) disLo = pct;
        }
        int percent = disLo >= 0 ? disLo : chgLo >= 0 ? chgLo : b.Box;
        bool charging = disLo < 0 && (chgLo >= 0 || b.BoxChg);
        var parts = new List<string>();
        if (b.Left >= 0) parts.Add("L " + Pct(b.Left) + (b.LeftChg ? " ⚡" : ""));
        if (b.Right >= 0) parts.Add("R " + Pct(b.Right) + (b.RightChg ? " ⚡" : ""));
        if (b.Box >= 0) parts.Add("case " + Pct(b.Box) + (b.BoxChg ? " ⚡" : ""));
        return (percent, charging, string.Join(", ", parts));
    }

    static bool _watching;

    public static void Poll(PollContext ctx)
    {
        List<(ulong Addr, string Name, bool Connected)> paired;
        try { paired = PairedDevices(); } catch { return; }
        if (!paired.Any(p => p.Connected)) return; // pas d'écoute BLE tant qu'aucun appareil appairé n'est connecté
        bool justStarted = !_watching && BleAdverts.Ensure(); _watching = true;
        if (!justStarted) BleAdverts.Ensure();
        for (int i = 0; justStarted && i < 15 && !paired.Any(p => Lookup(p.Addr) != null); i++) Thread.Sleep(200); // on laisse aux écouteurs le temps de diffuser
        foreach (var (addr, name, connected) in paired)
        {
            if (!connected || Lookup(addr) is not { } b) continue;
            var (pct, chg, detail) = Summarise(b);
            if (pct < 0) continue;
            ctx.Out.Add(new DeviceStatus { Key = "fastpair:" + addr.ToString("X12"), Name = name.Length > 0 ? name : "Bluetooth earbuds", Kind = DeviceKind.Headset, Via = "Bluetooth", Percent = pct, Charging = chg, Detail = detail });
        }
    }

    /// <summary>Appareils Bluetooth appairés : (adresse, nom, connecté).</summary>
    internal static List<(ulong Addr, string Name, bool Connected)> PairedDevices()
    {
        var list = new List<(ulong, string, bool)>();
        var infos = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask().GetAwaiter().GetResult();
        foreach (var info in infos)
        {
            using var dev = BluetoothDevice.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
            if (dev == null) continue;
            list.Add((dev.BluetoothAddress, dev.Name ?? "", dev.ConnectionStatus == BluetoothConnectionStatus.Connected));
        }
        return list;
    }
}
