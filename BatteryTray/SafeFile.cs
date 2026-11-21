namespace BatteryTray;

/// <summary>
/// Écriture « tout ou rien » : le texte est écrit dans un fichier voisin puis déplacé d'un coup sur la destination.
/// Sans cela, un arrêt brutal pendant l'écriture (mise à jour, coupure de courant, processus tué) laissait un fichier
/// de réglages ou d'historique tronqué, et tout repartait de zéro au démarrage suivant.
/// </summary>
static class SafeFile
{
    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
