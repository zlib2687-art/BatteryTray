//! BatteryTray en Rust : batterie de trois appareils dans la zone de notification, panneau et paramètres.
//! Portage de BatteryTray (C#), même licence GPL-3.0. Aucun accès réseau.
#![cfg_attr(not(test), windows_subsystem = "windows")]

mod app;
mod autonomy;
mod devices;
mod flyout;
mod gfx;
mod hid;
mod icon;
mod panel;
mod settings;
mod state;
mod system;
mod watchdog;
mod windows;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let silent = args.iter().any(|a| a == "/silent");
    let value = |name: &str| args.iter().position(|a| a == name).and_then(|i| args.get(i + 1));

    if let Some(path) = value("--list") {
        system::list(path);
        return;
    }
    // « --preview dossier » : dessine le panneau et les paramètres avec des données d'exemple
    if let Some(folder) = value("--preview") {
        windows::previews(folder);
        return;
    }
    if args.iter().any(|a| a == "--uninstall") {
        system::uninstall(silent);
        return;
    }
    if system::is_setup() || args.iter().any(|a| a == "--install") {
        system::install(silent);
        return;
    }
    app::run();
}
