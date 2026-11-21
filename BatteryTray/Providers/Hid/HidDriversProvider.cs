using System.Diagnostics;

namespace BatteryTray.Providers.Hid;

/// <summary>
/// Pilotes de marques en lecture seule (souris, casques, claviers sans fil à récepteur propriétaire). Une seule énumération HID
/// par lecture ; chaque pilote regarde les collections présentes, ne parle qu'aux appareils qu'il reconnaît exactement
/// (VID:PID et forme de la collection) et « revendique » ce qu'il a traité. Un pilote qui échoue ou tarde ne bloque pas les autres.
/// Pilotes portés de BatteryHub (GPL-3.0) : voir NOTICE.md et l'en-tête de chaque fichier.
/// </summary>
public sealed class HidDriversProvider : IBatteryProvider
{
    public string Name => "Pilotes HID (marques)";

    /// <summary>Pilotes dans l'ordre d'exécution : les plus spécifiques d'abord.</summary>
    public static readonly (string Name, Action<PollContext> Poll)[] Drivers =
    {
        ("Glorious / Hitscan / G-Wolves / WLMouse", GloriousDriver.Poll),
        ("SteelSeries", SteelSeriesDriver.Poll),
        ("Razer BlackShark", RazerHeadsetDriver.Poll),
        ("Arbiter Studio", ArbiterDriver.Poll),
        ("LAMZU", LamzuDriver.Poll),
        ("PlayStation", PlayStationDriver.Poll),
        ("Corsair", CorsairDriver.Poll),
        ("HyperX", HyperXDriver.Poll),
        ("Kysona / Pulsar / ATK / VXE / Redragon / HECATE", KysonaDriver.Poll),
        ("ASTRO", AstroDriver.Poll),
        ("ASUS ROG", RogDriver.Poll),
        ("Jabra", JabraDriver.Poll),
        ("JBL Quantum", JblDriver.Poll),
        ("Cooler Master", CoolerMasterDriver.Poll),
        ("Alienware", AlienwareDriver.Poll),
        ("Audeze", AudezeDriver.Poll),
        ("Akko", AkkoDriver.Poll),
        ("AKG", AkgDriver.Poll),
        ("Razer Barracuda Pro", BarracudaProDriver.Poll),
        ("AJAZZ", AjazzDriver.Poll),
        ("CHERRY", CherryDriver.Poll),
        ("Endgame Gear", EndgameDriver.Poll),
        ("ENDORFY", EndorfyDriver.Poll),
        ("EWEADN", EweadnDriver.Poll),
        ("Fantech", FantechDriver.Poll),
        ("Zowie", ZowieDriver.Poll),
        ("Finalmouse", FinalmouseDriver.Poll),
        ("Flydigi", FlydigiDriver.Poll),
        ("Fractal Design", FractalDriver.Poll),
        ("GameSir", GameSirDriver.Poll),
        ("GameSir SAPJoy", GameSirSapjoyDriver.Poll),
        ("8BitDo", EightBitDoDriver.Poll),
        ("Keychron", KeychronDriver.Poll),
        ("MSI", MsiDriver.Poll),
        ("NuPhy", NuphyDriver.Poll),
        ("NZXT", NzxtDriver.Poll),
        ("Rapoo", RapooDriver.Poll),
        ("Areson / Redragon", AresonDriver.Poll),
        ("Chimera", ChimeraDriver.Poll),
        ("DARKRIM", DarkrimDriver.Poll),
        ("BEITONG", BeitongDriver.Poll),
        ("DAREU", DareuDriver.Poll),
        ("Machenike", MachenikeDriver.Poll),
        ("Darmoshark", DarmosharkDriver.Poll),
        ("MAONO", MaonoDriver.Poll),
        ("MOONDROP", MoondropDriver.Poll),
        ("Niceboy", NiceboyDriver.Poll),
        ("Ninjutso", NinjutsoDriver.Poll),
        ("Sprime", SprimeDriver.Poll),
        ("Fantech Raigor", RaigorDriver.Poll),
        ("Scyrox", ScyroxDriver.Poll),
        ("Semico", SemicoDriver.Poll),
        ("Sinowealth", SinowealthReceiverDriver.Poll),
        ("VAXEE", VaxeeDriver.Poll),
        ("VGN", VgnDriver.Poll),
        ("Zenith", ZenithDriver.Poll),
        ("Press Play", PressPlayDriver.Poll),
        ("Plantronics / Poly", PlantronicsDriver.Poll),
        ("Turtle Beach", TurtleBeachDriver.Poll),
        ("Sony INZONE", SonyInzoneDriver.Poll),
        ("Autres souris (Motospeed, Xenta, Delux, Ajazz, Incott, Wraith)", MiscMiceDriver.Poll),
        ("Pulsar", PulsarDriver.Poll),
        ("RAWM / SADES", RawmDriver.Poll),
        ("Logitech G435", LogitechG435Driver.Poll),
        ("Logitech HID++ (compléments)", LogitechExtraDriver.Poll),
    };

    /// <summary>Durée maximale cumulée d'une lecture : au-delà, les pilotes restants sont sautés jusqu'à la lecture suivante.</summary>
    const int BudgetMs = 15_000;

    public IReadOnlyList<BatteryReading> ReadAll()
    {
        var ctx = new PollContext(HidColl.Enumerate());
        if (ctx.Colls.Count == 0) return Array.Empty<BatteryReading>();
        var total = Stopwatch.StartNew();
        foreach (var (name, poll) in Drivers)
        {
            if (total.ElapsedMilliseconds > BudgetMs) break;
            try { poll(ctx); }
            catch (Exception e) { Diagnostics.Add($"{name}: {e.GetType().Name}"); }
        }
        return ToReadings(ctx.Out, DateTime.Now);
    }

    /// <summary>Erreurs des derniers pilotes (pour le diagnostic <c>--list</c>), sans donnée personnelle.</summary>
    public static List<string> Diagnostics { get; } = new();

    /// <summary>Convertit les résultats : seuls les appareils connectés qui ont un niveau sont affichés (aucune valeur inventée).</summary>
    public static List<BatteryReading> ToReadings(IEnumerable<DeviceStatus> statuses, DateTime now) =>
        statuses.Where(s => s.Connected && s.Percent is >= 0 and <= 100 && !string.IsNullOrWhiteSpace(s.Key))
            .Select(s => new BatteryReading("hid-" + s.Key, s.Name, s.Kind, s.Percent, s.Charging ? ChargeState.Charging : null,
                s.Detail ?? (string.IsNullOrEmpty(s.Via) ? null : s.Via), now))
            .ToList();
}
