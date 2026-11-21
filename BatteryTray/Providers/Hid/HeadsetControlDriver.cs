// Passerelle facultative vers HeadsetControl (github.com/Sapd/HeadsetControl), qui prend en charge bien plus de casques que les pilotes intégrés. Si headsetcontrol.exe se trouve à côté de BatteryTray.exe, il est lancé avec
// `-b -o json --timeout 1500` et tout casque non déjà pris en charge ici est ajouté.
// Dérivé de BatteryHub (krisgfx9, GPL-3.0), src/drivers/headsetcontrol.cpp, porté en C# — voir NOTICE.md.
// Différence volontaire avec l'original : l'exécutable n'est cherché QUE dans le dossier du programme, jamais dans le PATH (un headsetcontrol.exe déposé dans un dossier du PATH ne doit pas être exécuté à notre insu) ; la sortie
// est lue avec System.Text.Json et bornée à 1 Mo ; le processus est tué après 30 s. HeadsetControl peut prendre plusieurs secondes par casque : il tourne donc sur son propre fil et la lecture régulière utilise son dernier résultat
// au lieu de l'attendre. Seule la batterie est demandée (-b), avec un court délai HID pour qu'un casque endormi ne le bloque pas.
using System.Diagnostics;
using System.Text.Json;

namespace BatteryTray.Providers.Hid;

public static class HeadsetControlDriver
{
    public sealed record Device(ushort Vid, ushort Pid, DeviceStatus Status);

    /// <summary>Analyse la sortie JSON de HeadsetControl (texte éventuellement précédé de bruit avant le premier « { »).</summary>
    public static List<Device> Parse(string text)
    {
        var list = new List<Device>();
        int start = text.IndexOf('{');
        if (start < 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(text[start..], new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return list;
            foreach (var d in devices.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("id_vendor", out var v) || !d.TryGetProperty("id_product", out var p) || !d.TryGetProperty("battery", out var bat) || bat.ValueKind != JsonValueKind.Object) continue;
                if (!TryHex(v, out ushort vid) || !TryHex(p, out ushort pid)) continue;
                string st = bat.TryGetProperty("status", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() ?? "" : "";
                string name = d.TryGetProperty("device", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
                var s = new DeviceStatus
                {
                    Key = "hsc:" + HidUtil.Hex4(vid) + ":" + HidUtil.Hex4(pid), Name = name.Length > 0 ? name : "Headset", Kind = DeviceKind.Headset, Via = "HeadsetControl",
                    Charging = st == "BATTERY_CHARGING", Connected = st is "BATTERY_AVAILABLE" or "BATTERY_CHARGING",
                };
                if (s.Connected && bat.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.TryGetDouble(out double level) && level >= 0) s.Percent = HidUtil.Clamp100((int)level);
                list.Add(new Device(vid, pid, s));
            }
        }
        catch (JsonException) { /* sortie invalide : aucun casque */ }
        return list;
    }

    static bool TryHex(JsonElement e, out ushort value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.String && ushort.TryParse(e.GetString()?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    static string? FindExe()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "headsetcontrol.exe");
        return File.Exists(local) ? local : null;
    }

    static string Run(string exe, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-b -o json --timeout 1500") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var err = p.StandardError.ReadToEndAsync();
            var buf = new char[4096]; var sb = new System.Text.StringBuilder();
            var reader = p.StandardOutput;
            var task = Task.Run(() => { int n; while ((n = reader.Read(buf, 0, buf.Length)) > 0) { sb.Append(buf, 0, n); if (sb.Length > (1 << 20)) break; } });
            if (!task.Wait(timeoutMs)) { try { p.Kill(true); } catch { } return ""; }
            p.WaitForExit(2000);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static readonly object Lock = new();
    static List<Device> _devices = new();
    static long _updated;
    static int _running;

    static void Refresh()
    {
        var exe = FindExe();
        var found = exe == null ? new List<Device>() : Parse(Run(exe, 30000));
        lock (Lock) { _devices = found; _updated = Environment.TickCount64; }
    }

    public static void Poll(PollContext ctx)
    {
        if (FindExe() == null) return;
        long now = Environment.TickCount64;
        if (_updated == 0) Refresh(); // première lecture : on attend, pour que le premier affichage (et --list) soit complet
        else if (now - _updated > 50_000 && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            Task.Run(() => { try { Refresh(); } finally { Interlocked.Exchange(ref _running, 0); } });
        List<Device> devices; lock (Lock) devices = _devices.ToList();
        foreach (var h in devices) if (!ctx.IsClaimed(h.Vid, h.Pid)) ctx.Add(h.Status, h.Vid, h.Pid);
    }
}
