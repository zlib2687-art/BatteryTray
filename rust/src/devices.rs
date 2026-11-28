//! Lecture des trois appareils. Portage des modules C# de BatteryTray ; seules des requêtes de lecture sont envoyées.

use crate::hid::{self, Info, Stream};
use crate::pointer::{self, Pointer};
use std::time::{Duration, Instant};

const RAZER: u16 = 0x1532;
const LOGITECH: u16 = 0x046D;
const PRO_X2: u16 = 0x0AF7;
const SW_ID: u8 = 0x0A;

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Kind {
    Mouse,
    Headset,
    Keyboard,
    Gamepad,
    Microphone,
    Other,
}

#[derive(Clone, Debug)]
pub struct Reading {
    pub id: String,
    pub name: String,
    pub kind: Kind,
    pub percent: u8,
    pub charging: bool,
    /// Pourcentage estimé d'après la tension (affiché avec « ~ »).
    pub estimated: bool,
    /// Réglages en cours (souris Logitech et Razer seulement).
    pub pointer: Pointer,
}

/// Comment relire les réglages d'une souris sans tout redécouvrir.
enum Source {
    Razer { info: Info, transaction: u8, extended_rate: bool },
    /// Numéros de fonction HID++ et variante (vrai : la plus récente).
    Hidpp { info: Info, device: u8, dpi: Option<(u8, bool)>, rate: Option<(u8, bool)> },
}

struct Live {
    id: String,
    source: Source,
    pointer: Pointer,
}

#[derive(Default)]
pub struct Reader {
    /// Dernières tensions de chaque souris à tension seule (médiane sur 5).
    voltages: Vec<(String, Vec<u16>)>,
    /// Récepteurs Logitech que ce module lit déjà : les pilotes complémentaires les ignorent.
    logitech_pids: Vec<u16>,
    /// Souris dont le DPI et la fréquence sont relus en continu.
    mice: Vec<Live>,
    /// Sources standard de Windows, relues au plus une fois par minute.
    generic: Option<(Instant, Vec<Reading>)>,
}

impl Reader {
    pub fn read_all(&mut self) -> Vec<Reading> {
        let all = hid::enumerate(&[RAZER, LOGITECH]);
        self.mice.clear();
        let mut out = Vec::new();
        out.extend(pro_x2(&all));
        for info in all.iter().filter(|d| d.vid == LOGITECH && d.pid != PRO_X2 && d.usage_page >= 0xFF00 && d.output_len == 20) {
            self.logitech(info, &mut out);
        }
        for info in all.iter().filter(|d| d.vid == RAZER && d.feature_len >= 90) {
            if let Some((r, source)) = razer(info) {
                if !out.iter().any(|o| o.id == r.id) {
                    self.mice.push(Live { id: r.id.clone(), source, pointer: r.pointer });
                    out.push(r);
                }
            }
        }
        // pilotes de marques (SteelSeries, Corsair, HyperX, PlayStation, casques Razer)
        crate::generic::merge(&mut out, crate::brands::read_all(&self.logitech_pids));
        // toutes marques ensuite : un appareil déjà lu par un module propre à sa marque n'est pas répété
        if self.generic.as_ref().is_none_or(|g| g.0.elapsed() >= Duration::from_secs(60)) {
            let mut found = crate::generic::bluetooth_le();
            crate::generic::merge(&mut found, crate::generic::hid_standard());
            crate::generic::merge(&mut found, crate::generic::windows_property());
            self.generic = Some((Instant::now(), found));
        }
        if let Some((_, found)) = &self.generic {
            crate::generic::merge(&mut out, found.clone());
        }
        out
    }

    /// Relit les réglages des souris connues ; ne rend que ceux qui ont changé. `with_rate` : la fréquence aussi.
    pub fn poll_pointers(&mut self, with_rate: bool) -> Vec<(String, Pointer)> {
        let mut changed = Vec::new();
        for mouse in &mut self.mice {
            let read = match &mouse.source {
                Source::Razer { info, transaction, extended_rate } => {
                    let len = info.feature_len.clamp(91, 4096);
                    pointer::razer_live(len, *transaction, with_rate.then_some(*extended_rate), |r| hid::feature_exchange(info, r))
                }
                Source::Hidpp { info, device, dpi, rate } => match Stream::open(info) {
                    Some(s) => logitech_read(&s, *device, *dpi, rate.filter(|_| with_rate)),
                    None => continue,
                },
            };
            // une souris endormie ne répond pas : on garde la dernière valeur
            let merged = Pointer { dpi: read.dpi.or(mouse.pointer.dpi), rate: read.rate.or(mouse.pointer.rate) };
            if merged != mouse.pointer {
                mouse.pointer = merged;
                changed.push((mouse.id.clone(), merged));
            }
        }
        changed
    }

    fn smooth(&mut self, id: &str, mv: u16) -> u16 {
        let index = match self.voltages.iter().position(|(k, _)| k == id) {
            Some(i) => i,
            None => {
                self.voltages.push((id.to_string(), Vec::new()));
                self.voltages.len() - 1
            }
        };
        let recent = &mut self.voltages[index].1;
        recent.push(mv);
        if recent.len() > 5 {
            recent.remove(0);
        }
        median(recent)
    }

    fn logitech(&mut self, info: &Info, out: &mut Vec<Reading>) {
        let Some(s) = Stream::open(info) else { return };
        for index in [0xFFu8, 1, 2, 3, 4, 5, 6] {
            match request(&s, index, 0x00, 1, &[0, 0, SW_ID]) {
                Some(pong) if pong[2] != 0x8F && pong[2] != 0xFF => {}
                _ => continue,
            }
            let name = read_name(&s, index).unwrap_or_else(|| format!("Logitech {:04X}", info.pid));
            let name = short_name(&name);
            let id = format!("logi-{:04X}-{index}", info.pid);
            if !self.logitech_pids.contains(&info.pid) {
                self.logitech_pids.push(info.pid);
            }
            let lower = name.to_lowercase();
            let kind = if lower.contains("headset") {
                Kind::Headset
            } else if lower.contains("keyboard") {
                Kind::Keyboard
            } else {
                Kind::Mouse
            };
            let mut pointer = Pointer::default();
            if kind == Kind::Mouse {
                let (dpi, rate) = (logitech_dpi_function(&s, index), logitech_rate_function(&s, index));
                pointer = logitech_read(&s, index, dpi, rate);
                self.mice.retain(|m| m.id != id);
                self.mice.push(Live { id: id.clone(), source: Source::Hidpp { info: info.clone(), device: index, dpi, rate }, pointer });
            }
            let reading = |percent: u8, charging: bool, estimated: bool| Reading {
                id: id.clone(),
                name: name.clone(),
                kind,
                percent: percent.min(100),
                charging,
                estimated,
                pointer,
            };

            // pourcentage fourni par l'appareil (0x1004 puis 0x1000)
            let mut found = false;
            for (feature, function) in [(0x1004u16, 1u8), (0x1000, 0)] {
                let Some(f) = feature_index(&s, index, feature) else { continue };
                if let Some(a) = request(&s, index, f, function, &[]).filter(|a| a[2] == f) {
                    out.push(reading(a[4], matches!(a[6], 1 | 3 | 4), false));
                    found = true;
                    break;
                }
            }
            if found {
                continue;
            }
            // tension seulement (0x1001) : pourcentage estimé
            let Some(f) = feature_index(&s, index, 0x1001) else { continue };
            if let Some(c) = request(&s, index, f, 0, &[]).filter(|c| c[2] == f) {
                let mv = self.smooth(&id, u16::from_be_bytes([c[4], c[5]]));
                out.push(reading(estimate(mv), c[6] & 0x80 != 0, true));
            }
        }
    }
}

fn request(s: &Stream, device: u8, feature: u8, function: u8, params: &[u8]) -> Option<Vec<u8>> {
    let mut buf = [0u8; 20];
    buf[..4].copy_from_slice(&[0x11, device, feature, (function << 4) | SW_ID]);
    let n = params.len().min(16);
    buf[4..4 + n].copy_from_slice(&params[..n]);
    if !s.write(&buf) {
        return None;
    }
    let end = Instant::now() + Duration::from_millis(500);
    while Instant::now() < end {
        let mut r = s.read(400)?;
        if r.len() < 7 || (r[0] != 0x11 && r[0] != 0x10) || r[1] != device {
            continue;
        }
        let ours = r[2] == feature && r[3] & 0x0F == SW_ID;
        if r[2] == 0xFF || r[2] == 0x8F || ours {
            r.resize(20, 0);
            return Some(r);
        }
    }
    None
}

/// Fonction de sensibilité d'une souris HID++ 2.0 : 0x2201, sinon 0x2202.
fn logitech_dpi_function(s: &Stream, device: u8) -> Option<(u8, bool)> {
    feature_index(s, device, 0x2201).map(|f| (f, false)).or_else(|| feature_index(s, device, 0x2202).map(|f| (f, true)))
}

/// Fonction de fréquence : 0x8061, sinon 0x8060.
fn logitech_rate_function(s: &Stream, device: u8) -> Option<(u8, bool)> {
    feature_index(s, device, 0x8061).map(|f| (f, true)).or_else(|| feature_index(s, device, 0x8060).map(|f| (f, false)))
}

fn logitech_read(s: &Stream, device: u8, dpi: Option<(u8, bool)>, rate: Option<(u8, bool)>) -> Pointer {
    let ask = |f: u8, function: u8| request(s, device, f, function, &[0]).filter(|a| a[2] == f);
    Pointer {
        dpi: dpi.and_then(|(f, extended)| {
            let a = ask(f, if extended { 5 } else { 2 })?;
            if extended { pointer::hidpp_dpi_extended(&a) } else { pointer::hidpp_dpi(&a) }
        }),
        rate: rate.and_then(|(f, extended)| {
            let a = ask(f, if extended { 2 } else { 1 })?;
            if extended { pointer::hidpp_rate_extended(&a) } else { pointer::hidpp_rate(&a) }
        }),
    }
}

fn feature_index(s: &Stream, device: u8, feature: u16) -> Option<u8> {
    let r = request(s, device, 0x00, 0, &feature.to_be_bytes())?;
    (r[2] == 0x00 && r[4] > 0).then_some(r[4])
}

fn read_name(s: &Stream, device: u8) -> Option<String> {
    let f = feature_index(s, device, 0x0005)?;
    let count = request(s, device, f, 0, &[]).filter(|c| c[2] == f)?[4].min(64) as usize;
    let mut name = String::new();
    while name.len() < count {
        let before = name.len();
        let Some(r) = request(s, device, f, 1, &[before as u8]).filter(|r| r[2] == f) else { break };
        for &b in r[4..].iter().take(count - before) {
            if !(0x20..0x7F).contains(&b) {
                break;
            }
            name.push(b as char);
        }
        if name.len() == before {
            break;
        }
    }
    (!name.is_empty()).then_some(name)
}

/// « G502 LIGHTSPEED Wireless Gaming Mouse » → « G502 LIGHTSPEED ».
pub fn short_name(name: &str) -> String {
    for suffix in [" Wireless Gaming Mouse", " Wireless Gaming Headset", " Wireless Gaming Keyboard", " Gaming Mouse", " Wireless Mouse"] {
        let Some(cut) = name.len().checked_sub(suffix.len()) else { continue };
        if name.get(cut..).is_some_and(|end| end.eq_ignore_ascii_case(suffix)) {
            let shorter = name[..cut].trim();
            if !shorter.is_empty() && !shorter.eq_ignore_ascii_case("Wireless") {
                return shorter.to_string();
            }
            break;
        }
    }
    name.to_string()
}

fn pro_x2(all: &[Info]) -> Option<Reading> {
    let info = all.iter().find(|d| d.vid == LOGITECH && d.pid == PRO_X2 && d.path_text.contains("col03"))?;
    let s = Stream::open(info)?;
    let mut buf = [0u8; 64];
    buf[..10].copy_from_slice(&[0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A]);
    if !s.write(&buf) {
        return None;
    }
    let end = Instant::now() + Duration::from_secs(2);
    while Instant::now() < end {
        let Some(r) = s.read(500) else { continue };
        if let Some((percent, charging)) = parse_pro_x2(&r) {
            return Some(Reading {
                id: "prox2".into(),
                name: "Logitech PRO X 2".into(),
                kind: Kind::Headset,
                percent,
                charging,
                estimated: false,
                pointer: Pointer::default(),
            });
        }
    }
    None
}

/// Trame batterie : id 0x51, type 0x0B, étiquette 0x04, niveau <= 100, état 0x02 = en charge.
pub fn parse_pro_x2(r: &[u8]) -> Option<(u8, bool)> {
    (r.len() >= 13 && r[0] == 0x51 && r[1] == 0x0B && r[8] == 0x04 && r[10] <= 100).then(|| (r[10], r[12] == 0x02))
}

/// Requête Razer de 90 octets (plus l'identifiant de rapport) : classe 0x07, somme de contrôle XOR.
pub fn razer_request(len: usize, transaction: u8, command: u8) -> Vec<u8> {
    razer_command(len, transaction, 0x07, command, 2, &[])
}

/// Requête Razer quelconque : classe, commande, taille des données et premiers arguments.
pub fn razer_command(len: usize, transaction: u8, class: u8, command: u8, size: u8, arguments: &[u8]) -> Vec<u8> {
    let mut buf = vec![0u8; len];
    buf[2] = transaction;
    buf[6] = size;
    buf[7] = class;
    buf[8] = command;
    buf[9..9 + arguments.len()].copy_from_slice(arguments);
    buf[89] = buf[3..89].iter().fold(0, |c, &b| c ^ b);
    buf
}

fn razer(info: &Info) -> Option<(Reading, Source)> {
    let len = info.feature_len.clamp(91, 4096);
    for transaction in [0x1Fu8, 0xFF, 0x3F, 0x9F] {
        let Some(level) = hid::feature_exchange(info, &razer_request(len, transaction, 0x80)) else { continue };
        if level[1] != 0x02 {
            continue;
        }
        let charging = hid::feature_exchange(info, &razer_request(len, transaction, 0x84))
            .is_some_and(|c| c[1] == 0x02 && c[10] == 1);
        let product = if info.product.is_empty() { format!("Razer {:04X}", info.pid) } else { info.product.clone() };
        let name = if product.to_lowercase().starts_with("razer") { product } else { format!("Razer {product}") };
        let (pointer, extended_rate) = pointer::razer(len, transaction, |request| hid::feature_exchange(info, request));
        let reading = Reading {
            id: format!("razer-{:04X}", info.pid),
            name,
            kind: Kind::Mouse,
            percent: ((level[10] as u32 * 100 + 127) / 255) as u8,
            charging,
            estimated: false,
            pointer,
        };
        return Some((reading, Source::Razer { info: info.clone(), transaction, extended_rate }));
    }
    None
}

pub fn median(values: &[u16]) -> u16 {
    let mut sorted = values.to_vec();
    sorted.sort_unstable();
    sorted.get(sorted.len() / 2).copied().unwrap_or(0)
}

/// Courbe d'une cellule Li-ion typique (mV, %) : estimation, jamais une valeur lue.
pub fn estimate(mv: u16) -> u8 {
    const POINTS: [(u16, u8); 13] = [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40),
        (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];
    if mv >= POINTS[0].0 {
        return 100;
    }
    for pair in POINTS.windows(2) {
        let ((high_mv, high), (low_mv, low)) = (pair[0], pair[1]);
        if mv >= low_mv {
            let t = f64::from(mv - low_mv) / f64::from(high_mv - low_mv);
            return (f64::from(low) + t * f64::from(high - low)).round() as u8;
        }
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn voltage_curve() {
        assert_eq!(estimate(4300), 100);
        assert_eq!(estimate(4186), 100);
        assert_eq!(estimate(3811), 50);
        assert_eq!(estimate(3500), 0);
        assert_eq!(estimate(0), 0);
    }

    #[test]
    fn names() {
        assert_eq!(short_name("G502 LIGHTSPEED Wireless Gaming Mouse"), "G502 LIGHTSPEED");
        assert_eq!(short_name("Wireless Gaming Mouse"), "Wireless Gaming Mouse");
        assert_eq!(short_name("été"), "été");
    }

    #[test]
    fn headset_frame() {
        let mut r = [0u8; 64];
        r[0] = 0x51;
        r[1] = 0x0B;
        r[8] = 0x04;
        r[10] = 73;
        assert_eq!(parse_pro_x2(&r), Some((73, false)));
        r[12] = 2;
        assert_eq!(parse_pro_x2(&r), Some((73, true)));
        r[10] = 255;
        assert_eq!(parse_pro_x2(&r), None);
        assert_eq!(parse_pro_x2(&r[..5]), None);
    }

    #[test]
    fn razer_checksum() {
        let r = razer_request(91, 0x1F, 0x80);
        assert_eq!(r[89], 2 ^ 0x07 ^ 0x80);
    }
}
