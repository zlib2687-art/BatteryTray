namespace BatteryTray;

// Microphone est ajouté À LA FIN : les valeurs numériques sont enregistrées dans les réglages.
public enum DeviceKind { Mouse, Headset, Keyboard, Gamepad, Webcam, Other, Microphone }

/// <summary>Codes d'état de charge, indépendants de la langue (le texte affiché vient de Loc).</summary>
public static class ChargeState
{
    public const string Charging = "charging";
    public const string Full = "full";
}

/// <summary>
/// Une lecture de batterie. Percent est null quand l'appareil ne fournit pas de pourcentage :
/// on n'estime jamais une valeur lue.
/// </summary>
public sealed record BatteryReading(
    string Id,
    string Name,
    DeviceKind Kind,
    int? Percent,
    string? Charge,
    string? Detail,
    DateTime At)
{
    /// <summary>
    /// Autonomie estimée en heures (vitesse de décharge observée). Null en charge, ou tant que
    /// l'estimation n'est pas possible (voir <see cref="AutonomyPending"/>). C'est une estimation,
    /// jamais une valeur lue sur l'appareil.
    /// </summary>
    public double? AutonomyHours { get; init; }
    public bool AutonomyPending { get; init; }

    /// <summary>
    /// Vrai quand <see cref="AutonomyHours"/> est une borne basse (« plus de … ») : le niveau n'a pas encore
    /// baissé d'un point, donc la vitesse de décharge est forcément inférieure à 1 point par durée observée.
    /// </summary>
    public bool AutonomyLowerBound { get; init; }

    /// <summary>Vrai quand la durée vient de l'autonomie annoncée saisie par l'utilisateur (pas d'une mesure).</summary>
    public bool AutonomyFromRating { get; init; }

    public bool IsCharging => Charge == ChargeState.Charging;

    /// <summary>Branché : en charge ou chargé (câble présent). Sert à colorer en vert.</summary>
    public bool IsPlugged => Charge is ChargeState.Charging or ChargeState.Full;

    /// <summary>
    /// Vrai quand <see cref="Percent"/> n'est PAS lu sur l'appareil mais estimé d'après sa tension
    /// (voir <see cref="VoltageCurve"/>). Toujours affiché avec « ~ ».
    /// </summary>
    public bool PercentEstimated { get; init; }

    /// <summary>« 73 % », « ~99 % » (estimé d'après la tension) ou « — » (aucun niveau).</summary>
    public string PercentText => Percent is int p ? $"{(PercentEstimated ? "~" : "")}{p} %" : "—";

    /// <summary>
    /// Appareil actuellement injoignable : <see cref="Percent"/> est sa DERNIÈRE valeur connue et
    /// <see cref="At"/> l'heure de cette lecture (affiché en gris).
    /// </summary>
    public bool Offline { get; init; }

    /// <summary>
    /// Temps estimé avant charge complète (appareil en charge). <see cref="ChargeEtaPending"/> tant que la
    /// vitesse de charge n'est pas connue. Estimation d'après la vitesse de charge observée.
    /// </summary>
    public double? ChargeEtaHours { get; init; }
    public bool ChargeEtaPending { get; init; }

    /// <summary>Batteries séparées (écouteurs gauche / droit…) quand l'appareil en expose plusieurs ; sinon null.</summary>
    public IReadOnlyList<BatteryPart>? Parts { get; init; }
}

/// <summary>Une batterie d'un appareil à plusieurs batteries. Label : voir <see cref="BatteryParts"/>.</summary>
public sealed record BatteryPart(string Label, int Percent);

public interface IBatteryProvider
{
    string Name { get; }
    IReadOnlyList<BatteryReading> ReadAll();
}
