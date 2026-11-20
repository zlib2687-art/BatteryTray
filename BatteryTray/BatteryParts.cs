namespace BatteryTray;

/// <summary>
/// Appareils à plusieurs batteries (écouteurs : gauche, droit…). Le service Batterie du Bluetooth LE peut
/// exister en plusieurs exemplaires ; chaque caractéristique peut porter un descripteur « Presentation Format »
/// (0x2904) dont le champ « description » (Bluetooth SIG Assigned Numbers, espace de noms 1) indique laquelle :
/// 0x000E = gauche, 0x000F = droite. Tout autre code (ou aucun descripteur) = « autre ».
/// </summary>
public static class BatteryParts
{
    public const string Left = "left", Right = "right", Other = "other";

    public static string LabelFromDescription(ushort description) => description switch
    {
        0x000E => Left,
        0x000F => Right,
        _ => Other,
    };

    /// <summary>
    /// Caractéristique Bluetooth LE « Battery Power State » (0x2A1A) : les bits 4-5 donnent l'état de charge
    /// (0 inconnu, 1 non rechargeable, 2 ne charge pas, 3 EN CHARGE). Retourne « charging » ou null.
    /// </summary>
    public static string? ChargeFromPowerState(byte powerState) => ((powerState >> 4) & 0b11) == 3 ? ChargeState.Charging : null;

    /// <summary>
    /// Niveau à retenir pour l'icône et les alertes : le plus bas des écouteurs (gauche/droit) s'ils sont identifiés,
    /// sinon le plus bas de toutes les batteries. Un boîtier plein ne cache donc pas un écouteur presque vide.
    /// </summary>
    public static int? Aggregate(IReadOnlyList<BatteryPart> parts)
    {
        if (parts.Count == 0) return null;
        var buds = parts.Where(p => p.Label is Left or Right).ToList();
        return (buds.Count > 0 ? buds : parts.ToList()).Min(p => p.Percent);
    }
}
