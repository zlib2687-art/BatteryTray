using System.Text.Json;

namespace BatteryTray;

/// <summary>Préférences d'un appareil (identifié par son Id).</summary>
public sealed class DevicePref
{
    /// <summary>Masqué : pas d'icône dans la barre ni de ligne dans le flyout.</summary>
    public bool Hidden { get; set; }
    /// <summary>
    /// Vrai si l'appareil a été masqué automatiquement (aucun pourcentage à la première lecture). Il est
    /// réaffiché tout seul dès qu'un pourcentage existe ; un masquage choisi par l'utilisateur n'est jamais défait.
    /// </summary>
    public bool AutoHidden { get; set; }
    /// <summary>Nom choisi par l'utilisateur ; vide = nom d'origine.</summary>
    public string? Name { get; set; }
    /// <summary>Seuil de batterie faible propre à cet appareil (%) ; vide = seuil par défaut.</summary>
    public int? LowThreshold { get; set; }
    /// <summary>Autonomie annoncée (heures), facultative : sert d'estimation tant qu'aucune mesure n'est possible.</summary>
    public double? RatedHours { get; set; }
}

/// <summary>Appareil déjà vu : permet de le garder dans les paramètres même quand il est déconnecté.</summary>
public sealed class KnownDevice
{
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    /// <summary>Dernière valeur lue, gardée pour l'afficher en gris quand l'appareil est hors ligne.</summary>
    public int? LastPercent { get; set; }
    public bool LastEstimated { get; set; }
    public DateTime? LastSeen { get; set; }
}

/// <summary>Réglages de l'utilisateur, stockés localement dans %AppData%\BatteryTray\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Code de langue (« fr », « ar-TN », « en »…) ; vide = langue de Windows.</summary>
    public string Language { get; set; } = "";
    public bool NotifyLow { get; set; } = true;
    public bool NotifyFull { get; set; } = true;
    /// <summary>Seuil de batterie faible par défaut (%).</summary>
    public int LowThreshold { get; set; } = 20;
    /// <summary>Le pourcentage passe en orange sous ce seuil, en rouge à partir de <see cref="RedBelow"/>.</summary>
    public int OrangeBelow { get; set; } = 40;
    public int RedBelow { get; set; } = 15;
    /// <summary>Intervalle d'actualisation en minutes.</summary>
    public int PollMinutes { get; set; } = 2;
    /// <summary>Taille du flyout, en % (85, 100, 125 ou 150) ; 100 = la taille standard.</summary>
    public int FlyoutScale { get; set; } = 100;

    public Dictionary<string, DevicePref> Devices { get; set; } = new();
    public Dictionary<string, KnownDevice> Known { get; set; } = new();
    /// <summary>Ordre d'affichage (Ids des appareils).</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>Appareils déjà signalés « faible » / « chargé » : évite de répéter la notification.</summary>
    public HashSet<string> LowNotified { get; set; } = new();
    public HashSet<string> FullNotified { get; set; } = new();

    /// <summary>
    /// Choix « Démarrer avec Windows » (vrai par défaut). Mémorisé ici et pas seulement dans la clé Run : l'application
    /// recrée la clé au démarrage si elle manque (désinstallation partielle, clé effacée), et le script de surveillance
    /// lit ce réglage pour savoir s'il a le droit de la relancer.
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    public static AppSettings Current { get; set; } = new();

    /// <summary>Dossier des données (réglages, historique, journal). Modifiable pour les tests.</summary>
    public static string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryTray");

    public static string FilePath => Path.Combine(DataDir, "settings.json");

    public DevicePref PrefFor(string id) => Devices.TryGetValue(id, out var p) ? p : new DevicePref();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                loaded.Normalize();
                return loaded;
            }
        }
        catch { /* fichier absent ou corrompu : réglages par défaut */ }
        return new();
    }

    /// <summary>
    /// Rend valide un fichier de réglages modifié à la main ou abîmé : listes « null » remplacées, valeurs absurdes ramenées
    /// dans leurs bornes (un intervalle de 100 000 minutes faisait planter la minuterie au démarrage).
    /// </summary>
    public void Normalize()
    {
        Devices ??= new();
        Known ??= new();
        Order ??= new();
        LowNotified ??= new();
        FullNotified ??= new();
        foreach (var key in Devices.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Devices.Remove(key);
        foreach (var key in Known.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Known.Remove(key);
        Language ??= "";

        PollMinutes = Math.Clamp(PollMinutes, 1, 60);
        LowThreshold = Math.Clamp(LowThreshold, 1, 99);
        OrangeBelow = Math.Clamp(OrangeBelow, 2, 100);
        RedBelow = Math.Clamp(RedBelow, 1, 99);
        if (RedBelow >= OrangeBelow) RedBelow = OrangeBelow - 1;
        FlyoutScale = Math.Clamp(FlyoutScale, 70, 200);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* les réglages ne doivent jamais faire planter l'application */ }
    }
}
