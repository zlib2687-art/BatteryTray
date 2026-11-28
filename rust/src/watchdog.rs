//! Redémarrage automatique : si l'application s'arrête sans que l'utilisateur ait choisi « Quitter » (plantage,
//! processus tué), une tâche planifiée la relance au plus 10 minutes plus tard, sans aucune fenêtre.
//! Portage de Watchdog.cs. Aucun droit administrateur.

use crate::state::data_dir;
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};

pub const TASK: &str = "BatteryTray Rust surveillance";
const SCRIPT: &str = "surveiller.vbs";
const FLAG: &str = "arret-voulu.flag";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// « Quitter » : la tâche ne doit pas relancer.
pub fn mark_intentional_exit() {
    let dir = data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let _ = std::fs::write(dir.join(FLAG), "quitter");
}

/// L'application tourne : un futur arrêt sans « Quitter » sera inattendu.
pub fn clear_intentional_exit() {
    let _ = std::fs::remove_file(data_dir().join(FLAG));
}

/// Script VBScript : ne fait rien si tout va bien. « Démarrer avec Windows » décoché = pas de relance non plus.
fn script() -> String {
    [
        "' BatteryTray : relance le programme s'il s'est arrete de facon inattendue (tache planifiee, sans fenetre).",
        "Set sh = CreateObject(\"WScript.Shell\")",
        "Set fso = CreateObject(\"Scripting.FileSystemObject\")",
        "dossier = fso.GetParentFolderName(WScript.ScriptFullName)",
        "donnees = sh.ExpandEnvironmentStrings(\"%APPDATA%\") & \"\\BatteryTrayRust\"",
        "If fso.FileExists(donnees & \"\\arret-voulu.flag\") Then WScript.Quit 0",
        "On Error Resume Next",
        "demarrage = sh.RegRead(\"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\BatteryTrayRust\")",
        "If Err.Number <> 0 Then WScript.Quit 0",
        "On Error GoTo 0",
        "Set wmi = GetObject(\"winmgmts:\\\\.\\root\\cimv2\")",
        "Set procs = wmi.ExecQuery(\"Select ProcessId From Win32_Process Where Name = 'BatteryTray.exe'\")",
        "If procs.Count > 0 Then WScript.Quit 0",
        "exe = dossier & \"\\BatteryTray.exe\"",
        "If Not fso.FileExists(exe) Then WScript.Quit 0",
        "If Not fso.FolderExists(donnees) Then fso.CreateFolder(donnees)",
        "Set f = fso.OpenTextFile(donnees & \"\\redemarrages.txt\", 8, True)",
        "f.WriteLine Now & \" relance apres un arret inattendu\"",
        "f.Close",
        // style 1 (normal) et non 0 (masqué) : sinon la fenêtre des paramètres ne s'ouvrirait plus
        "sh.Run \"\"\"\" & exe & \"\"\"\", 1, False",
        "",
    ]
    .join("\r\n")
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

/// Toutes les 10 min, pendant la session, même sur batterie.
fn task_xml(wscript: &str, script_path: &str) -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Relance BatteryTray s'il s'arrete de facon inattendue.</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition><Interval>PT10M</Interval></Repetition>
      <StartBoundary>2026-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Hidden>true</Hidden>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{}</Command>
      <Arguments>//B //nologo "{}"</Arguments>
    </Exec>
  </Actions>
</Task>"#,
        escape(wscript),
        escape(script_path)
    )
}

/// Outil système appelé par son chemin complet, sans console.
fn system32(name: &str) -> String {
    format!(r"{}\System32\{name}", std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into()))
}

fn schtasks(args: &[&str]) -> bool {
    Command::new(system32("schtasks.exe"))
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .is_ok_and(|s| s.success())
}

/// Écrit le script à côté du programme et enregistre (ou remet à jour) la tâche.
pub fn register(install_dir: &Path) -> bool {
    let script_path = install_dir.join(SCRIPT);
    if std::fs::write(&script_path, script()).is_err() {
        return false;
    }
    let xml = task_xml(&system32("wscript.exe"), &script_path.to_string_lossy());
    let mut bytes = vec![0xFF, 0xFE];
    bytes.extend(xml.encode_utf16().flat_map(u16::to_le_bytes));
    // nom propre à ce processus, écrit dans le dossier d'installation
    let file = install_dir.join(format!("tache-{}.xml", std::process::id()));
    if std::fs::write(&file, bytes).is_err() {
        return false;
    }
    let done = schtasks(&["/Create", "/TN", TASK, "/XML", &file.to_string_lossy(), "/F"]);
    let _ = std::fs::remove_file(file);
    done
}

pub fn unregister() {
    schtasks(&["/Delete", "/TN", TASK, "/F"]);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn script_respects_quit_and_startup_choice() {
        let s = script();
        let flag = s.find("arret-voulu.flag").unwrap();
        let startup = s.find("CurrentVersion\\Run\\BatteryTrayRust").unwrap();
        let launch = s.find("sh.Run").unwrap();
        assert!(flag < launch && startup < launch);
        assert!(s.ends_with("1, False\r\n"));
    }

    #[test]
    fn task_is_windowless_and_escaped() {
        let xml = task_xml(r"C:\Windows\System32\wscript.exe", r"C:\a & b\surveiller.vbs");
        assert!(xml.contains("//B //nologo") && xml.contains("PT10M") && xml.contains("a &amp; b"));
    }
}
