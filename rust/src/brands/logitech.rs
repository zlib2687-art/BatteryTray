//! Compléments Logitech (VID 046D) : casques G à mesure de tension (0x1F20), anciens appareils HID++ 1.0, récepteur
//! PRO X3 SUPERSTRIKE, et casque G435. Dérivé de BatteryHub (GPL-3.0), src/drivers/logitech.cpp et logitech_g435_0adf.cpp
//! (références : HID++ 2.0, Solaar, HeadsetControl, g435-battery-percentage MIT). Les appareils déjà lus par le module
//! principal ne sont jamais interrogés ici.

use super::{interpolate, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::Info;
use std::time::{Duration, Instant};

const VID: u16 = 0x046D;
/// Récepteur dont le HID++ est sur la page FF43.
const PASSTHROUGH: u16 = 0xC54F;
const SW_ID: u8 = 0x0D;

const G533_MV: [i64; 6] = [4200, 3850, 3790, 3750, 3680, 3330];
const G533_PCT: [i64; 6] = [100, 50, 30, 20, 5, 0];
const G535_MV: [i64; 6] = [4175, 3817, 3766, 3730, 3664, 3310];
const G633_MV: [i64; 8] = [4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150];
const G633_PCT: [i64; 8] = [100, 80, 60, 40, 20, 10, 5, 0];
const GPRO_MV: [i64; 6] = [4150, 3830, 3780, 3740, 3670, 3320];
const PROX_MV: [i64; 21] = [
    4102, 4046, 3993, 3950, 3915, 3877, 3838, 3809, 3789, 3772, 3758, 3747, 3736, 3722, 3707, 3687, 3661, 3644, 3548, 3426, 3293,
];
const PROX_PCT: [i64; 21] = [100, 95, 90, 85, 80, 75, 70, 65, 60, 55, 50, 45, 40, 35, 30, 25, 20, 15, 10, 5, 0];
const DEFAULT_MV: [i64; 6] = [4100, 3850, 3780, 3730, 3650, 3300];
const LI_MV: [i64; 13] = [4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500];
const LI_PCT: [i64; 13] = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0];

/// Tension → pourcentage d'un casque G (calibration de HeadsetControl).
pub fn headset_percent(pid: u16, mv: i64) -> u8 {
    match pid {
        0x0A66 => interpolate(mv, &G533_MV, &G533_PCT),
        0x0AC4 => interpolate(mv, &G535_MV, &G533_PCT),
        0x0A5C | 0x0A89 | 0x0A5B | 0x0A87 | 0x0AB5 | 0x0AFE => interpolate(mv, &G633_MV, &G633_PCT),
        0x0AAA | 0x0ABA => interpolate(mv, &PROX_MV, &PROX_PCT),
        0x0AA7 | 0x0AFB => interpolate(mv, &GPRO_MV, &G533_PCT),
        _ => interpolate(mv, &DEFAULT_MV, &G533_PCT),
    }
}

#[derive(Debug, PartialEq)]
pub enum Reply {
    Ok(Vec<u8>),
    Error(u8),
}

/// Réponse HID++ à la requête (appareil, b2, b3) ; `None` : un autre message.
pub fn parse_reply(r: &[u8], device: u8, b2: u8, b3: u8) -> Option<Reply> {
    if r.len() < 7 || (r[0] != 0x10 && r[0] != 0x11) || r[1] != device {
        return None;
    }
    if r[2] == b2 && r[3] == b3 {
        return Some(Reply::Ok(r[4..].to_vec()));
    }
    ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == b2 && r[4] == b3).then(|| Reply::Error(r[5]))
}

/// (pourcentage, en charge)
pub type Battery = (Option<u8>, bool);

/// UNIFIED_BATTERY (0x1004).
pub fn unified(p: &[u8]) -> Option<Battery> {
    let (soc, level, state) = (*p.first()?, *p.get(1)?, *p.get(2)?);
    let percent = if (1..=100).contains(&soc) {
        Some(soc)
    } else if level != 0 {
        Some(if level & 8 != 0 { 100 } else if level & 4 != 0 { 60 } else if level & 2 != 0 { 20 } else { 5 })
    } else {
        None
    };
    Some((percent, matches!(state, 1..=3)))
}

/// BATTERY_STATUS (0x1000).
pub fn level_status(p: &[u8]) -> Option<Battery> {
    let (level, state) = (*p.first()?, *p.get(2)?);
    let percent = if level == 0 && state == 3 { Some(100) } else { Some(level).filter(|&v| v <= 100) };
    Some((percent, (1..=4).contains(&state)))
}

/// BATTERY_VOLTAGE (0x1001) : pourcentage estimé d'après la tension.
pub fn voltage(p: &[u8]) -> Option<Battery> {
    let mv = i64::from(u16::from_be_bytes([*p.first()?, *p.get(1)?]));
    let charging = p.get(2)? & 0x80 != 0;
    Some(((mv != 0).then(|| interpolate(mv, &LI_MV, &LI_PCT)), charging))
}

/// HID++ 1.0, registre 0x0D.
pub fn register_0d(p: &[u8]) -> Option<Battery> {
    (p.len() >= 3 && (1..=100).contains(&p[0])).then(|| (Some(p[0]), (0x21..=0x26).contains(&p[2])))
}

/// HID++ 1.0, registre 0x07 : niveaux 1 à 7.
pub fn register_07(p: &[u8]) -> Option<Battery> {
    const LEVELS: [u8; 8] = [0, 5, 10, 20, 35, 50, 75, 100];
    (p.len() >= 2 && p[0] != 0).then(|| (Some(LEVELS[usize::from(p[0].min(7))]), (0x21..=0x26).contains(&p[1])))
}

fn kind_from_type(t: u8) -> Option<Kind> {
    Some(match t {
        0x00 | 0x02 => Kind::Keyboard,
        0x03..=0x05 => Kind::Mouse,
        0x08 => Kind::Headset,
        0x0B | 0x0C => Kind::Gamepad,
        _ => return None,
    })
}

/// Appareil physique d'une collection (deux récepteurs identiques restent distincts).
pub fn physical_key(path: &str) -> String {
    let mut parts = path.split('#').skip(1);
    let (Some(hardware), Some(instance), Some(_)) = (parts.next(), parts.next(), parts.next()) else { return String::new() };
    let hardware = match hardware.find("&col") {
        Some(at) => format!("{}{}", &hardware[..at], hardware.get(at + 6..).unwrap_or("")),
        None => hardware.to_string(),
    };
    let instance = instance.rfind('&').map_or(instance, |at| &instance[..at]);
    format!("{hardware}#{instance}")
}

struct Hidpp {
    short: Option<Dev>,
    long: Option<Dev>,
}

impl Hidpp {
    fn request(&self, device: u8, b2: u8, b3: u8, params: &[u8], force_short: bool, timeout_ms: u64) -> Option<Reply> {
        let (channel, len) = match (&self.long, &self.short) {
            (Some(long), _) if !force_short => (long, 20),
            (_, Some(short)) => (short, 7),
            _ => return None,
        };
        let mut message = vec![0u8; len];
        message[..4].copy_from_slice(&[if len == 20 { 0x11 } else { 0x10 }, device, b2, b3]);
        let n = params.len().min(len - 4);
        message[4..4 + n].copy_from_slice(&params[..n]);
        if !channel.write(&message) {
            return None;
        }
        let end = Instant::now() + Duration::from_millis(timeout_ms);
        while Instant::now() < end {
            for d in [&self.long, &self.short].into_iter().flatten() {
                if let Some(reply) = d.read(15).and_then(|r| parse_reply(&r, device, b2, b3)) {
                    return Some(reply);
                }
            }
        }
        None
    }

    fn call(&self, device: u8, feature: u8, function: u8, params: &[u8]) -> Option<Vec<u8>> {
        match self.request(device, feature, (function << 4) | SW_ID, params, false, 500)? {
            Reply::Ok(p) => Some(p),
            Reply::Error(_) => None,
        }
    }

    fn feature(&self, device: u8, id: u16) -> Option<u8> {
        self.call(device, 0x00, 0, &id.to_be_bytes())?.first().copied().filter(|&f| f != 0)
    }

    fn name(&self, device: u8) -> (Option<String>, Option<Kind>) {
        let Some(f) = self.feature(device, 0x0005) else { return (None, None) };
        let mut name = String::new();
        let count = self.call(device, f, 0, &[]).and_then(|c| c.first().copied()).unwrap_or(0).min(64) as usize;
        while name.len() < count {
            let before = name.len();
            let Some(p) = self.call(device, f, 1, &[before as u8]) else { break };
            name.extend(p.iter().take(count - before).take_while(|&&b| (0x20..0x7F).contains(&b)).map(|&b| b as char));
            if name.len() == before {
                break;
            }
        }
        let kind = self.call(device, f, 2, &[]).and_then(|t| t.first().copied()).and_then(kind_from_type);
        let name = (!name.is_empty()).then(|| {
            if ["Logitech", "G ", "MX"].iter().any(|p| name.starts_with(p)) { name } else { format!("Logitech {name}") }
        });
        (name, kind)
    }

    /// `all` faux : seule la mesure 0x1F20 est lue, le reste appartient au module principal.
    fn battery20(&self, device: u8, pid: u16, all: bool) -> Option<Battery> {
        if all {
            for (id, function, parse) in [(0x1004, 1, unified as fn(&[u8]) -> Option<Battery>), (0x1000, 0, level_status), (0x1001, 0, voltage)] {
                if let Some(b) = self.feature(device, id).and_then(|f| self.call(device, f, function, &[])).and_then(|p| parse(&p)) {
                    return Some(b);
                }
            }
        } else if [0x1004, 0x1000, 0x1001].iter().any(|&id| self.feature(device, id).is_some()) {
            return None;
        }
        let p = self.call(device, self.feature(device, 0x1F20)?, 0, &[])?;
        let mv = i64::from(u16::from_be_bytes([*p.first()?, *p.get(1)?]));
        Some(((mv != 0).then(|| headset_percent(pid, mv)), *p.get(2)? == 0x03))
    }

    fn battery10(&self, device: u8) -> Option<Battery> {
        let ask = |register| match self.request(device, 0x81, register, &[], true, 500) {
            Some(Reply::Ok(p)) => Some(p),
            _ => None,
        };
        ask(0x0D).and_then(|p| register_0d(&p)).or_else(|| ask(0x07).and_then(|p| register_07(&p)))
    }
}

fn poll_device(ctx: &mut Context, pid: u16, short: Option<usize>, long: Option<usize>, suffix: &str) -> bool {
    let Some(first) = long.or(short) else { return false };
    let hidpp = Hidpp {
        short: short.and_then(|i| Dev::open(&ctx.colls[i])),
        long: long.and_then(|i| Dev::open(&ctx.colls[i])),
    };
    if hidpp.short.is_none() && hidpp.long.is_none() {
        return false;
    }
    let (product, path) = (ctx.colls[first].product.clone(), ctx.colls[first].path_text.clone());
    let passthrough = pid == PASSTHROUGH;
    let bluetooth = path.contains("{00001812-") || path.contains("{00001124-");
    let ping = |device| hidpp.request(device, 0x00, 0x10 | SW_ID, &[0, 0, 0x5A], false, 400);
    let modern = |reply: &Option<Reply>| matches!(reply, Some(Reply::Ok(p)) if p.first().is_some_and(|&v| v >= 2));
    let indices: Vec<u8> = if passthrough {
        vec![1]
    } else if bluetooth || (!product.to_lowercase().contains("receiver") && modern(&ping(0xFF))) {
        vec![0xFF]
    } else {
        (1..=6).collect()
    };
    let mut any = false;
    for device in indices {
        if ctx.out_of_time() {
            break;
        }
        let mut name = if device == 0xFF && !product.is_empty() { product.clone() } else { "Logitech wireless device".into() };
        let mut kind = passthrough.then_some(Kind::Mouse);
        let reply = ping(device);
        let battery = if modern(&reply) {
            let (read_name, read_kind) = hidpp.name(device);
            let battery = hidpp.battery20(device, pid, passthrough);
            if battery.is_some() {
                name = read_name.unwrap_or(name);
                kind = read_kind.or(kind);
            }
            battery
        } else if reply == Some(Reply::Error(0x01)) {
            hidpp.battery10(device) // « sous-identifiant invalide » : appareil HID++ 1.0
        } else {
            None
        };
        let Some((percent, charging)) = battery else { continue };
        let lower = name.to_lowercase();
        let kind = kind.unwrap_or_else(|| {
            if lower.contains("headset") { Kind::Headset } else if lower.contains("keyboard") { Kind::Keyboard } else { Kind::Mouse }
        });
        let mut s = Status::new(format!("logix:{pid:04X}{suffix}:{device}"), name, kind);
        (s.percent, s.charging) = (percent, charging);
        ctx.out.push(s);
        any = true;
    }
    any
}

pub fn poll(ctx: &mut Context) {
    for pid in ctx.products(VID) {
        let passthrough = pid == PASSTHROUGH;
        let shorts = ctx.find(VID, pid, |c| {
            c.output_len == 7
                && ((c.usage_page == 0xFF00 && c.usage == 1) || (passthrough && c.usage_page == 0xFF43 && c.usage == 0x0301))
        });
        let longs = ctx.find(VID, pid, |c| {
            c.output_len == 20
                && ((c.usage_page == 0xFF00 && c.usage == 2)
                    || (c.usage_page == 0xFF43 && (c.usage == 0x0202 || (passthrough && c.usage == 0x0302))))
        });
        let mut groups: Vec<String> = shorts.iter().chain(&longs).map(|&i| physical_key(&ctx.colls[i].path_text)).collect();
        groups.sort();
        groups.dedup();
        let mut any = false;
        for (n, group) in groups.iter().enumerate() {
            let pick = |list: &[usize], colls: &[Info]| list.iter().copied().find(|&i| &physical_key(&colls[i].path_text) == group);
            let (short, long) = (pick(&shorts, &ctx.colls), pick(&longs, &ctx.colls));
            let suffix = if n > 0 { format!("#{}", n + 1) } else { String::new() };
            any |= poll_device(ctx, pid, short, long, &suffix);
        }
        if any {
            ctx.claim(VID, pid);
        }
    }
}

// ---------------------------------------------------------------- G435 (récepteur 0ADF)

/// CRC-16/XMODEM.
pub fn crc16(data: &[u8]) -> u16 {
    data.iter().fold(0u16, |mut crc, &b| {
        crc ^= u16::from(b) << 8;
        for _ in 0..8 {
            crc = if crc & 0x8000 != 0 { (crc << 1) ^ 0x1021 } else { crc << 1 };
        }
        crc
    })
}

/// Trame de 64 octets : commande, longueur, CRC (sur la trame entière, champ à zéro), corps.
pub fn frame(command: u8, body: &[u8]) -> [u8; 64] {
    let mut f = [0u8; 64];
    f[0] = command;
    f[1] = body.len() as u8;
    let n = body.len().min(60);
    f[4..4 + n].copy_from_slice(&body[..n]);
    let crc = crc16(&f).to_le_bytes();
    (f[2], f[3]) = (crc[0], crc[1]);
    f
}

/// Tension de la réponse GET_BATTERY (motif 00 07 00 06 seq 00 mV).
pub fn millivolts(data: &[u8]) -> Option<u16> {
    data.windows(8)
        .filter(|w| w[0] == 0 && w[1] == 0x07 && w[2] == 0 && w[3] == 0x06 && w[5] == 0)
        .map(|w| u16::from_le_bytes([w[6], w[7]]))
        .find(|mv| (2500..=4500).contains(mv))
}

pub fn g435_percent(mv: i64) -> u8 {
    const V: [i64; 16] = [4200, 4150, 4100, 4050, 4000, 3950, 3900, 3850, 3800, 3750, 3700, 3650, 3600, 3550, 3500, 3400];
    const P: [i64; 16] = [100, 95, 90, 85, 80, 75, 70, 62, 55, 45, 35, 25, 15, 10, 5, 0];
    interpolate(mv, &V, &P)
}

fn transact(d: &Dev, command: u8, body: &[u8], wait_ms: u64) -> Option<u16> {
    let mut report = [0u8; 65]; // identifiant de rapport 0
    report[1..].copy_from_slice(&frame(command, body));
    d.flush();
    d.query(&report, wait_ms, |r| millivolts(r).is_some()).and_then(|r| millivolts(&r))
}

/// La session est toujours refermée. Le « mode distant » ne choisit que la destination de la requête et ne persiste pas.
fn g435_session(d: &Dev) -> Option<u16> {
    const INIT: [u8; 19] = [0x50, 0x41, 0x06, 0x01, 0x0D, 0x00, 0x25, 0x34, 0x12, 0x5A, 0x5A, 0x01, 0, 0, 0, 0xF0, 0, 0, 0];
    transact(d, 0x84, &[0x01], 50);
    transact(d, 0x85, &[0x01], 50);
    transact(d, 0x80, &INIT, 450);
    transact(d, 0x80, &[0x50, 0x41, 0x0E, 2, 1, 0xE0], 120);
    transact(d, 0x80, &[0x50, 0x41, 0x0E, 3, 2, 0xE1, 0x01], 120);
    transact(d, 0x80, &[0x50, 0x41, 0x0E, 4, 1, 0xE0], 120);
    let level = transact(d, 0x80, &[0x50, 0x41, 0x06, 5, 1, 0x00, 0x31], 250);
    transact(d, 0x85, &[0x02], 50);
    level
}

pub fn poll_g435(ctx: &mut Context) {
    const PID: u16 = 0x0ADF;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let fits = |c: &Info| c.usage_page == 0xFF00 && c.usage == 1 && c.input_len == 65 && c.output_len == 65;
    let Some(&i) = ctx.find(VID, PID, fits).first() else { return };
    let Some(d) = Dev::open(&ctx.colls[i]) else { return };
    let mut s = Status::new("g435:0ADF", "Logitech G435", Kind::Headset);
    match g435_session(&d) {
        Some(mv) => s.percent = Some(g435_percent(i64::from(mv))),
        None => s.connected = false, // récepteur présent, casque éteint ou hors de portée
    }
    ctx.add(s, VID, PID);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hidpp_replies() {
        assert_eq!(parse_reply(&[0x11, 1, 4, 0x0D, 9, 8, 7], 1, 4, 0x0D), Some(Reply::Ok(vec![9, 8, 7])));
        assert_eq!(parse_reply(&[0x10, 1, 0x8F, 0, 0x1D, 1, 0], 1, 0, 0x1D), Some(Reply::Error(1)));
        assert_eq!(parse_reply(&[0x11, 2, 4, 0x0D, 9, 8, 7], 1, 4, 0x0D), None);
        assert_eq!(unified(&[0, 4, 0]), Some((Some(60), false)));
        assert_eq!(level_status(&[0, 0, 3]), Some((Some(100), true)));
        assert_eq!(voltage(&[0x10, 0x5A, 0x80]), Some((Some(100), true)));
        assert_eq!(register_07(&[7, 0x21]), Some((Some(100), true)));
        assert_eq!(register_0d(&[0, 0, 0]), None);
        assert_eq!(headset_percent(0x0A66, 4200), 100);
    }

    #[test]
    fn physical_devices() {
        let short = r"\\?\hid#vid_046d&pid_c539&mi_02&col01#8&abc&0&0000#{guid}";
        let long = r"\\?\hid#vid_046d&pid_c539&mi_02&col02#8&abc&0&0001#{guid}";
        assert_eq!(physical_key(short), physical_key(long));
        assert_eq!(physical_key("autre"), "");
    }

    #[test]
    fn g435() {
        assert_eq!(crc16(b"123456789"), 0x31C3);
        let f = frame(0x84, &[1]);
        let mut zeroed = f;
        (zeroed[2], zeroed[3]) = (0, 0);
        assert_eq!(u16::from_le_bytes([f[2], f[3]]), crc16(&zeroed));
        assert_eq!(millivolts(&[0, 0x07, 0, 0x06, 5, 0, 0x68, 0x10]), Some(4200));
        assert_eq!(g435_percent(4200), 100);
        assert_eq!(g435_percent(3400), 0);
    }
}
