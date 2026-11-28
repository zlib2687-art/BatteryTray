//! Manettes 8BitDo et GameSir. Dérivé de BatteryHub (GPL-3.0), src/drivers/eightbitdo.cpp, gamesir.cpp et
//! gamesir_sapjoy.cpp (sources : SDL, 8bitdo-ultimate2-linux, GamesirBatteryTray, cyclone2-linux). Lecture seule.

use super::{Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};
use std::cell::Cell;
use std::time::{Duration, Instant};

const EIGHT: u16 = 0x2DC8;
const GAMESIR: u16 = 0x3537;

fn pad(key: String, name: impl Into<String>) -> Status {
    Status::new(key, name, Kind::Gamepad)
}

fn eight_name(pid: u16) -> Option<&'static str> {
    Some(match pid {
        0x310B | 0x6013 => "8BitDo Ultimate 2 Wireless",
        0x6012 => "8BitDo Ultimate 2",
        0x202F | 0x202E => "8BitDo Ultimate 3-mode",
        0x301B | 0x301A | 0x310A => "8BitDo Ultimate 2C Wireless",
        0x3012 | 0x3013 | 0x3109 => "8BitDo Ultimate 2.4G",
        0x6007 => "8BitDo Ultimate Bluetooth",
        0x6009 => "8BitDo Pro 3",
        0x6003 | 0x6006 => "8BitDo Pro 2",
        0x6001 | 0x6101 => "8BitDo SN30 Pro",
        0x6000 | 0x6100 => "8BitDo SF30 Pro",
        _ => return None,
    })
}

fn is_ultimate2(pid: u16) -> bool {
    matches!(pid, 0x310B | 0x6012 | 0x6013)
}

fn eight_key(pid: u16) -> String {
    if is_ultimate2(pid) { "8bitdo:ultimate2".into() } else { format!("8bitdo:{pid:04X}") }
}

/// Rapport D-input : octet 14, bits 0..6 pourcentage, bit 7 en charge.
pub fn dinput(r: &[u8], min_len: usize) -> Option<(u8, bool)> {
    if r.len() < min_len.max(15) || (r[0] != 0x01 && r[0] != 0x04) || r[14] & 0x7F > 100 {
        return None;
    }
    let percent = r[14] & 0x7F;
    Some((percent, r[14] & 0x80 != 0 && percent < 100))
}

pub fn vendor_battery(r: &[u8]) -> Option<(u8, bool)> {
    let value = *r.get(18)?;
    let percent = value & 0x7F;
    (1..=100).contains(&percent).then(|| (percent, value & 0x80 != 0 && percent < 100))
}

thread_local!(static LAST_ULTIMATE2: Cell<Option<u8>> = const { Cell::new(None) });

fn ask_eight(d: &Dev, request: &[u8], low: u8, high: u8) -> Option<Vec<u8>> {
    d.flush();
    d.query(request, 300, |r| r.len() >= 19 && r[0] == 0x02 && r[1] == 0x04 && r[4] == low && r[5] == high)
}

fn eight_vendor(c: &Info) -> Option<Status> {
    let d = Dev::open(c)?;
    let pid = c.pid;
    let product = c.product.trim();
    let name = eight_name(pid).map_or_else(
        || if product.is_empty() { "8BitDo Wireless Controller".into() } else { product.to_string() },
        String::from,
    );
    let mut s = pad(eight_key(pid), name);
    let linked = ask_eight(&d, &[0x81, 0x04, 0x20, 0x01], 0x20, 0x01).map(|r| r[18] != 0);
    if linked != Some(false) {
        if let Some((percent, charging)) = ask_eight(&d, &[0x81, 0x04, 0x21, 0x01, 0, 0, 0x06], 0x21, 0x01).and_then(|r| vendor_battery(&r)) {
            (s.percent, s.charging) = (Some(percent), charging);
            if is_ultimate2(pid) {
                LAST_ULTIMATE2.set(Some(percent));
            }
            return Some(s);
        }
    }
    if let Some(info) = ask_eight(&d, &[0x81, 0x04, 0x00, 0x01], 0x00, 0x01).filter(|r| r.len() >= 24) {
        let internal = u16::from_le_bytes([info[22], info[23]]);
        if internal != 0x6013 && internal != 0x3109 {
            s.charging = true; // sur câble
            s.percent = LAST_ULTIMATE2.get().filter(|_| is_ultimate2(pid));
            return Some(s);
        }
        s.connected = false; // posée sur sa base : radio coupée
        return Some(s);
    }
    (pid == 0x6013 || pid == 0x3109).then(|| {
        s.connected = false;
        s
    })
}

fn eight_dinput(c: &Info) -> Option<Status> {
    let d = Dev::open(c)?;
    let pid = c.pid;
    if matches!(pid, 0x6009 | 0x6003 | 0x6006 | 0x6001 | 0x6101 | 0x6000 | 0x6100) && c.feature_len > 0 {
        hid::feature_read(c, 0x06, c.feature_len); // passe la manette en rapports complets
    }
    let min_len = if pid == 0x6012 { 34 } else { 15 };
    let end = Instant::now() + Duration::from_millis(600);
    let mut found = None;
    while found.is_none() && Instant::now() < end {
        found = d.read(100).and_then(|r| dinput(&r, min_len));
    }
    let (percent, charging) = found?;
    if is_ultimate2(pid) && percent > 0 {
        LAST_ULTIMATE2.set(Some(percent));
    }
    let mut s = pad(eight_key(pid), eight_name(pid)?);
    (s.percent, s.charging) = (Some(percent), charging);
    Some(s)
}

pub fn poll_eightbitdo(ctx: &mut Context) {
    for pid in ctx.products(EIGHT) {
        let vendor = |c: &Info| {
            (c.usage_page == 0xFF7A || c.usage_page == 0xFFA0) && c.usage == 1 && c.input_len >= 64 && c.output_len >= 64
        };
        for i in ctx.find(EIGHT, pid, vendor) {
            if let Some(s) = eight_vendor(&ctx.colls[i]) {
                ctx.add(s, EIGHT, pid);
                break;
            }
        }
        if ctx.is_claimed(EIGHT, pid) || eight_name(pid).is_none() {
            continue;
        }
        for i in ctx.find(EIGHT, pid, |c| c.usage_page == 1 && (c.usage == 5 || c.usage == 4) && c.input_len >= 15) {
            if ctx.out_of_time() {
                return;
            }
            if let Some(s) = eight_dinput(&ctx.colls[i]) {
                ctx.add(s, EIGHT, pid);
                break;
            }
        }
    }
}

/// Nova : octet 18, 0..100 %, FF = en charge sans pourcentage.
pub fn nova(r: &[u8]) -> Option<(Option<u8>, bool)> {
    if r.len() < 19 || r[0] != 0x01 {
        return None;
    }
    match r[18] {
        0xFF => Some((None, true)),
        value => (value <= 100).then_some((Some(value), false)),
    }
}

/// Cyclone 2 : [35] câble, [36] pourcentage.
pub fn cyclone(r: &[u8]) -> Option<(u8, bool)> {
    (r.len() >= 37 && r[0] == 0x12 && r[36] <= 100).then(|| (r[36], r[35] == 1))
}

/// SAPJoy « infos appareil » : (identifiant matériel, pourcentage, en charge).
pub fn sapjoy(r: &[u8]) -> Option<(u32, Option<u8>, bool)> {
    if r.len() < 15 || r[0] != 0x06 || r[2] != 0x0A || r[3] != 0x01 || r[1] < 14 || usize::from(r[1]) + 1 > r.len() {
        return None;
    }
    // l'adresse n'est gardée que sous forme d'empreinte, pour distinguer deux manettes
    let address = &r[7..13];
    let blank = address.iter().all(|&b| b == 0) || address.iter().all(|&b| b == 0xFF);
    let print = if blank { 0 } else { address.iter().fold(2_166_136_261u32, |h, &b| (h ^ u32::from(b)).wrapping_mul(16_777_619)) };
    Some(if r[13] == 0xFF { (print, None, true) } else { (print, Some(r[13].min(100)), false) })
}

pub fn poll_gamesir(ctx: &mut Context) {
    for pid in ctx.products(GAMESIR) {
        if matches!(pid, 0x1098 | 0x100F | 0x1040) {
            for i in ctx.find(GAMESIR, pid, |c| c.usage_page == 0xFF7A && c.output_len >= 3) {
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                d.flush();
                let reply = d.query(&[0x00, 0x01, 0x01], 800, |r| r.len() >= 19 && r[0] == 0x01);
                let Some((percent, charging)) = reply.and_then(|r| nova(&r)) else { continue };
                let mut s = pad(format!("gamesir:{pid:04X}"), "GameSir Nova");
                (s.percent, s.charging) = (percent, charging);
                ctx.add(s, GAMESIR, pid);
                break;
            }
        } else if pid == 0x100B {
            for i in ctx.find(GAMESIR, pid, |c| c.usage_page == 0xFFF0 && c.usage == 0x40 && c.output_len >= 3 && c.input_len >= 37) {
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                d.flush();
                let reply = d.query(&[0x0F, 0x03], 1500, |r| cyclone(r).is_some());
                let Some((percent, charging)) = reply.and_then(|r| cyclone(&r)) else { continue };
                let mut s = pad(format!("gamesir:{pid:04X}"), "GameSir Cyclone 2");
                (s.percent, s.charging) = (Some(percent), charging);
                ctx.add(s, GAMESIR, pid);
                break;
            }
        }
    }
    for pid in [0x100E, 0x103E, 0x1050] {
        if ctx.is_claimed(GAMESIR, pid) {
            continue;
        }
        let fits = |c: &Info| c.usage_page == 0xFFA0 && c.output_len == 32 && c.input_len == 64 && c.feature_len == 0;
        for i in ctx.find(GAMESIR, pid, fits) {
            let product = ctx.colls[i].product.clone();
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let mut request = [0u8; 32];
            (request[0], request[1], request[2], request[3]) = (0x07, 0x03, 0x0A, 0x01);
            d.flush();
            let reply = d.query(&request, 900, |r| sapjoy(r).is_some());
            let Some((print, percent, charging)) = reply.and_then(|r| sapjoy(&r)) else { continue };
            let key = if print == 0 { format!("gamesir:{pid:04X}") } else { format!("gamesir:sapjoy:{print:08X}") };
            if ctx.out.iter().any(|o| o.key == key) {
                ctx.claim(GAMESIR, pid);
            } else {
                let name = if product.is_empty() { "GameSir controller".into() } else { product };
                let mut s = pad(key, name);
                (s.percent, s.charging) = (percent, charging);
                ctx.add(s, GAMESIR, pid);
            }
            break;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames() {
        let mut r = [0u8; 40];
        (r[0], r[14]) = (0x01, 0x80 | 64);
        assert_eq!(dinput(&r, 34), Some((64, true)));
        r[18] = 0xFF;
        assert_eq!(nova(&r), Some((None, true)));
        r[18] = 55;
        assert_eq!(nova(&r), Some((Some(55), false)));
        assert_eq!(vendor_battery(&r), Some((55, false)));
        (r[0], r[35], r[36]) = (0x12, 1, 88);
        assert_eq!(cyclone(&r), Some((88, true)));
        let mut s = [0u8; 15];
        (s[0], s[1], s[2], s[3], s[13]) = (0x06, 14, 0x0A, 0x01, 96);
        assert_eq!(sapjoy(&s), Some((0, Some(96), false)));
    }
}
