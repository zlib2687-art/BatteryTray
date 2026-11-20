using System.Diagnostics;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// Installateur intégré : le même fichier sert d'installateur (<c>BatteryTray-Setup.exe</c>) et de programme.
/// Installation pour l'utilisateur courant uniquement, sans droits administrateur :
///  - copie dans %LocalAppData%\Programs\BatteryTray (dossier stable) ;
///  - démarrage automatique (clé Run de l'utilisateur) ;
///  - raccourci dans le menu Démarrer ;
///  - entrée « Applications installées » avec désinstallation.
/// Désinstallation : <c>BatteryTray.exe --uninstall</c> (depuis Paramètres → Applications).
/// L'option <c>/silent</c> supprime les questions.
/// </summary>
static class Installer
{
    public const string SetupFileName = "BatteryTray-Setup.exe";
    const string AppName = "BatteryTray";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BatteryTray";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    static string TargetExe => Path.Combine(InstallDir, "BatteryTray.exe");
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    /// <summary>Vrai si ce programme est l'exemplaire installé (et non une copie lancée ailleurs, par exemple un test).</summary>
    public static bool RunsFromInstallDir() =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(TargetExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsSetupFile() =>
        string.Equals(Path.GetFileName(Environment.ProcessPath), SetupFileName, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ installation
    public static int Install(bool silent)
    {
        if (!silent && MessageBox.Show(Loc.T("install.ask", InstallDir), Loc.T("install.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        try
        {
            StopRunningInstances();
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, TargetExe, overwrite: true);

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.SetValue(AppName, $"\"{TargetExe}\"");

            CreateShortcut(ShortcutPath, TargetExe);
            Watchdog.Register(InstallDir); // relance automatique après un arrêt inattendu (facultatif : un échec n'empêche pas l'installation)

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("DisplayIcon", TargetExe);
                k.SetValue("UninstallString", $"\"{TargetExe}\" --uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(TargetExe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            Process.Start(new ProcessStartInfo(TargetExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            if (!silent)
                MessageBox.Show(Loc.T("install.done"), Loc.T("install.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception e)
        {
            if (!silent)
                MessageBox.Show(Loc.T("install.error", e.Message), Loc.T("install.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    // ------------------------------------------------------------------ désinstallation
    public static int Uninstall(bool silent)
    {
        if (!silent && MessageBox.Show(Loc.T("uninstall.ask"), Loc.T("install.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        bool wipeData = !silent && MessageBox.Show(Loc.T("uninstall.data"), Loc.T("install.title"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        try
        {
            Watchdog.Unregister(); // d'abord : sinon la tâche relancerait le programme qu'on arrête
            StopRunningInstances();

            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                run?.DeleteValue(AppName, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            try { File.Delete(ShortcutPath); } catch { }
            if (wipeData)
                try { Directory.Delete(AppSettings.DataDir, recursive: true); } catch { }

            // Message AVANT le nettoyage : le programme tourne tant que la boîte est ouverte, et un dossier dont l'exécutable
            // est en cours d'utilisation ne peut pas être supprimé (avant, le nettoyage partait trop tôt et laissait 163 Mo).
            if (!silent)
                MessageBox.Show(Loc.T("uninstall.done"), Loc.T("install.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);

            // le programme en cours d'exécution ne peut pas se supprimer lui-même : un cmd caché le fait après sa sortie,
            // avec une seconde tentative si le premier essai tombe pendant que le fichier est encore verrouillé
            string dir = InstallDir;
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\" 2>nul & if exist \"{dir}\" (ping 127.0.0.1 -n 6 >nul & rmdir /s /q \"{dir}\" 2>nul)")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return 0;
        }
        catch (Exception e)
        {
            if (!silent)
                MessageBox.Show(e.Message, Loc.T("install.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    // ------------------------------------------------------------------ démarrage avec Windows
    public static bool IsStartupEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(AppName) is string;
    }

    /// <summary>Active ou retire le démarrage automatique (clé Run de l'utilisateur, sans droits administrateur).</summary>
    public static void SetStartup(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) run.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else run.DeleteValue(AppName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ utilitaires
    static void StopRunningInstances()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppName))
        {
            if (p.Id == me) continue;
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return; // raccourci facultatif
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.Description = AppName;
        lnk.Save();
    }
}
