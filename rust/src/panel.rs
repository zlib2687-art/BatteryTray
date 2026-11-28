//! Fenêtre de paramètres à quatre pages. Rien n'est appliqué avant « OK ».

use crate::devices::Kind;
use crate::gfx::{rect, theme, Align, Canvas, Rect, Theme};
use crate::settings::{Settings, POLL_CHOICES, SCALE_CHOICES};

pub const WIDTH: f32 = 720.0;
pub const HEIGHT: f32 = 540.0;
const NAV: f32 = 176.0;
const LEFT: f32 = NAV + 20.0;
const CARD: f32 = WIDTH - LEFT - 20.0;
const PAGES: [(char, &str); 4] = [('\u{E713}', "Général"), ('\u{E7F4}', "Affichage"), ('\u{EA8F}', "Notifications"), ('\u{E772}', "Appareils")];

pub struct Known {
    pub id: String,
    pub name: String,
    pub kind: Kind,
}

/// Copie de travail des réglages, tant que la fenêtre est ouverte.
pub struct Draft {
    pub settings: Settings,
    pub startup: bool,
    pub known: Vec<Known>,
    pub page: usize,
    pub selected: usize,
    pub editing: bool,
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Field {
    Poll,
    Scale,
    Orange,
    Red,
    Low,
    DeviceLow,
    Rated,
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Toggle {
    Startup,
    Brand,
    NotifyLow,
    NotifyFull,
    Shown,
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Action {
    Page(usize),
    Flip(Toggle),
    Step(Field, i32),
    Select(usize),
    EditName,
    Move(i32),
    Ok,
    Cancel,
}

impl Draft {
    /// Index dans `settings.devices` des appareils connus, dans l'ordre choisi.
    fn listed(&self) -> Vec<usize> {
        (0..self.settings.devices.len()).filter(|&i| self.known.iter().any(|k| k.id == self.settings.devices[i].id)).collect()
    }

    fn current(&self) -> Option<usize> {
        self.listed().get(self.selected).copied()
    }

    /// Applique un clic. `Some(true)` : valider et fermer ; `Some(false)` : annuler.
    pub fn act(&mut self, action: Action) -> Option<bool> {
        if action != Action::EditName {
            self.editing = false;
        }
        match action {
            Action::Ok => return Some(true),
            Action::Cancel => return Some(false),
            Action::Page(p) => self.page = p,
            Action::Select(i) => self.selected = i,
            Action::EditName => self.editing = true,
            Action::Move(by) => {
                let listed = self.listed();
                let to = self.selected as i32 + by;
                if to >= 0 && (to as usize) < listed.len() {
                    self.settings.devices.swap(listed[self.selected], listed[to as usize]);
                    self.selected = to as usize;
                }
            }
            Action::Flip(t) => match t {
                Toggle::Startup => self.startup = !self.startup,
                Toggle::Brand => self.settings.brand_colors = !self.settings.brand_colors,
                Toggle::NotifyLow => self.settings.notify_low = !self.settings.notify_low,
                Toggle::NotifyFull => self.settings.notify_full = !self.settings.notify_full,
                Toggle::Shown => {
                    if let Some(i) = self.current() {
                        self.settings.devices[i].hidden = !self.settings.devices[i].hidden;
                    }
                }
            },
            Action::Step(field, by) => self.step(field, by),
        }
        None
    }

    fn step(&mut self, field: Field, by: i32) {
        let device = self.current();
        let s = &mut self.settings;
        let add = |v: u32, by: i32, min: u32, max: u32| (v as i64 + i64::from(by)).clamp(i64::from(min), i64::from(max)) as u32;
        let next = |choices: &[u32], value: u32| {
            let i = choices.iter().position(|&c| c >= value).unwrap_or(choices.len() - 1) as i32;
            choices[(i + by).clamp(0, choices.len() as i32 - 1) as usize]
        };
        match field {
            Field::Poll => s.poll_minutes = next(&POLL_CHOICES, s.poll_minutes),
            Field::Scale => s.scale = next(&SCALE_CHOICES, s.scale),
            Field::Orange => s.orange_below = add(s.orange_below.into(), by * 5, u32::from(s.red_below) + 1, 100) as u8,
            Field::Red => s.red_below = add(s.red_below.into(), by * 5, 1, u32::from(s.orange_below) - 1) as u8,
            Field::Low => s.low_threshold = add(s.low_threshold.into(), by * 5, 5, 95) as u8,
            Field::DeviceLow => {
                if let Some(i) = device {
                    s.devices[i].low = add(s.devices[i].low.into(), by * 5, 0, 95) as u8;
                }
            }
            Field::Rated => {
                if let Some(i) = device {
                    s.devices[i].rated = add(s.devices[i].rated, by * 5, 0, 500);
                }
            }
        }
    }

    /// Saisie du nom : `\u{8}` efface.
    pub fn type_char(&mut self, c: char) {
        if !self.editing {
            return;
        }
        let Some(i) = self.current() else { return };
        let name = &mut self.settings.devices[i].name;
        if c == '\u{8}' {
            name.pop();
        } else if !c.is_control() && name.chars().count() < 40 {
            name.push(c);
        }
    }
}

type Zones = Vec<(Rect, Action)>;

fn button(c: &Canvas, t: &Theme, zones: &mut Zones, r: Rect, label: &str, primary: bool, action: Action) {
    c.fill(r, 5.0, if primary { t.accent } else { t.card });
    c.stroke(r, 5.0, t.border, 1.0);
    c.text(r, label, 13.0, if primary { t.on_accent } else { t.text }, Align::Center);
    zones.push((r, action));
}

/// Carte d'un réglage ; renvoie son rectangle.
fn row(c: &Canvas, t: &Theme, y: f32, label: &str) -> Rect {
    let r = rect(LEFT, y, CARD, 44.0);
    c.fill(r, 7.0, t.card);
    c.stroke(r, 7.0, t.border, 1.0);
    c.text(rect(r.x + 14.0, r.y, 270.0, r.h), label, 13.0, t.text, Align::Left);
    r
}

fn toggle(c: &Canvas, t: &Theme, zones: &mut Zones, y: f32, label: &str, on: bool, which: Toggle) {
    let r = row(c, t, y, label);
    let track = rect(r.x + r.w - 54.0, r.y + 12.0, 40.0, 20.0);
    if on {
        c.fill(track, 10.0, t.accent);
        c.fill(rect(track.x + 23.0, track.y + 4.0, 12.0, 12.0), 6.0, t.on_accent);
    } else {
        c.stroke(track, 10.0, t.sub, 1.2);
        c.fill(rect(track.x + 5.0, track.y + 4.0, 12.0, 12.0), 6.0, t.sub);
    }
    c.text(rect(track.x - 86.0, r.y, 78.0, r.h), if on { "Activé" } else { "Désactivé" }, 12.0, t.sub, Align::Right);
    zones.push((r, Action::Flip(which)));
}

fn stepper(c: &Canvas, t: &Theme, zones: &mut Zones, y: f32, label: &str, value: &str, field: Field) {
    let r = row(c, t, y, label);
    let right = r.x + r.w - 14.0;
    for (x, symbol, by) in [(right - 180.0, "−", -1), (right - 30.0, "+", 1)] {
        let b = rect(x, r.y + 7.0, 30.0, 30.0);
        c.fill(b, 5.0, t.hover);
        c.stroke(b, 5.0, t.border, 1.0);
        c.text(b, symbol, 16.0, t.text, Align::Center);
        zones.push((b, Action::Step(field, by)));
    }
    c.text(rect(right - 148.0, r.y, 116.0, r.h), value, 13.0, t.text, Align::Center);
}

/// Dessine la fenêtre et renvoie les zones cliquables.
pub fn draw(c: &Canvas, d: &Draft, light: bool) -> Zones {
    let t = theme(light);
    let mut zones = Zones::new();
    c.fill(rect(-2.0, -2.0, WIDTH + 4.0, HEIGHT + 4.0), 0.0, t.back);
    c.fill(rect(-2.0, -2.0, NAV + 2.0, HEIGHT + 4.0), 0.0, t.nav);

    for (i, (symbol, label)) in PAGES.iter().enumerate() {
        let r = rect(10.0, 16.0 + i as f32 * 42.0, NAV - 20.0, 36.0);
        if i == d.page {
            c.fill(r, 6.0, t.card);
            c.fill(rect(r.x, r.y + 10.0, 3.0, 16.0), 1.5, t.accent);
        }
        c.symbol(rect(r.x + 10.0, r.y, 22.0, r.h), *symbol, 15.0, t.text);
        c.text(rect(r.x + 38.0, r.y, r.w - 40.0, r.h), label, 13.0, t.text, Align::Left);
        zones.push((r, Action::Page(i)));
    }
    c.strong(rect(LEFT, 12.0, CARD, 36.0), PAGES[d.page.min(3)].1, 21.0, t.text, Align::Left);

    let s = &d.settings;
    let at = |i: usize| 58.0 + i as f32 * 50.0;
    match d.page {
        0 => {
            stepper(c, &t, &mut zones, at(0), "Actualiser toutes les", &format!("{} min", s.poll_minutes), Field::Poll);
            toggle(c, &t, &mut zones, at(1), "Démarrer avec Windows", d.startup, Toggle::Startup);
        }
        1 => {
            stepper(c, &t, &mut zones, at(0), "Taille du panneau", &format!("{} %", s.scale), Field::Scale);
            toggle(c, &t, &mut zones, at(1), "Couleurs de marque", s.brand_colors, Toggle::Brand);
            stepper(c, &t, &mut zones, at(2), "Orange sous", &format!("{} %", s.orange_below), Field::Orange);
            stepper(c, &t, &mut zones, at(3), "Rouge à partir de", &format!("{} %", s.red_below), Field::Red);
        }
        2 => {
            toggle(c, &t, &mut zones, at(0), "Batterie faible", s.notify_low, Toggle::NotifyLow);
            toggle(c, &t, &mut zones, at(1), "Charge terminée", s.notify_full, Toggle::NotifyFull);
            stepper(c, &t, &mut zones, at(2), "Seuil de batterie faible", &format!("{} %", s.low_threshold), Field::Low);
        }
        _ => draw_devices(c, &t, &mut zones, d),
    }

    let y = HEIGHT - 48.0;
    button(c, &t, &mut zones, rect(WIDTH - 130.0, y, 110.0, 32.0), "OK", true, Action::Ok);
    button(c, &t, &mut zones, rect(WIDTH - 250.0, y, 110.0, 32.0), "Annuler", false, Action::Cancel);
    zones
}

fn draw_devices(c: &Canvas, t: &Theme, zones: &mut Zones, d: &Draft) {
    let listed = d.listed();
    if listed.is_empty() {
        c.text(rect(LEFT, 58.0, CARD, 30.0), "Aucun appareil vu pour l'instant.", 13.0, t.sub, Align::Left);
        return;
    }
    let mut y = 56.0;
    for (position, &i) in listed.iter().enumerate().take(4) {
        let pref = &d.settings.devices[i];
        let Some(known) = d.known.iter().find(|k| k.id == pref.id) else { continue };
        let r = rect(LEFT, y, CARD, 30.0);
        let selected = position == d.selected;
        c.fill(r, 6.0, if selected { t.hover } else { t.card });
        c.stroke(r, 6.0, if selected { t.accent } else { t.border }, 1.0);
        c.symbol(rect(r.x + 8.0, r.y, 24.0, r.h), if known.kind == Kind::Mouse { '\u{E962}' } else { '\u{E7F6}' }, 14.0, t.text);
        let name = if pref.name.trim().is_empty() { &known.name } else { &pref.name };
        c.text(rect(r.x + 36.0, r.y, 330.0, r.h), name, 13.0, if pref.hidden { t.dim } else { t.text }, Align::Left);
        if pref.hidden {
            c.text(rect(r.x + r.w - 100.0, r.y, 88.0, r.h), "masqué", 11.5, t.dim, Align::Right);
        }
        zones.push((r, Action::Select(position)));
        y += 34.0;
    }

    let Some(i) = d.current() else { return };
    let pref = &d.settings.devices[i];
    let original = d.known.iter().find(|k| k.id == pref.id).map_or("", |k| k.name.as_str());
    let mut y = 200.0;
    toggle(c, t, zones, y, "Afficher", !pref.hidden, Toggle::Shown);
    y += 48.0;

    let r = row(c, t, y, "Nom");
    let field = rect(r.x + r.w - 294.0, r.y + 7.0, 280.0, 30.0);
    c.fill(field, 5.0, t.back);
    c.stroke(field, 5.0, if d.editing { t.accent } else { t.border }, if d.editing { 1.6 } else { 1.0 });
    let text = if d.editing { format!("{}|", pref.name) } else if pref.name.is_empty() { original.to_string() } else { pref.name.clone() };
    let color = if pref.name.is_empty() && !d.editing { t.dim } else { t.text };
    c.text(rect(field.x + 8.0, field.y, field.w - 12.0, field.h), &text, 12.5, color, Align::Left);
    zones.push((field, Action::EditName));
    y += 48.0;

    let low = if pref.low == 0 { "par défaut".to_string() } else { format!("{} %", pref.low) };
    stepper(c, t, zones, y, "Seuil d'alerte de cet appareil", &low, Field::DeviceLow);
    y += 48.0;
    let rated = if pref.rated == 0 { "automatique".to_string() } else { format!("{} h", pref.rated) };
    stepper(c, t, zones, y, "Autonomie annoncée", &rated, Field::Rated);
    y += 48.0;
    button(c, t, zones, rect(LEFT, y, 110.0, 30.0), "Monter", false, Action::Move(-1));
    button(c, t, zones, rect(LEFT + 120.0, y, 110.0, 30.0), "Descendre", false, Action::Move(1));
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::Pref;

    fn draft() -> Draft {
        let mut settings = Settings::default();
        settings.devices = ["a", "b"].map(|id| Pref { id: id.into(), ..Default::default() }).to_vec();
        let known = ["a", "b"].map(|id| Known { id: id.into(), name: id.into(), kind: Kind::Mouse }).into();
        Draft { settings, startup: true, known, page: 3, selected: 0, editing: false }
    }

    #[test]
    fn choices_stop_at_their_ends() {
        let mut d = draft();
        for _ in 0..9 {
            d.act(Action::Step(Field::Poll, 1));
            d.act(Action::Step(Field::Scale, -1));
            d.act(Action::Step(Field::Red, 1));
        }
        assert_eq!((d.settings.poll_minutes, d.settings.scale, d.settings.red_below), (10, 85, 39));
    }

    #[test]
    fn order_name_and_validation() {
        let mut d = draft();
        d.act(Action::Move(1));
        assert_eq!(d.settings.devices[1].id, "a");
        d.act(Action::EditName);
        for c in "Ab\t".chars() {
            d.type_char(c);
        }
        d.type_char('\u{8}');
        assert_eq!(d.settings.devices[1].name, "A");
        assert_eq!(d.act(Action::Ok), Some(true));
        assert_eq!(d.act(Action::Cancel), Some(false));
    }
}
