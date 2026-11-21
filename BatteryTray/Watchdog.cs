using System.Diagnostics;
using System.Security;
using System.Text;

namespace BatteryTray;

/// <summary>
/// Redémarrage automatique : si l'application s'arrête sans que l'utilisateur ait choisi « Quitter » (arrêt forcé par
/// une mise à jour, plantage, processus tué), une tâche planifiée la relance au plus 10 minutes plus tard.
///
/// Fonctionnement :
///  - en quittant par le menu, l'application écrit un fichier-témoin (<see cref="FlagPath"/>) : la tâche ne relance alors rien ;
///  - à chaque démarrage, l'application supprime ce témoin ;
///  - la tâche exécute <c>surveiller.vbs</c> (wscript, sans aucune fenêtre : l'utilisateur joue en plein écran) qui relance
///    le programme s'il ne tourne pas et si le témoin est absent, et note chaque relance dans <c>redemarrages.txt</c>.
/// Aucun droit administrateur : tâche de l'utilisateur courant, exécutée pendant sa session.
/// </summary>
static class Watchdog
{
    public const string TaskName = "BatteryTray surveillance";
    public const string ScriptName = "surveiller.vbs";
    public const int IntervalMinutes = 10;

    public static string FlagPath => Path.Combine(AppSettings.DataDir, "arret-voulu.flag");

    /// <summary>Appelé quand l'utilisateur quitte volontairement.</summary>
    public static void MarkIntentionalExit()
    {
        try { Directory.CreateDirectory(AppSettings.DataDir); File.WriteAllText(FlagPath, DateTime.Now.ToString("s")); } catch { }
    }

    /// <summary>Appelé au démarrage : l'application tourne, le témoin n'a plus lieu d'être.</summary>
    public static void ClearIntentionalExit()
    {
        try { File.Delete(FlagPath); } catch { }
    }

    /// <summary>Contenu du script de surveillance (VBScript). Il doit rester sans fenêtre et sans effet si tout va bien.</summary>
    public static string ScriptText() => string.Join("\r\n", new[]
    {
        "' BatteryTray : relance le programme s'il s'est arrete de facon inattendue (tache planifiee, sans fenetre).",
        "Set sh = CreateObject(\"WScript.Shell\")",
        "Set fso = CreateObject(\"Scripting.FileSystemObject\")",
        "dossier = fso.GetParentFolderName(WScript.ScriptFullName)",
        "donnees = sh.ExpandEnvironmentStrings(\"%APPDATA%\") & \"\\BatteryTray\"",
        "If fso.FileExists(donnees & \"\\arret-voulu.flag\") Then WScript.Quit 0",
        // « Démarrer avec Windows » désactivé dans les paramètres : l'utilisateur ne veut pas qu'elle se lance seule, on respecte ce choix
        "reglages = donnees & \"\\settings.json\"",
        "If fso.FileExists(reglages) Then",
        "  Set fr = fso.OpenTextFile(reglages, 1)",
        "  If Not fr.AtEndOfStream Then texte = fr.ReadAll Else texte = \"\"",
        "  fr.Close",
        "  If InStr(1, texte, \"\"\"StartWithWindows\"\": false\", 1) > 0 Then WScript.Quit 0",
        "End If",
        "Set wmi = GetObject(\"winmgmts:\\\\.\\root\\cimv2\")",
        "Set procs = wmi.ExecQuery(\"Select ProcessId From Win32_Process Where Name = 'BatteryTray.exe'\")",
        "If procs.Count > 0 Then WScript.Quit 0",
        "exe = dossier & \"\\BatteryTray.exe\"",
        "If Not fso.FileExists(exe) Then WScript.Quit 0",
        "If Not fso.FolderExists(donnees) Then fso.CreateFolder(donnees)",
        "Set f = fso.OpenTextFile(donnees & \"\\redemarrages.txt\", 8, True)",
        "f.WriteLine Year(Now) & \"-\" & Right(\"0\" & Month(Now), 2) & \"-\" & Right(\"0\" & Day(Now), 2) & \" \" & Right(\"0\" & Hour(Now), 2) & \":\" & Right(\"0\" & Minute(Now), 2) & \" relance apres un arret inattendu\"",
        "f.Close",
        // style 1 (normal) et NON 0 (masqué) : un programme lancé « masqué » transmet cet état à sa première fenêtre,
        // et la fenêtre des paramètres ne s'ouvrait plus. L'application n'a aucune fenêtre au démarrage : rien ne s'affiche.
        "sh.Run \"\"\"\" & exe & \"\"\"\", 1, False",
        "",
    });

    /// <summary>Définition de la tâche (XML) : toutes les 10 min, pendant la session, même sur batterie, sans fenêtre.</summary>
    public static string TaskXml(string scriptPath)
    {
        string wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        string start = DateTime.Now.AddMinutes(1).ToString("s");
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Relance BatteryTray s'il s'arrete de facon inattendue.</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition><Interval>PT{IntervalMinutes}M</Interval></Repetition>
      <StartBoundary>{start}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals><Principal id=""Author""><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Hidden>true</Hidden>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(wscript)}</Command>
      <Arguments>//B //nologo ""{SecurityElement.Escape(scriptPath)}""</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    /// <summary>Écrit le script à côté du programme et enregistre la tâche. Renvoie false si l'enregistrement échoue (facultatif).</summary>
    public static bool Register(string installDir)
    {
        try
        {
            string script = Path.Combine(installDir, ScriptName);
            File.WriteAllText(script, ScriptText(), Encoding.Default);

            // nom imprévisible : un autre programme ne peut pas deviner le fichier et le remplacer avant que schtasks le lise
            string xml = Path.Combine(Path.GetTempPath(), "batterytray-tache-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(xml, TaskXml(script), Encoding.Unicode);
            try { return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F") == 0; }
            finally { try { File.Delete(xml); } catch { } }
        }
        catch { return false; }
    }

    public static void Unregister()
    {
        try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); } catch { }
    }

    static int RunSchtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }
}
