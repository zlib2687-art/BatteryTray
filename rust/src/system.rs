//! Registre, installation, heure locale et diagnostics.

use crate::i18n::{self, t, tf};
use std::path::PathBuf;
use std::ptr::{null, null_mut};
use std::time::Duration;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::System::Registry::*;
use windows_sys::Win32::System::Threading::GetCurrentProcess;
use windows_sys::Win32::System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

pub const APP: &str = "BatteryTrayRust";
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

#[link(name = "kernel32")]
extern "system" {
    fn SetProcessWorkingSetSize(process: HANDLE, minimum: usize, maximum: usize) -> i32;
}

/// Rend à Windows la mémoire inutilisée.
pub fn trim() {
    unsafe { SetProcessWorkingSetSize(GetCurrentProcess(), usize::MAX, usize::MAX) };
}

pub fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

/// Thème clair demandé ? `value` : « SystemUsesLightTheme » (barre) ou « AppsUseLightTheme » (fenêtres).
pub fn light_theme(value: &str) -> bool {
    let key = wide(r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
    let value = wide(value);
    let (mut data, mut size) = (0u32, 4u32);
    let status = unsafe {
        RegGetValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), RRF_RT_REG_DWORD, null_mut(), (&mut data as *mut u32).cast(), &mut size)
    };
    status == ERROR_SUCCESS && data == 1
}

/// Heure locale « 14:05 » d'un instant en secondes depuis 1970.
pub fn clock(unix: u64) -> String {
    let ticks = (unix + 11_644_473_600) * 10_000_000;
    let file_time = FILETIME { dwLowDateTime: ticks as u32, dwHighDateTime: (ticks >> 32) as u32 };
    unsafe {
        let mut utc: SYSTEMTIME = std::mem::zeroed();
        let mut local: SYSTEMTIME = std::mem::zeroed();
        if FileTimeToSystemTime(&file_time, &mut utc) == 0 || SystemTimeToTzSpecificLocalTime(null(), &utc, &mut local) == 0 {
            return String::new();
        }
        format!("{:02}:{:02}", local.wHour, local.wMinute)
    }
}

pub fn startup_enabled() -> bool {
    let (key, value) = (wide(RUN_KEY), wide(APP));
    unsafe { RegGetValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), RRF_RT_REG_SZ, null_mut(), null_mut(), null_mut()) == ERROR_SUCCESS }
}

pub fn set_startup(exe: Option<&PathBuf>) -> bool {
    let (key, value) = (wide(RUN_KEY), wide(APP));
    unsafe {
        match exe {
            Some(path) => {
                let data = wide(&format!("\"{}\"", path.display()));
                RegSetKeyValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), REG_SZ, data.as_ptr().cast(), (data.len() * 2) as u32) == ERROR_SUCCESS
            }
            None => RegDeleteKeyValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr()) == ERROR_SUCCESS,
        }
    }
}

fn install_path() -> PathBuf {
    PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join(r"Programs\BatteryTrayRust\BatteryTray.exe")
}

fn message(text: &str) {
    let (text, title) = (wide(text), wide("BatteryTray"));
    unsafe { MessageBoxW(null_mut(), text.as_ptr(), title.as_ptr(), MB_OK) };
}

/// Le programme lancé s'appelle « …Setup.exe » : c'est l'installateur.
pub fn is_setup() -> bool {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.file_stem().map(|s| s.to_string_lossy().to_lowercase().ends_with("setup")))
        .unwrap_or(false)
}

/// Dossier d'installation, si le programme en cours est bien l'exemplaire installé.
pub fn installed_dir() -> Option<PathBuf> {
    let current = std::env::current_exe().ok()?;
    let target = install_path();
    current.to_string_lossy().eq_ignore_ascii_case(&target.to_string_lossy()).then(|| target.parent().map(PathBuf::from)).flatten()
}

pub fn install(silent: bool) {
    i18n::set(&crate::settings::Settings::parse(&crate::settings::read("reglages.tsv")).language);
    let target = install_path();
    let done = std::env::current_exe().is_ok_and(|source| {
        // une version déjà lancée verrouille son fichier : on lui demande de se fermer
        source == target
            || (close_running()
                && target.parent().is_some_and(|dir| std::fs::create_dir_all(dir).is_ok())
                && copy_with_retry(&source, &target))
    }) && set_startup(Some(&target));
    if done {
        let _ = std::process::Command::new(&target).spawn();
    }
    if !silent {
        message(&if done { t("install.done").to_string() } else { tf("install.error", &["BatteryTray.exe"]) });
    }
}

pub fn uninstall(silent: bool) {
    set_startup(None);
    crate::watchdog::unregister();
    if !silent {
        i18n::set("");
        message(t("uninstall.done"));
    }
}

/// Ferme proprement la version en cours, s'il y en a une.
fn close_running() -> bool {
    let class = wide(APP);
    let window = unsafe { FindWindowW(class.as_ptr(), null()) };
    window.is_null() || unsafe { PostMessageW(window, WM_CLOSE, 0, 0) } != 0
}

/// Le fichier reste verrouillé un court instant après la fermeture : jusqu'à 5 s d'essais.
fn copy_with_retry(source: &PathBuf, target: &PathBuf) -> bool {
    (0..25).any(|_| {
        std::fs::copy(source, target).is_ok() || {
            std::thread::sleep(Duration::from_millis(200));
            false
        }
    })
}

/// « --list fichier » : une lecture de chaque appareil, pour comparer avec les logiciels des fabricants.
pub fn list(path: &str) {
    let mut reader = crate::devices::Reader::default();
    let mut text = String::new();
    for r in reader.read_all() {
        text += &format!(
            "{}\t{}\t{}{} %\t{}\t{}\n",
            r.id,
            r.name,
            if r.estimated { "~" } else { "" },
            r.percent,
            if r.charging { "en charge" } else { "sur batterie" },
            r.pointer.text()
        );
    }
    let _ = std::fs::write(path, text);
}

/// Vrai si le clavier ou la souris ont servi il y a moins de `millis`.
pub fn used_within(millis: u32) -> bool {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};
    let mut last = LASTINPUTINFO { cbSize: size_of::<LASTINPUTINFO>() as u32, dwTime: 0 };
    unsafe { GetLastInputInfo(&mut last) != 0 && windows_sys::Win32::System::SystemInformation::GetTickCount().wrapping_sub(last.dwTime) < millis }
}
