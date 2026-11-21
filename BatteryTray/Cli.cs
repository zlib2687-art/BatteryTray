using System.Runtime.InteropServices;
using System.Text;
using HidSharp;
using BatteryTray.Providers;
using BatteryTray.Providers.Hid;

namespace BatteryTray;

/// <summary>
/// Options en ligne de commande de diagnostic :
///  --list [fichier]   lit chaque source une fois et liste les appareils trouvés (nom, niveau, charge) ;
///  --dump [fichier]   décrit les périphériques HID connectés (VID:PID, nom, collections) pour signaler un appareil non pris en charge ;
///  --demo             affiche le flyout avec des appareils d'exemple, sans matériel ni écriture des réglages.
/// Sans nom de fichier, <c>--list</c> et <c>--dump</c> écrivent dans la console qui a lancé le programme.
/// </summary>
static class Cli
{
    public static IBatteryProvider[] AllProviders() => new IBatteryProvider[]
    {
        new ProX2Provider(), new LogitechHidppProvider(), new RazerProvider(),
        new HidDriversProvider(), new BluetoothProvider(), new WindowsBatteryProvider(), new GamepadProvider(), new PnpBatteryProvider(),
    };

    /// <summary>Lit chaque source une fois : durée et appareils trouvés.</summary>
    public static string BuildList()
    {
        var report = new StringBuilder();
        foreach (var p in AllProviders())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var readings = p.ReadAll();
                report.AppendLine($"{p.Name}: {readings.Count} appareil(s) en {sw.ElapsedMilliseconds} ms"
                    + (p is PnpBatteryProvider pnp ? $" (périphériques examinés : {pnp.LastScanned})" : ""));
                foreach (var r in readings) report.AppendLine($"    {Clean(r.Name)} — {r.PercentText} {r.Charge}");
            }
            catch (Exception e) { report.AppendLine($"{p.Name}: ERREUR {e.GetType().Name}: {e.Message}"); }
        }
        return report.ToString();
    }

    /// <summary>
    /// Décrit les périphériques HID présents. Ne contient ni numéro de série ni chemin de périphérique (qui peut contenir
    /// un identifiant d'instance) : ce rapport peut être joint à une demande de prise en charge sans révéler d'information personnelle.
    /// </summary>
    public static string BuildDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BatteryTray --dump : périphériques HID (sans numéro de série)");
        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices().ToList(); }
        catch (Exception e) { return sb.AppendLine($"ERREUR {e.GetType().Name}: {e.Message}").ToString(); }

        foreach (var g in devices.GroupBy(d => (d.VendorID, d.ProductID)).OrderBy(g => g.Key.VendorID).ThenBy(g => g.Key.ProductID))
        {
            var first = g.First();
            sb.AppendLine();
            sb.AppendLine($"{g.Key.VendorID:X4}:{g.Key.ProductID:X4}  {Clean(Safe(() => first.GetProductName()))}  [{Clean(Safe(() => first.GetManufacturer()))}]");
            foreach (var d in g)
            {
                string usage = "?";
                try
                {
                    var usages = d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues().Select(u => $"{(u >> 16):X4}:{(u & 0xFFFF):X4}")).Distinct().Take(6);
                    usage = string.Join(" ", usages);
                }
                catch { /* descripteur illisible : on le note tel quel */ }
                sb.AppendLine($"    usages {usage}  rapports max entrée/sortie/fonction {Safe(() => d.GetMaxInputReportLength())}/{Safe(() => d.GetMaxOutputReportLength())}/{Safe(() => d.GetMaxFeatureReportLength())}");
            }
        }
        return sb.ToString();
    }

    static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    static string Clean(string? s) => string.IsNullOrEmpty(s) ? "?" : new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
    static string Clean(object? s) => Clean(s?.ToString());

    /// <summary>Écrit dans le fichier demandé, sinon dans la console parente (le programme est une application fenêtrée).</summary>
    public static void Output(string text, string? file)
    {
        if (file != null && !file.StartsWith("--", StringComparison.Ordinal)) { File.WriteAllText(file, text); return; }
        if (AttachConsole(ATTACH_PARENT_PROCESS)) { Console.Out.Write(text); Console.Out.Flush(); }
        else File.WriteAllText(Path.Combine(Path.GetTempPath(), "batterytray-diagnostic.txt"), text);
    }

    // ------------------------------------------------------------------ --demo
    /// <summary>Affiche le flyout avec des appareils d'exemple ; se ferme quand on clique ailleurs. N'écrit aucun réglage.</summary>
    public static int RunDemo()
    {
        var now = DateTime.Now;
        var history = DemoData.History(now);
        var flyout = new FlyoutForm { History = _ => history };
        flyout.SettingsRequested += () => { };
        var context = new ApplicationContext();
        flyout.VisibleChanged += (_, _) => { if (!flyout.Visible) context.ExitThread(); };
        flyout.Toggle(DemoData.Readings(now));
        Application.Run(context);
        return 0;
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    const int ATTACH_PARENT_PROCESS = -1;
}
