//! État de l'application, icônes de la barre et boucle de messages.

use crate::autonomy::{self, Estimate, Estimator};
use crate::devices::{Reader, Reading};
use crate::settings::{self, Settings};
use crate::state::{self, Alert, Device, State};
use crate::system::{self, wide, APP};
use crate::{icon, watchdog, windows};
use std::cell::RefCell;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::Mutex;
use std::time::Duration;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::CreateMutexW;
use windows_sys::Win32::UI::HiDpi::{SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2};
use windows_sys::Win32::UI::Shell::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const WM_TRAY: u32 = WM_APP + 1;
const WM_READINGS: u32 = WM_APP + 2;
const MENU_REFRESH: usize = 1;
const MENU_STARTUP: usize = 2;
const MENU_QUIT: usize = 3;
const MENU_SETTINGS: usize = 4;
const PLACEHOLDER: usize = 999;

/// Lecture transmise par le fil d'arrière-plan au fil d'interface.
static PENDING: Mutex<Option<Vec<Reading>>> = Mutex::new(None);
/// Intervalle entre deux lectures, en secondes.
static INTERVAL: AtomicU64 = AtomicU64::new(120);

pub struct App {
    pub window: HWND,
    pub state: State,
    pub settings: Settings,
    pub estimator: Estimator,
    /// Identifiants des icônes déjà créées ; la position sert d'identifiant Windows.
    icons: Vec<String>,
    wake: Sender<()>,
    taskbar_created: u32,
    saved_estimates: u64,
    /// Le relâchement qui suit un double-clic est ignoré.
    ignore_up: bool,
}

thread_local!(static CURRENT: RefCell<Option<App>> = const { RefCell::new(None) });

/// Accès à l'état ; ignoré (None) si un appel est déjà en cours.
pub fn with<T>(action: impl FnOnce(&mut App) -> T) -> Option<T> {
    CURRENT.with(|cell| cell.try_borrow_mut().ok()?.as_mut().map(action))
}

fn copy_to(target: &mut [u16], text: &str) {
    let mut n = 0;
    for c in text.encode_utf16().take(target.len() - 1) {
        target[n] = c;
        n += 1;
    }
    target[n] = 0;
}

fn base_data(window: HWND, id: usize) -> NOTIFYICONDATAW {
    let mut data: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
    data.cbSize = size_of::<NOTIFYICONDATAW>() as u32;
    data.hWnd = window;
    data.uID = id as u32 + 1;
    data
}

fn rgb_of_brand(name: &str, light: bool) -> Option<u32> {
    let lower = name.to_lowercase();
    let color: (u32, u32, u32) = if ["razer", "deathadder", "viper", "blackshark"].iter().any(|k| lower.contains(k)) {
        (68, 214, 44)
    } else if ["logitech", "g502", "pro x", "lightspeed"].iter().any(|k| lower.contains(k)) {
        (0, 184, 252)
    } else {
        return None;
    };
    // teinte plus foncée sur fond clair
    let shade = |v: u32| if light { v * 62 / 100 } else { v };
    Some(0xFF00_0000 | shade(color.0) << 16 | shade(color.1) << 8 | shade(color.2))
}

impl App {
    /// Appareils affichés (non masqués), dans l'ordre choisi.
    pub fn visible(&self) -> Vec<&Device> {
        let mut list: Vec<&Device> = self.state.devices.iter().filter(|d| !self.settings.pref(&d.reading.id).hidden).collect();
        list.sort_by_key(|d| self.settings.devices.iter().position(|p| p.id == d.reading.id).unwrap_or(usize::MAX));
        list
    }

    pub fn name_of(&self, d: &Device) -> String {
        let name = self.settings.pref(&d.reading.id).name;
        if name.trim().is_empty() { d.reading.name.clone() } else { name.trim().to_string() }
    }

    /// Couleur d'état : gris hors ligne, vert en charge, rouge ou orange si faible, sinon marque ou accent.
    pub fn status_color(&self, d: &Device, light: bool) -> u32 {
        let t = crate::gfx::theme(light);
        let r = &d.reading;
        if !d.online {
            t.dim
        } else if r.charging {
            t.green
        } else if r.percent <= self.settings.red_below {
            0xFFE5_3935
        } else if r.percent < self.settings.orange_below {
            0xFFF5_8A00
        } else {
            self.settings.brand_colors.then(|| rgb_of_brand(&r.name, light)).flatten().unwrap_or(t.accent)
        }
    }

    /// Deuxième ligne : hors ligne, charge et temps restant, ou autonomie, avec l'heure de lecture.
    pub fn second_line(&self, d: &Device) -> String {
        let time = system::clock(d.seen);
        if !d.online {
            return format!("hors ligne · {time}");
        }
        if d.reading.charging {
            return match d.estimate {
                Estimate::Full(_) => format!("en charge · {}", autonomy::describe(d.estimate).unwrap_or_default()),
                _ => format!("en charge · {time}"),
            };
        }
        match autonomy::describe(d.estimate) {
            Some(text) => format!("{text} · {time}"),
            None => format!("lu à {time}"),
        }
    }

    pub fn refresh_icons(&mut self) {
        let window = self.window;
        let light = system::light_theme("SystemUsesLightTheme");
        let shown: Vec<(String, devices_kind::Kind, u8, u32, String)> = self
            .visible()
            .iter()
            .map(|d| {
                let r = &d.reading;
                let color = if !d.online {
                    0x8A8A8A
                } else if r.charging {
                    if light { 0x0F7B0F } else { 0x4CD964 }
                } else if r.percent <= self.settings.red_below {
                    0xFF4540
                } else if r.percent < self.settings.orange_below {
                    0xFF9500
                } else if light {
                    0x000000
                } else {
                    0xFFFFFF
                };
                let mark = if r.estimated { "~" } else { "" };
                let tip = format!("{}\n{mark}{} %\n{}", self.name_of(d), r.percent, self.second_line(d));
                (r.id.clone(), r.kind, r.percent, color, tip)
            })
            .collect();

        // retire les icônes des appareils masqués ou oubliés
        for (i, id) in self.icons.iter().enumerate() {
            if !shown.iter().any(|s| &s.0 == id) {
                unsafe { Shell_NotifyIconW(NIM_DELETE, &base_data(window, i)) };
            }
        }
        for (id, kind, percent, color, tip) in &shown {
            let index = match self.icons.iter().position(|i| i == id) {
                Some(i) => i,
                None => {
                    self.icons.push(id.clone());
                    self.icons.len() - 1
                }
            };
            set_icon(window, index, *kind, *percent, *color, tip);
        }
        // sans aucun appareil, une icône reste nécessaire pour atteindre les paramètres et quitter
        if shown.is_empty() {
            set_icon(window, PLACEHOLDER, devices_kind::Kind::Mouse, 0, 0x8A8A8A, "BatteryTray : aucun appareil");
        } else {
            unsafe { Shell_NotifyIconW(NIM_DELETE, &base_data(window, PLACEHOLDER)) };
        }
    }

    fn notify(&self, alert: &Alert) {
        let (id, title, body, warning) = match alert {
            Alert::Low(id, name, p) => (id, "Batterie faible", format!("{name} : {p} %"), true),
            Alert::Full(id, name) => (id, "Charge terminée", format!("{name} est chargé."), false),
        };
        let Some(index) = self.icons.iter().position(|i| i == id) else { return };
        let mut data = base_data(self.window, index);
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = if warning { NIIF_WARNING } else { NIIF_INFO };
        copy_to(&mut data.szInfoTitle, title);
        copy_to(&mut data.szInfo, &body);
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) };
    }

    fn receive(&mut self) {
        let Some(readings) = PENDING.lock().ok().and_then(|mut p| p.take()) else { return };
        let now = state::now();
        let ids: Vec<String> = readings.iter().map(|r| r.id.clone()).collect();
        let alerts = self.state.apply(readings, now, &self.settings);
        if self.settings.remember(ids.iter().map(String::as_str)) {
            settings::write_atomic("reglages.tsv", &self.settings.serialize());
        }
        for d in &mut self.state.devices {
            if d.online {
                let pref = self.settings.pref(&d.reading.id);
                let rating = (pref.rated > 0).then(|| f64::from(pref.rated)).or_else(|| autonomy::rated_hours(&d.reading.name));
                d.estimate = self.estimator.apply(&d.reading, rating, now);
            }
        }
        self.refresh_icons();
        for alert in &alerts {
            self.notify(alert);
        }
        self.state.save();
        // l'historique d'autonomie n'est écrit qu'au plus toutes les 5 minutes
        if now.saturating_sub(self.saved_estimates) >= 300 {
            self.saved_estimates = now;
            settings::write_atomic("autonomie.tsv", &self.estimator.serialize());
        }
    }

    pub fn read_now(&self) {
        let _ = self.wake.send(());
    }

    /// Prend en compte des réglages validés dans la fenêtre.
    pub fn apply_settings(&mut self, new: Settings, startup: bool) {
        self.settings = new;
        self.settings.normalize();
        settings::write_atomic("reglages.tsv", &self.settings.serialize());
        if startup != system::startup_enabled() {
            let exe = std::env::current_exe().ok();
            system::set_startup(if startup { exe.as_ref() } else { None });
        }
        INTERVAL.store(u64::from(self.settings.poll_minutes) * 60, Ordering::Relaxed);
        self.refresh_icons();
        self.read_now();
    }
}

mod devices_kind {
    pub use crate::devices::Kind;
}

fn set_icon(window: HWND, index: usize, kind: devices_kind::Kind, percent: u8, color: u32, tip: &str) {
    let mut data = base_data(window, index);
    data.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE | NIF_SHOWTIP;
    data.uCallbackMessage = WM_TRAY;
    data.hIcon = icon::create(kind, percent, color);
    copy_to(&mut data.szTip, tip);
    unsafe {
        if Shell_NotifyIconW(NIM_MODIFY, &data) == 0 {
            Shell_NotifyIconW(NIM_ADD, &data);
        }
        DestroyIcon(data.hIcon);
    }
}

pub fn run() {
    unsafe {
        // même nom que la version C# : les deux ne tournent jamais en même temps
        let name = wide(r"Local\BatteryTray.SingleInstance");
        let mutex = CreateMutexW(null(), 1, name.as_ptr());
        if mutex.is_null() || GetLastError() == ERROR_ALREADY_EXISTS {
            return;
        }
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        let instance = GetModuleHandleW(null());
        let class_name = wide(APP);
        let mut class: WNDCLASSW = std::mem::zeroed();
        class.lpfnWndProc = Some(window_proc);
        class.hInstance = instance;
        class.lpszClassName = class_name.as_ptr();
        RegisterClassW(&class);
        // fenêtre jamais affichée (pas « message seul » : elle doit recevoir les diffusions)
        let window = CreateWindowExW(0, class_name.as_ptr(), class_name.as_ptr(), 0, 0, 0, 0, 0, null_mut(), null_mut(), instance, null());
        if window.is_null() {
            return;
        }
        windows::register(instance);
        watchdog::clear_intentional_exit();
        // enregistrée par l'application elle-même, seulement si c'est l'exemplaire installé
        if let Some(dir) = system::installed_dir() {
            std::thread::spawn(move || watchdog::register(&dir));
        }

        let settings = Settings::parse(&settings::read("reglages.tsv"));
        INTERVAL.store(u64::from(settings.poll_minutes) * 60, Ordering::Relaxed);
        let (wake, woken) = mpsc::channel::<()>();
        let target = window as isize;
        std::thread::spawn(move || read_loop(target, woken));

        let taskbar = wide("TaskbarCreated");
        CURRENT.with(|cell| {
            *cell.borrow_mut() = Some(App {
                window,
                state: State::load(),
                settings,
                estimator: Estimator::parse(&settings::read("autonomie.tsv")),
                icons: Vec::new(),
                wake,
                taskbar_created: RegisterWindowMessageW(taskbar.as_ptr()),
                saved_estimates: 0,
                ignore_up: false,
            })
        });
        with(App::refresh_icons);

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

fn read_loop(window: isize, woken: mpsc::Receiver<()>) {
    let mut reader = Reader::default();
    let mut known: Vec<String> = Vec::new();
    loop {
        let readings = reader.read_all();
        // un appareil Logitech déjà vu qui ne répond pas dort : réessai rapproché
        let asleep = known.iter().any(|id| id.starts_with("logi-") && !readings.iter().any(|r| &r.id == id));
        for r in &readings {
            if !known.contains(&r.id) {
                known.push(r.id.clone());
            }
        }
        if let Ok(mut slot) = PENDING.lock() {
            *slot = Some(readings);
        }
        unsafe { PostMessageW(window as HWND, WM_READINGS, 0, 0) };
        system::trim();
        let wait = if asleep { 20 } else { INTERVAL.load(Ordering::Relaxed) };
        match woken.recv_timeout(Duration::from_secs(wait)) {
            Err(RecvTimeoutError::Disconnected) => break,
            Err(RecvTimeoutError::Timeout) => {}
            // les évènements arrivent en rafales : on attend le calme
            Ok(()) => while woken.recv_timeout(Duration::from_secs(2)).is_ok() {},
        }
    }
}

/// Arrêt voulu : la tâche de surveillance ne doit pas relancer.
unsafe fn quit(window: HWND) {
    watchdog::mark_intentional_exit();
    DestroyWindow(window);
}

/// Menu du clic droit. L'état n'est pas emprunté pendant qu'il est ouvert.
unsafe fn show_menu(window: HWND) {
    let menu = CreatePopupMenu();
    let labels = [(MENU_REFRESH, "Actualiser"), (MENU_SETTINGS, "Paramètres…"), (MENU_STARTUP, "Démarrer avec Windows"), (MENU_QUIT, "Quitter")];
    for (id, label) in labels {
        let checked = if id == MENU_STARTUP && system::startup_enabled() { MF_CHECKED } else { MF_UNCHECKED };
        let label = wide(label);
        AppendMenuW(menu, MF_STRING | checked, id, label.as_ptr());
    }
    let mut at = POINT { x: 0, y: 0 };
    GetCursorPos(&mut at);
    SetForegroundWindow(window);
    let choice = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.x, at.y, 0, window, null());
    DestroyMenu(menu);
    match choice as usize {
        MENU_REFRESH => {
            with(|app| app.read_now());
        }
        MENU_SETTINGS => windows::open_settings(),
        MENU_STARTUP => {
            let exe = std::env::current_exe().ok();
            system::set_startup(if system::startup_enabled() { None } else { exe.as_ref() });
        }
        MENU_QUIT => quit(window),
        _ => {}
    }
}

unsafe extern "system" fn window_proc(window: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_TRAY => {
            match lparam as u32 {
                WM_RBUTTONUP | WM_CONTEXTMENU => show_menu(window),
                WM_LBUTTONUP => {
                    if !with(|app| std::mem::take(&mut app.ignore_up)).unwrap_or(false) {
                        windows::toggle_flyout();
                    }
                }
                WM_LBUTTONDBLCLK => {
                    with(|app| app.ignore_up = true);
                    windows::close_flyout();
                    windows::open_settings();
                }
                _ => {}
            }
            0
        }
        // même effet que le menu « Quitter »
        WM_COMMAND if wparam == MENU_QUIT => {
            quit(window);
            0
        }
        WM_READINGS => {
            with(App::receive);
            windows::refresh_flyout();
            system::trim();
            0
        }
        // périphérique branché ou débranché, sortie de veille
        WM_DEVICECHANGE | WM_POWERBROADCAST => {
            with(|app| app.read_now());
            1
        }
        WM_DESTROY => {
            with(|app| {
                for i in 0..app.icons.len() {
                    Shell_NotifyIconW(NIM_DELETE, &base_data(window, i));
                }
                Shell_NotifyIconW(NIM_DELETE, &base_data(window, PLACEHOLDER));
                app.state.save();
                settings::write_atomic("autonomie.tsv", &app.estimator.serialize());
            });
            PostQuitMessage(0);
            0
        }
        _ => {
            // l'Explorateur a redémarré : les icônes sont à recréer
            let recreated = with(|app| {
                let restarted = msg == app.taskbar_created;
                if restarted {
                    app.icons.clear();
                    app.refresh_icons();
                }
                restarted
            });
            if recreated == Some(true) { 0 } else { DefWindowProcW(window, msg, wparam, lparam) }
        }
    }
}
