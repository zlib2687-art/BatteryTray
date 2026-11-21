// Couche HID commune aux pilotes de marques.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0) : src/hid.h et src/drivers/drivers.h, portés en C# — voir NOTICE.md.
// Convention des tampons (celle de hidapi, pour que les protocoles décrits par d'autres projets s'appliquent tels quels) :
//  * Write / SetFeature : data[0] est l'identifiant de rapport (0 si l'appareil ne numérote pas ses rapports) ; le tampon est complété par des zéros ;
//  * Read : un identifiant 0 en tête est retiré, donc data[0] est le premier octet utile pour un rapport non numéroté,
//    et l'identifiant du rapport pour un rapport numéroté.
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HidSharp;

namespace BatteryTray.Providers.Hid;

/// <summary>Une collection HID de premier niveau d'un périphérique (page et usage, longueurs de rapports, interface USB).</summary>
public sealed class HidColl
{
    public string Path { get; init; } = "";
    public string Product { get; init; } = "";
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    /// <summary>Numéro d'interface USB (MI_xx dans le chemin), -1 si inconnu.</summary>
    public int Iface { get; init; } = -1;
    public ushort Page { get; init; }
    public ushort Usage { get; init; }
    public int InLen { get; init; }
    public int OutLen { get; init; }
    public int FeatLen { get; init; }
    internal HidDevice? Device { get; init; }

    static readonly Regex InterfaceRx = new(@"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extrait le numéro d'interface d'un chemin de périphérique (« ...mi_03... » → 3), -1 sinon. Logique pure, testée.</summary>
    public static int InterfaceFromPath(string? path)
    {
        var m = InterfaceRx.Match(path ?? "");
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
    }

    public static List<HidColl> Enumerate()
    {
        var list = new List<HidColl>();
        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices().ToList(); }
        catch { return list; }
        foreach (var d in devices)
        {
            ushort page = 0, usage = 0;
            try
            {
                var item = d.GetReportDescriptor().DeviceItems.FirstOrDefault();
                if (item != null)
                {
                    uint u = item.Usages.GetAllValues().FirstOrDefault();
                    page = (ushort)(u >> 16); usage = (ushort)(u & 0xFFFF);
                }
            }
            catch { /* descripteur illisible : page et usage restent à 0, le pilote ne retiendra pas cette collection */ }
            list.Add(new HidColl
            {
                Path = d.DevicePath ?? "",
                Product = Try(() => d.GetProductName()) ?? "",
                Vid = (ushort)d.VendorID, Pid = (ushort)d.ProductID,
                Iface = InterfaceFromPath(d.DevicePath),
                Page = page, Usage = usage,
                InLen = Try(() => d.GetMaxInputReportLength()),
                OutLen = Try(() => d.GetMaxOutputReportLength()),
                FeatLen = Try(() => d.GetMaxFeatureReportLength()),
                Device = d,
            });
        }
        return list;
    }

    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
}

/// <summary>Résultat brut d'un pilote pour un appareil.</summary>
public sealed class DeviceStatus
{
    /// <summary>Clé stable (ne change pas si le récepteur change de port USB).</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Other;
    /// <summary>« USB cable », « 2.4 GHz receiver »…</summary>
    public string Via { get; set; } = "";
    /// <summary>Niveau 0–100, ou -1 si inconnu.</summary>
    public int Percent { get; set; } = -1;
    public bool Charging { get; set; }
    /// <summary>Faux si l'appareil est éteint ou hors de portée : aucune valeur n'est alors affichée.</summary>
    public bool Connected { get; set; } = true;
    public ushort Vid { get; set; }
    /// <summary>Précision affichée à la place de « Via » quand elle est connue (ex. « docked »), sinon null.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Contexte d'une lecture : les collections HID présentes, les appareils déjà pris en charge (pour que les pilotes génériques
/// ne les signalent pas deux fois) et les résultats.
/// </summary>
public sealed class PollContext
{
    public List<HidColl> Colls { get; }
    readonly HashSet<(ushort, ushort)> _claimed = new();
    public List<DeviceStatus> Out { get; } = new();

    public PollContext(List<HidColl> colls) => Colls = colls;

    public bool IsClaimed(ushort vid, ushort pid) => _claimed.Contains((vid, pid));
    public void Claim(ushort vid, ushort pid) => _claimed.Add((vid, pid));

    public void Add(DeviceStatus s, ushort vid, ushort pid)
    {
        if (s.Vid == 0) s.Vid = vid;
        Out.Add(s);
        Claim(vid, pid);
    }

    /// <summary>Clé qui reste la même quand le récepteur change de port : <paramref name="baseKey"/> pour le premier appareil, puis « #2 », « #3 »…</summary>
    public string StableKey(string baseKey)
    {
        bool Taken(string k) => Out.Any(s => s.Key == k && s.Connected);
        if (!Taken(baseKey)) return baseKey;
        for (int n = 2; ; n++) if (!Taken($"{baseKey}#{n}")) return $"{baseKey}#{n}";
    }

    /// <summary>Couples (VID, PID) distincts d'un fabricant, pas encore pris en charge.</summary>
    public List<(ushort Vid, ushort Pid)> Products(ushort vid) =>
        Colls.Where(c => c.Vid == vid && !IsClaimed(c.Vid, c.Pid)).Select(c => (c.Vid, c.Pid)).Distinct().OrderBy(p => p.Pid).ToList();

    /// <summary>Collections d'un produit, éventuellement filtrées.</summary>
    public List<HidColl> Find(ushort vid, ushort pid, Func<HidColl, bool>? pred = null) =>
        Colls.Where(c => c.Vid == vid && c.Pid == pid && (pred == null || pred(c))).ToList();
}

/// <summary>Accès à un périphérique HID : rapports de fonctionnalité sans accès lecture/écriture, ou flux complet.</summary>
public sealed class HidDev : IDisposable
{
    IntPtr _handle = new(-1);
    HidStream? _stream;
    HidColl? _coll;

    public HidColl Coll => _coll ?? throw new InvalidOperationException("périphérique fermé");
    public bool Ok => _stream != null || _handle != new IntPtr(-1);

    /// <summary>Accès nul : suffit pour les rapports de fonctionnalité, fonctionne sur les souris et claviers que Windows protège.</summary>
    public bool OpenFeatureOnly(HidColl c)
    {
        Close();
        _handle = CreateFile(c.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_handle == new IntPtr(-1)) return false;
        _coll = c;
        return true;
    }

    /// <summary>Flux lecture/écriture.</summary>
    public bool Open(HidColl c)
    {
        Close();
        if (c.Device == null || !c.Device.TryOpen(out var stream)) return false;
        stream.ReadTimeout = 500;
        stream.WriteTimeout = 1000; // sans délai, un récepteur qui ne répond plus bloquerait la lecture pour toujours
        _stream = stream;
        _coll = c;
        return true;
    }

    public void Close()
    {
        _stream?.Dispose(); _stream = null;
        if (_handle != new IntPtr(-1)) { CloseHandle(_handle); _handle = new IntPtr(-1); }
    }

    public void Dispose() => Close();

    /// <summary>Envoie un rapport de sortie : data[0] = identifiant (0 si non numéroté), complété par des zéros.</summary>
    public bool Write(byte[] data, int timeoutMs = 500)
    {
        if (_stream == null || _coll == null) return false;
        try
        {
            var buf = new byte[Math.Max(_coll.OutLen, data.Length)];
            data.CopyTo(buf, 0);
            _stream.WriteTimeout = timeoutMs;
            _stream.Write(buf);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { return false; }
    }

    public bool Write(params int[] bytes) => Write(bytes.Select(b => (byte)b).ToArray());

    /// <summary>Lit un rapport d'entrée (identifiant 0 en tête retiré). Renvoie null en cas de délai dépassé ou d'erreur.</summary>
    public byte[]? Read(int timeoutMs)
    {
        if (_stream == null || _coll == null) return null;
        try
        {
            var buf = new byte[Math.Max(_coll.InLen, 65)];
            _stream.ReadTimeout = Math.Max(1, timeoutMs);
            int n = _stream.Read(buf, 0, buf.Length);
            if (n <= 0) return null;
            int start = buf[0] == 0 ? 1 : 0;
            return buf.Skip(start).Take(n - start).ToArray();
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { return null; }
    }

    /// <summary>Jette les rapports d'entrée en attente (réponses anciennes d'une autre application).</summary>
    public void Flush()
    {
        if (_stream == null) return;
        try
        {
            _stream.ReadTimeout = 1;
            var buf = new byte[Math.Max(_coll?.InLen ?? 65, 65)];
            for (int i = 0; i < 64; i++) _stream.Read(buf, 0, buf.Length);
        }
        catch { /* délai dépassé : la file est vide */ }
    }

    public bool SetFeature(byte[] data)
    {
        if (_coll == null) return false;
        var buf = new byte[Math.Max(_coll.FeatLen, data.Length)];
        data.CopyTo(buf, 0);
        if (_handle != new IntPtr(-1)) return HidD_SetFeature(_handle, buf, (uint)buf.Length);
        if (_stream == null) return false;
        try { _stream.SetFeature(buf); return true; } catch { return false; }
    }

    /// <summary>Lit un rapport de fonctionnalité ; out[0] = identifiant de rapport.</summary>
    public bool GetFeature(byte reportId, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (_coll == null || _coll.FeatLen <= 0) return false;
        var buf = new byte[_coll.FeatLen];
        buf[0] = reportId;
        bool ok;
        if (_handle != new IntPtr(-1)) ok = HidD_GetFeature(_handle, buf, (uint)buf.Length);
        else if (_stream != null) { try { _stream.GetFeature(buf); ok = true; } catch { ok = false; } }
        else ok = false;
        if (ok) data = buf;
        return ok;
    }

    /// <summary>Envoie un rapport de sortie par le canal de contrôle (HidD_SetOutputReport) ; data[0] = identifiant de rapport.</summary>
    public bool SetOutputReport(byte[] data)
    {
        if (_stream == null || _coll == null) return false;
        try
        {
            var buf = new byte[Math.Max(_coll.OutLen, data.Length)];
            data.CopyTo(buf, 0);
            _stream.Write(buf); // HidSharp 2.1 n'expose pas HidD_SetOutputReport : écriture ordinaire
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or NotSupportedException) { return false; }
    }

    /// <summary>Lit un rapport d'entrée par le canal de contrôle (HidD_GetInputReport) ; data[0] = identifiant de rapport.</summary>
    public bool GetInputReport(byte reportId, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (_stream == null || _coll == null || _coll.InLen <= 0) return false;
        var buf = new byte[_coll.InLen];
        buf[0] = reportId;
        try { int n = ReadInto(buf); if (n <= 0) return false; }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or NotSupportedException) { return false; }
        data = buf;
        return true;
    }

    int ReadInto(byte[] buf) { _stream!.ReadTimeout = 20; var tmp = new byte[buf.Length]; int n = _stream.Read(tmp, 0, tmp.Length); if (n > 0) Array.Copy(tmp, buf, Math.Min(n, buf.Length)); return n; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, uint len);
}

/// <summary>Petits utilitaires de conversion de niveaux, communs aux pilotes (logique pure, testée).</summary>
public static class HidUtil
{
    public static string Hex4(int v) => (v & 0xFFFF).ToString("X4");
    public static int Clamp100(int v) => Math.Clamp(v, 0, 100);

    /// <summary>Convertit linéairement lo..hi en 0..100, borné.</summary>
    public static int MapTo100(int v, int lo, int hi) => hi == lo ? 0 : Clamp100((int)Math.Round((v - lo) * 100.0 / (hi - lo)));

    /// <summary>Interpolation linéaire par morceaux dans des tables triées par tension décroissante.</summary>
    public static int Interpolate(int mv, int[] mvTable, int[] pctTable)
    {
        int n = Math.Min(mvTable.Length, pctTable.Length);
        if (n == 0) return 0;
        if (mv >= mvTable[0]) return pctTable[0];
        if (mv <= mvTable[n - 1]) return pctTable[n - 1];
        for (int i = 1; i < n; i++)
        {
            if (mv < mvTable[i]) continue; // on cherche le tronçon [tension i, tension i-1] qui contient mv
            int span = mvTable[i - 1] - mvTable[i];
            if (span <= 0) return pctTable[i];
            return (int)Math.Round(pctTable[i] + (pctTable[i - 1] - pctTable[i]) * (mv - mvTable[i]) / (double)span);
        }
        return pctTable[n - 1];
    }

    /// <summary>
    /// Type d'appareil d'après les collections de premier niveau qu'il expose (souris, puis manette, puis clavier) ;
    /// Other si rien ne permet de conclure. Logique pure, testée.
    /// </summary>
    public static DeviceKind KindFromCollections(IReadOnlyList<HidColl> all, ushort vid, ushort pid)
    {
        var mine = all.Where(c => c.Vid == vid && c.Pid == pid && c.Page == 0x01).ToList();
        if (mine.Any(c => c.Usage == 0x02)) return DeviceKind.Mouse;
        if (mine.Any(c => c.Usage is 0x04 or 0x05)) return DeviceKind.Gamepad;
        if (mine.Any(c => c.Usage == 0x06)) return DeviceKind.Keyboard;
        return DeviceKind.Other;
    }
}