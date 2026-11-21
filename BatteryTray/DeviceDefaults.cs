namespace BatteryTray;

/// <summary>
/// Autonomies de référence de modèles connus, utilisées UNIQUEMENT tant qu'aucune mesure n'existe pour l'appareil
/// (affichées avec « (annoncée) ») ; la mesure les remplace dès qu'elle est possible. Une valeur saisie par
/// l'utilisateur dans les paramètres est prioritaire.
/// </summary>
public static class DeviceDefaults
{
    // (fragment du nom, heures, source)
    static readonly (string Fragment, double Hours)[] Rated =
    {
        // Logitech PRO X 2 LIGHTSPEED : 50 h (spécifications Logitech). G HUB affichait « ~37 h » à 73 % (= 73 % × 50 h)
        // et la mesure sur le PC donnait ≈ 51 h pour une batterie pleine.
        ("PRO X 2", 50),

        // Logitech G502 LIGHTSPEED : G HUB affichait « ~25 heures restantes » à 100 % sur ce PC (le fabricant annonce 48 à 60 h
        // sur le papier ; 25 h correspond à l'usage réel selon G HUB). Valeur de départ, remplacée par la mesure.
        ("G502 LIGHTSPEED", 25),

        // Razer, autonomies annoncées par le fabricant (à confirmer : la mesure les remplace) : point de départ tant qu'aucune mesure n'existe.
        ("BlackShark V2 Pro", 70),
        ("Viper V3 Pro", 95),
        ("DeathAdder V3 Pro", 90),    };

    public static double? RatedHoursFor(string deviceName)
    {
        foreach (var (fragment, hours) in Rated)
            if (deviceName.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return hours;
        return null;
    }
}
