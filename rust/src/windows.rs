//! Fenêtres du panneau et des paramètres : créées à la demande, détruites à la fermeture.

use crate::app::{self, App};
use crate::autonomy::Sample;
use crate::devices::Kind;
use crate::flyout::{self, Chart, Hit, Model, Row};
use crate::gfx;
use crate::i18n::{self, t};
use crate::strings::LANGUAGES;
use crate::panel::{self, Action, Draft, Known};
use crate::settings::{Pref, Settings};
use crate::system::{self, wide};
use std::cell::RefCell;
use std::ptr::{null, null_mut};
use std::time::Instant;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const FLYOUT: &str = "BatteryTrayRustFlyout";
const SETTINGS: &str = "BatteryTrayRustSettings";

struct Flyout {
    window: HWND,
    model: Model,
    hover: Option<Hit>,
    scale: f32,
    shown: Instant,
}

struct Panel {
    window: HWND,
    draft: Draft,
    scale: f32,
}

thread_local! {
    static FLY: RefCell<Option<Flyout>> = const { RefCell::new(None) };
    static HIDDEN_AT: std::cell::Cell<Option<Instant>> = const { std::cell::Cell::new(None) };
    static PANEL: RefCell<Option<Panel>> = const { RefCell::new(None) };
}

pub fn register(instance: HINSTANCE) {
    type Proc = unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT;
    for (name, proc, style) in [(FLYOUT, flyout_proc as Proc, CS_DROPSHADOW), (SETTINGS, settings_proc as Proc, 0)] {
        let name = wide(name);
        unsafe {
            let mut class: WNDCLASSW = std::mem::zeroed();
            class.style = style;
            class.lpfnWndProc = Some(proc);
            class.hInstance = instance;
            class.hCursor = LoadCursorW(null_mut(), IDC_ARROW);
            class.lpszClassName = name.as_ptr();
            RegisterClassW(&class);
        }
    }
}

fn build_model(app: &App, expanded: Option<&str>) -> Model {
    let light = system::light_theme("AppsUseLightTheme");
    let rows: Vec<Row> = app
        .visible()
        .iter()
        .map(|d| Row {
            id: d.reading.id.clone(),
            name: app.name_of(d),
            kind: d.reading.kind,
            percent: d.reading.percent,
            estimated: d.reading.estimated,
            offline: !d.online,
            charging: d.reading.charging,
            line: app.second_line(d),
            extra: if d.online { d.reading.pointer.text() } else { String::new() },
            status: app.status_color(d, light),
        })
        .collect();
    let expanded = expanded.filter(|id| rows.iter().any(|r| r.id == *id)).map(|id| (id.to_string(), chart(app.estimator.samples(id))));
    Model { rows, light, expanded }
}

fn chart(samples: Vec<Sample>) -> Chart {
    let first = samples.first().map_or_else(String::new, |s| system::clock(s.t));
    let last = samples.last().map_or_else(String::new, |s| system::clock(s.t));
    Chart { samples, first, last }
}

/// Écran sous la souris : zone de travail et facteur d'échelle.
fn monitor() -> (RECT, f32) {
    unsafe {
        let mut at = POINT { x: 0, y: 0 };
        GetCursorPos(&mut at);
        let handle = MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST);
        let mut info: MONITORINFO = std::mem::zeroed();
        info.cbSize = size_of::<MONITORINFO>() as u32;
        GetMonitorInfoW(handle, &mut info);
        let (mut x, mut y) = (96u32, 96u32);
        GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, &mut x, &mut y);
        (info.rcWork, x as f32 / 96.0)
    }
}

fn dwm(window: HWND, attribute: u32, value: i32) {
    unsafe { DwmSetWindowAttribute(window, attribute, (&value as *const i32).cast(), 4) };
}

/// Place le panneau en bas à droite, à la taille de son contenu.
fn place(window: HWND, height: f32, scale: f32) {
    let (work, _) = monitor();
    let (w, h) = ((flyout::WIDTH * scale).round() as i32, (height * scale).round() as i32);
    let margin = (12.0 * scale) as i32;
    unsafe {
        SetWindowPos(window, HWND_TOPMOST, work.right - w - margin, (work.bottom - h - margin).max(work.top), w, h, SWP_NOACTIVATE);
        InvalidateRect(window, null(), 0);
    }
}

pub fn toggle_flyout() {
    if FLY.with(|f| f.borrow().is_some()) {
        close_flyout();
        return;
    }
    // le clic sur l'icône ferme d'abord le panneau (perte du focus) : pas de réouverture immédiate
    if HIDDEN_AT.get().is_some_and(|t| t.elapsed().as_millis() < 300) {
        return;
    }
    let Some((model, percent)) = app::with(|a| {
        // données fraîches, sauf si elles datent de moins de 30 s : une lecture complète retarde le suivi du DPI
        if crate::state::now().saturating_sub(a.last_read) >= 30 {
            a.read_now();
        }
        (build_model(a, None), a.settings.scale)
    }) else {
        return;
    };
    let scale = monitor().1 * percent as f32 / 100.0;
    let name = wide(FLYOUT);
    unsafe {
        let window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, name.as_ptr(), name.as_ptr(), WS_POPUP, 0, 0, 10, 10, null_mut(), null_mut(), GetModuleHandleW(null()), null());
        if window.is_null() {
            return;
        }
        gfx::acquire();
        dwm(window, 33, 2); // coins arrondis
        let height = flyout::height(&model);
        FLY.with(|f| *f.borrow_mut() = Some(Flyout { window, model, hover: None, scale, shown: Instant::now() }));
        place(window, height, scale);
        ShowWindow(window, SW_SHOW);
        SetForegroundWindow(window);
    }
}

pub fn close_flyout() {
    if let Some(window) = FLY.with(|f| f.borrow().as_ref().map(|f| f.window)) {
        unsafe { DestroyWindow(window) };
    }
}

/// Reconstruit le contenu du panneau ouvert (nouvelle lecture ou carte dépliée).
fn rebuild(expanded: Option<String>) {
    let Some(model) = app::with(|a| build_model(a, expanded.as_deref())) else { return };
    let height = flyout::height(&model);
    let target = FLY.with(|f| {
        f.borrow_mut().as_mut().map(|f| {
            f.model = model;
            (f.window, f.scale)
        })
    });
    if let Some((window, scale)) = target {
        place(window, height, scale);
    }
}

pub fn refresh_flyout() {
    if let Some(expanded) = FLY.with(|f| f.borrow().as_ref().map(|f| f.model.expanded.as_ref().map(|e| e.0.clone()))) {
        rebuild(expanded);
    }
}

fn logical(x: f32, width: f32) -> f32 {
    if i18n::is_rtl() { width - x } else { x }
}

fn point(lparam: LPARAM, scale: f32) -> (f32, f32) {
    let x = (lparam & 0xFFFF) as u16 as i16;
    let y = ((lparam >> 16) & 0xFFFF) as u16 as i16;
    (f32::from(x) / scale, f32::from(y) / scale)
}

unsafe extern "system" fn flyout_proc(window: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_PAINT => {
            let painted = FLY.with(|f| {
                let guard = f.try_borrow().ok()?;
                let f = guard.as_ref()?;
                gfx::paint(window, f.scale, |c| {
                    c.mirrored(flyout::WIDTH);
                    flyout::draw(c, &f.model, f.hover);
                });
                Some(())
            });
            if painted.is_none() {
                ValidateRect(window, null());
            }
            0
        }
        WM_MOUSEMOVE => {
            let changed = FLY.with(|f| {
                let mut guard = f.borrow_mut();
                let Some(f) = guard.as_mut() else { return false };
                let (x, y) = point(lparam, f.scale);
                let hover = flyout::hit(&f.model, logical(x, flyout::WIDTH), y);
                let changed = hover != f.hover;
                f.hover = hover;
                changed
            });
            if changed {
                InvalidateRect(window, null(), 0);
            }
            0
        }
        WM_LBUTTONUP => {
            let click = FLY.with(|f| {
                let guard = f.borrow();
                let f = guard.as_ref()?;
                let (x, y) = point(lparam, f.scale);
                let hit = flyout::hit(&f.model, logical(x, flyout::WIDTH), y)?;
                let id = match hit {
                    Hit::Row(i) => Some(f.model.rows[i].id.clone()),
                    Hit::Gear => None,
                };
                Some((id, f.model.expanded.as_ref().map(|e| e.0.clone())))
            });
            match click {
                // un seul graphique ouvert à la fois
                Some((Some(id), open)) => rebuild((open.as_deref() != Some(id.as_str())).then_some(id)),
                Some((None, _)) => {
                    close_flyout();
                    open_settings();
                }
                None => {}
            }
            0
        }
        WM_KEYDOWN if wparam == 0x1B => {
            DestroyWindow(window);
            0
        }
        WM_ACTIVATE if (wparam & 0xFFFF) as u32 == WA_INACTIVE => {
            // la barre des tâches reprend brièvement le focus juste après le clic
            let old = FLY.with(|f| f.try_borrow().ok().and_then(|f| f.as_ref().map(|f| f.shown.elapsed().as_millis() > 500)));
            if old == Some(true) {
                PostMessageW(window, WM_CLOSE, 0, 0);
            }
            0
        }
        WM_DESTROY => {
            if FLY.with(|f| f.borrow_mut().take()).is_some() {
                HIDDEN_AT.set(Some(Instant::now()));
                gfx::release();
                system::trim();
            }
            0
        }
        _ => DefWindowProcW(window, msg, wparam, lparam),
    }
}

pub fn open_settings() {
    if let Some(window) = PANEL.with(|p| p.borrow().as_ref().map(|p| p.window)) {
        unsafe {
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }
        return;
    }
    let Some(draft) = app::with(|a| {
        let known = a.state.devices.iter().map(|d| Known { id: d.reading.id.clone(), name: d.reading.name.clone(), kind: d.reading.kind }).collect();
        let mut settings = a.settings.clone();
        settings.remember(a.state.devices.iter().map(|d| d.reading.id.as_str()));
        Draft { settings, startup: system::startup_enabled(), known, page: 0, selected: 0, editing: false, list: None }
    }) else {
        return;
    };
    let (work, scale) = monitor();
    let name = wide(SETTINGS);
    let title = wide(t("set.title"));
    let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    unsafe {
        let mut area = RECT { left: 0, top: 0, right: (panel::WIDTH * scale) as i32, bottom: (panel::HEIGHT * scale) as i32 };
        AdjustWindowRectExForDpi(&mut area, style, 0, 0, (scale * 96.0) as u32);
        let (w, h) = (area.right - area.left, area.bottom - area.top);
        let x = work.left + (work.right - work.left - w) / 2;
        let y = work.top + (work.bottom - work.top - h) / 2;
        let window = CreateWindowExW(0, name.as_ptr(), title.as_ptr(), style, x, y, w, h, null_mut(), null_mut(), GetModuleHandleW(null()), null());
        if window.is_null() {
            return;
        }
        gfx::acquire();
        dwm(window, 20, i32::from(!system::light_theme("AppsUseLightTheme"))); // barre de titre assortie
        PANEL.with(|p| *p.borrow_mut() = Some(Panel { window, draft, scale }));
        ShowWindow(window, SW_SHOW);
        SetForegroundWindow(window);
    }
}

fn finish(window: HWND, accept: bool) {
    if accept {
        if let Some((settings, startup)) = PANEL.with(|p| p.borrow().as_ref().map(|p| (p.draft.settings.clone(), p.draft.startup))) {
            app::with(|a| a.apply_settings(settings, startup));
        }
    }
    unsafe { DestroyWindow(window) };
}

fn zones(p: &Panel, x: f32, y: f32) -> Option<Action> {
    // les zones cliquables sont celles que le dessin produit : on le rejoue sur une image jetable
    let mut found = None;
    let light = system::light_theme("AppsUseLightTheme");
    unsafe {
        use windows_sys::Win32::Graphics::GdiPlus::*;
        let mut bitmap = null_mut();
        GdipCreateBitmapFromScan0(1, 1, 0, 0x26200A, null(), &mut bitmap);
        gfx::on_image(bitmap, |c| {
            found = panel::draw(c, &p.draft, light).into_iter().rev().find(|(r, _)| r.contains(x, y)).map(|z| z.1);
        });
        GdipDisposeImage(bitmap.cast());
    }
    found
}

unsafe extern "system" fn settings_proc(window: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_PAINT => {
            let painted = PANEL.with(|p| {
                let guard = p.try_borrow().ok()?;
                let p = guard.as_ref()?;
                let light = system::light_theme("AppsUseLightTheme");
                gfx::paint(window, p.scale, |c| {
                    c.mirrored(panel::WIDTH);
                    panel::draw(c, &p.draft, light);
                });
                Some(())
            });
            if painted.is_none() {
                ValidateRect(window, null());
            }
            0
        }
        WM_LBUTTONUP => {
            let result = PANEL.with(|p| {
                let mut guard = p.borrow_mut();
                let p = guard.as_mut()?;
                let (x, y) = point(lparam, p.scale);
                let action = zones(p, logical(x, panel::WIDTH), y);
                match action {
                    Some(a) => p.draft.act(a),
                    None => {
                        (p.draft.editing, p.draft.list) = (false, None);
                        None
                    }
                }
            });
            match result {
                Some(accept) => finish(window, accept),
                None => {
                    InvalidateRect(window, null(), 0);
                }
            }
            0
        }
        WM_MOUSEWHEEL => {
            // trois lignes par cran
            let notches = i32::from((wparam >> 16) as u16 as i16) / 120;
            if PANEL.with(|p| p.borrow_mut().as_mut().is_some_and(|p| p.draft.scroll(-3 * notches))) {
                InvalidateRect(window, null(), 0);
            }
            0
        }
        WM_CHAR => {
            // liste ouverte : Échap et Entrée la referment sans fermer la fenêtre
            let closed = matches!(wparam as u32, 0x1B | 0x0D) && PANEL.with(|p| p.borrow_mut().as_mut().is_some_and(|p| p.draft.list.take().is_some()));
            if closed {
                InvalidateRect(window, null(), 0);
                return 0;
            }
            match wparam as u32 {
                0x1B => finish(window, false),
                0x0D => finish(window, true),
                code => {
                    if let Some(c) = char::from_u32(code) {
                        PANEL.with(|p| {
                            if let Some(p) = p.borrow_mut().as_mut() {
                                p.draft.type_char(c);
                            }
                        });
                        InvalidateRect(window, null(), 0);
                    }
                }
            }
            0
        }
        WM_DESTROY => {
            if PANEL.with(|p| p.borrow_mut().take()).is_some() {
                gfx::release();
                system::trim();
            }
            0
        }
        _ => DefWindowProcW(window, msg, wparam, lparam),
    }
}

/// Dessine le panneau et les quatre pages avec des données d'exemple, en PNG (vérification visuelle).
pub fn previews(folder: &str) {
    for (code, _) in LANGUAGES {
        i18n::set(code);
        let draft = Draft { settings: Settings::default(), startup: true, known: Vec::new(), page: 0, selected: 0, editing: false, list: None };
        gfx::render_png(&format!("{folder}\\langue-{code}.png"), panel::WIDTH, panel::HEIGHT, 1.0, |c| {
            c.mirrored(panel::WIDTH);
            panel::draw(c, &draft, false);
        });
    }
    i18n::set(&std::env::var("BATTERYTRAY_LANG").unwrap_or_else(|_| "fr".into()));
    for light in [false, true] {
        let draft = Draft { settings: Settings::default(), startup: true, known: Vec::new(), page: 0, selected: 0, editing: false, list: Some(0) };
        let suffix = if light { "clair" } else { "sombre" };
        gfx::render_png(&format!("{folder}\\liste-langues-{suffix}.png"), panel::WIDTH, panel::HEIGHT, 1.0, |c| {
            c.mirrored(panel::WIDTH);
            panel::draw(c, &draft, light);
        });
    }
    let samples: Vec<Sample> = (0..=24).map(|i| Sample { t: 1_795_800_000 + i * 600, p: 80 - (i / 3) as u8 }).collect();
    let names = [("a", "Razer DeathAdder V4 Pro", Kind::Mouse), ("b", "G502 LIGHTSPEED", Kind::Mouse), ("c", "Logitech PRO X 2", Kind::Headset)];
    for light in [false, true] {
        let t = gfx::theme(light);
        let suffix = if light { "clair" } else { "sombre" };
        let values = [(72u8, false, false, false, "reste ≈ 2 j 5 h · 01:40", t.accent), (100, true, true, false, "hors ligne · 01:12", t.dim), (31, false, false, true, "en charge · plein dans ≈ 1 h 6 min", t.green)];
        let rows = names
            .iter()
            .zip(values)
            .map(|(&(id, name, kind), (percent, estimated, offline, charging, line, status))| Row {
                id: id.into(), name: name.into(), kind, percent, estimated, offline, charging, line: line.into(), status,
                extra: if kind == Kind::Mouse && !offline { "1600 DPI · 1000 Hz".into() } else { String::new() },
            })
            .collect();
        let model = Model { rows, light, expanded: Some(("a".into(), chart(samples.clone()))) };
        gfx::render_png(&format!("{folder}\\flyout-{suffix}.png"), flyout::WIDTH, flyout::height(&model), 2.0, |c| {
            c.mirrored(flyout::WIDTH);
            flyout::draw(c, &model, None);
        });

        let mut settings = Settings::default();
        settings.devices = names.iter().map(|n| Pref { id: n.0.into(), ..Default::default() }).collect();
        for page in 0..4 {
            let known = names.iter().map(|n| Known { id: n.0.into(), name: n.1.into(), kind: n.2 }).collect();
            let draft = Draft { settings: settings.clone(), startup: true, known, page, selected: 0, editing: false, list: None };
            gfx::render_png(&format!("{folder}\\parametres-{page}-{suffix}.png"), panel::WIDTH, panel::HEIGHT, 1.0, |c| {
                c.mirrored(panel::WIDTH);
                panel::draw(c, &draft, light);
            });
        }
    }
}
