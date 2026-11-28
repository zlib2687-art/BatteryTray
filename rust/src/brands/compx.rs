//! Souris au protocole Compx / ATK / Sonix : Kysona, Pulsar, ATK, VXE, Redragon, HECATE et variantes. Dérivé de
//! BatteryHub (GPL-3.0), src/drivers/kysona.cpp, pulsar.cpp et atk_bitmouse.cpp (sources : hid-kysona.c, PulsarBattery,
//! pulsar-mouse-linux, HaloBattery, mouse_tray, entre autres). Lecture seule.

use super::corsair::{compx_checksum, compx_request};
use super::mice::gwolves_name;
use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};

const ATK: u16 = 0x373B;

fn mouse(key: String, name: impl Into<String>) -> Status {
    Status::new(key, name, Kind::Mouse)
}

// ---------------------------------------------------------------- rapport 08 de 17 octets (Kysona et cousins)

/// Récepteurs de claviers et de casques qui accepteraient la taille du rapport : jamais de trames de souris.
pub fn should_probe(vid: u16, pid: u16) -> bool {
    let atk_other = vid == ATK && [0x11CA, 0x11CB, 0x1203, 0x1204, 0x1178].contains(&pid);
    let keyboard = vid == 0x3554 && (pid == 0xFA09 || pid == 0xFB40);
    let hecate_headset = vid == 0x35BB && !(0xD3FA..=0xD3FD).contains(&pid);
    !(atk_other || keyboard || hecate_headset)
}

fn is_redragon(vid: u16, pid: u16) -> bool {
    vid == 0x3554 && [0xF55D, 0xF5D5, 0xF55E].contains(&pid)
}

fn is_vxe_r1(vid: u16, pid: u16) -> bool {
    vid == 0x3554 && [0xF58A, 0xF58C, 0xF58E, 0xF58F, 0xF505, 0xF506].contains(&pid)
}

/// La même clé sur câble et sur récepteur.
fn key_pid(vid: u16, pid: u16) -> u16 {
    match (vid, pid) {
        (0x3554, 0xF58C) => 0xF58A,
        (0x3554, 0xF58F) => 0xF58E,
        (0x3554, 0xF506) => 0xF505,
        _ => pid,
    }
}

pub fn clean_name(product: &str, vid: u16, pid: u16) -> String {
    if vid == 0x33E4 {
        return gwolves_name(pid).into();
    }
    if is_redragon(vid, pid) {
        return "Redragon wireless mouse".into();
    }
    if is_vxe_r1(vid, pid) {
        return "VXE Dragonfly R1".into();
    }
    if vid == 0x3770 {
        return "Hitscan Hyperlight".into();
    }
    if vid == 0x35BB {
        return "HECATE mouse".into();
    }
    let mut name = product;
    for suffix in [" Dongle", " Receiver", " 2.4G", " Wireless Receiver"] {
        if name.len() > suffix.len() {
            name = name.strip_suffix(suffix).unwrap_or(name);
        }
    }
    match name {
        "" if vid == ATK => "ATK wireless mouse".into(),
        "" => "Wireless mouse".into(),
        other => other.into(),
    }
}

fn compx(d: &Dev, command: u8) -> Option<Vec<u8>> {
    d.flush();
    let valid = |r: &[u8]| r.len() >= 17 && r[0] == 0x08 && r[1] == command && r[16] == compx_checksum(r);
    d.query(&compx_request(command), 500, |r| r.len() < 17 || valid(r)).filter(|r| valid(r))
}

pub fn poll_kysona(ctx: &mut Context) {
    let mut names: Vec<String> = Vec::new();
    for vendor in [0x3554, ATK, 0x3770, 0x33E4, 0x3710, 0x35BB] {
        for pid in ctx.products(vendor) {
            if !should_probe(vendor, pid) {
                continue;
            }
            let mut found = ctx.find(vendor, pid, |c| {
                (c.output_len == 17 && c.input_len == 17) || (c.usage_page >= 0xFF00 && c.output_len >= 17 && c.input_len >= 17)
            });
            found.sort_by_key(|&i| !(ctx.colls[i].usage_page == 0xFF02 && ctx.colls[i].usage == 2));
            for i in found {
                if ctx.out_of_time() {
                    return;
                }
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                let online = compx(&d, 0x03).map(|r| r[6] != 0);
                let battery = if online != Some(false) { compx(&d, 0x04) } else { None };
                if online.is_none() && battery.is_none() {
                    continue;
                }
                if online == Some(false) {
                    ctx.claim(vendor, pid);
                    break;
                }
                let name = clean_name(&ctx.colls[i].product, vendor, pid);
                let charging = battery.as_ref().is_some_and(|b| b[7] != 0);
                // câble et récepteur branchés ensemble : une seule souris
                if names.contains(&name) {
                    ctx.claim(vendor, pid);
                    if charging {
                        ctx.out.iter_mut().filter(|o| o.name == name).for_each(|o| o.charging = true);
                    }
                    break;
                }
                let mut s = mouse(format!("kysona:{vendor:04X}:{:04X}", key_pid(vendor, pid)), name.clone());
                match battery {
                    Some(b) => (s.percent, s.charging) = (Some(b[6]).filter(|&p| p <= 100), charging),
                    None => s.connected = false,
                }
                names.push(name);
                ctx.add(s, vendor, pid);
                break;
            }
        }
    }
}

// ---------------------------------------------------------------- Pulsar à puce Sonix (3710)

pub fn pulsar_request() -> [u8; 65] {
    let mut r = [0u8; 65];
    (r[2], r[3], r[4]) = (0x08, 0x81, 0x01);
    let sum: u16 = r[1..=62].iter().fold(0u16, |s, &v| s.wrapping_add(u16::from(v)));
    let bytes = sum.to_le_bytes();
    (r[63], r[64]) = (bytes[0], bytes[1]);
    r
}

#[derive(Debug, PartialEq)]
pub enum Sonix {
    NotOurs,
    Battery(u8),
    /// Vrai : « souris non liée ou endormie ».
    Pending(bool),
    NoReading,
}

pub fn parse_pulsar(r: &[u8]) -> Sonix {
    if r.len() < 65 {
        return Sonix::NoReading;
    }
    let w = &r[1..];
    if w[1] != 0x08 || w[2] != 0x81 || w[3] != 0x01 {
        return Sonix::NotOurs;
    }
    match w[0] {
        0x01 if w[6] <= 100 => Sonix::Battery(w[6]),
        0x01 => Sonix::NoReading,
        state => Sonix::Pending(state == 0x05),
    }
}

/// `Some(Some(%))` : niveau ; `Some(None)` : souris éteinte ; `None` : pas de réponse.
fn pulsar_battery(c: &Info) -> Option<Option<u8>> {
    let mut offline = false;
    for _ in 0..3 {
        if !hid::feature_write(c, &pulsar_request()) {
            return None;
        }
        for _ in 0..6 {
            pause(50);
            match parse_pulsar(&hid::feature_read(c, 0, c.feature_len)?) {
                Sonix::NoReading => return None,
                Sonix::NotOurs => {}
                Sonix::Battery(p) => return Some(Some(p)),
                Sonix::Pending(gone) => offline = gone,
            }
        }
    }
    offline.then_some(None)
}

pub fn poll_pulsar(ctx: &mut Context) {
    const VID: u16 = 0x3710;
    for (pid, wired, name) in [(0x3402, true, "Pulsar X2 V3"), (0x3406, true, "Pulsar X2 V3 eS"), (0x5402, false, ""), (0x5403, false, "")] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let mut found = ctx.find(VID, pid, |c| c.feature_len >= 65);
        found.sort_by_key(|&i| -ctx.colls[i].iface);
        for i in found {
            let c = &ctx.colls[i];
            let Some(level) = pulsar_battery(c) else { continue };
            let generic = c.product.is_empty() || c.product.contains("Dongle") || c.product.contains("Receiver");
            let name = match (wired, generic) {
                (true, _) => name.to_string(),
                (false, true) => "Pulsar wireless mouse".into(),
                (false, false) => c.product.clone(),
            };
            let mut s = mouse(format!("pulsar:{pid:04X}"), name);
            s.charging = wired;
            s.percent = level.filter(|&p| p > 0 || wired);
            s.connected = s.percent.is_some();
            ctx.add(s, VID, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- ATK BITMOUSE (avant Kysona)

#[derive(Clone, Copy, PartialEq)]
pub enum Link {
    NearLink,
    ZeroCable,
    ZeroReceiver,
}

pub fn atk_query(command: u8, link: Link) -> [u8; 64] {
    let mut r = [0u8; 64];
    (r[0], r[2], r[3], r[6], r[7]) = (0x08, 0x72, 0x02, command, 0x01);
    r[5] = u8::from(link != Link::ZeroCable);
    r[1] = if link == Link::NearLink { r[2..].iter().fold(0u8, |s, &v| s.wrapping_add(v)) } else { 0x7D };
    r
}

/// `None` : autre réponse ; `Some(None)` : erreur.
pub fn parse_atk(r: &[u8], command: u8, link: Link) -> Option<Option<u8>> {
    let zero = link != Link::NearLink;
    if r.len() < 8 || r[0] != 0x08 || r[1] != 0x72 || r[5] != command || (zero && r[6] != 0x01) {
        return None;
    }
    let failed = if zero { r[2] != 0 } else { r[2] == 0xFF };
    Some((!failed).then_some(r[7]))
}

/// Récepteur 8K Nano : batterie bit 7 = en charge ; liaison 0 = aucune souris.
pub fn parse_nano(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 10 || r[0] != 0x82 || r[9] == 0 {
        return None;
    }
    (1..=100).contains(&(r[8] & 0x7F)).then(|| (r[8] & 0x7F, r[8] & 0x80 != 0))
}

fn atk_value(d: &Dev, command: u8, link: Link) -> Option<u8> {
    d.flush();
    let timeout = if link == Link::NearLink { 700 } else { 500 };
    d.query(&atk_query(command, link), timeout, |r| parse_atk(r, command, link).is_some())
        .and_then(|r| parse_atk(&r, command, link))
        .flatten()
}

pub fn poll_atk(ctx: &mut Context) {
    let zero = |c: &Info| c.usage_page == 0xFF05 && c.usage == 1 && c.output_len == 64 && c.input_len == 64;
    let mut reported = false;
    for pid in [0x1154, 0x124F, 0x1155] {
        if ctx.is_claimed(ATK, pid) {
            continue;
        }
        for i in ctx.find(ATK, pid, zero) {
            ctx.claim(ATK, pid); // garde les trames Kysona hors de cette collection, même endormie
            if reported {
                break;
            }
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let wired = pid == 0x1154;
            let link = if wired { Link::ZeroCable } else { Link::ZeroReceiver };
            let Some(level) = atk_value(&d, 0x07, link).filter(|&v| v <= 100) else { continue };
            let mut s = mouse("atkzero:1154".into(), "ATK Zero");
            s.percent = Some(level);
            s.charging = wired && atk_value(&d, 0x08, link) == Some(0x02);
            ctx.out.push(s);
            reported = true;
            break;
        }
    }

    const NANO_8K: u16 = 0x1178;
    if !ctx.is_claimed(ATK, NANO_8K) {
        for i in ctx.find(ATK, NANO_8K, |c| c.usage_page == 0xFF10 && c.usage == 1 && c.feature_len >= 9 && c.input_len >= 11) {
            let c = &ctx.colls[i];
            let Some(d) = Dev::open(c) else { continue };
            d.flush();
            if !hid::feature_write(c, &[0x00, 0x82]) {
                continue;
            }
            let Some(reply) = d.wait_for(700, |r| r.len() >= 10 && r[0] == 0x82) else { continue };
            let level = parse_nano(&reply);
            if let Some((percent, charging)) = level {
                let mut s = mouse("atknano8k:1178".into(), "ATK 8K Nano mouse");
                (s.percent, s.charging) = (Some(percent), charging);
                ctx.out.push(s);
            }
            ctx.claim(ATK, NANO_8K);
            break;
        }
    }

    const NK: u16 = 0x1216;
    if ctx.is_claimed(ATK, NK) {
        return;
    }
    for i in ctx.find(ATK, NK, |c| c.usage_page == 0xFF05 && c.usage == 1 && c.output_len >= 64 && c.input_len >= 8) {
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        let Some(level) = atk_value(&d, 0x07, Link::NearLink).filter(|v| (1..=100).contains(v)) else { continue };
        let mut s = mouse("atknk:1216".into(), "ATK / VXE NK mouse");
        s.percent = Some(level);
        s.charging = atk_value(&d, 0x0F, Link::NearLink) == Some(1);
        ctx.add(s, ATK, NK);
        break;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn probing_and_names() {
        assert!(should_probe(0x3554, 0xF58A));
        assert!(!should_probe(0x3554, 0xFA09));
        assert!(!should_probe(0x35BB, 0x1234));
        assert!(!should_probe(ATK, 0x1178));
        assert_eq!(clean_name("VXE NordicMouse 1K Dongle", 0x3554, 0xF58A), "VXE Dragonfly R1");
        assert_eq!(clean_name("Kysona M600 Dongle", 0x3554, 1), "Kysona M600");
        assert_eq!(clean_name("", ATK, 1), "ATK wireless mouse");
    }

    #[test]
    fn frames() {
        let r = pulsar_request();
        assert_eq!(u16::from_le_bytes([r[63], r[64]]), 0x08 + 0x81 + 0x01);
        let mut reply = [0u8; 65];
        (reply[1], reply[2], reply[3], reply[4], reply[7]) = (1, 8, 0x81, 1, 66);
        assert_eq!(parse_pulsar(&reply), Sonix::Battery(66));
        reply[1] = 5;
        assert_eq!(parse_pulsar(&reply), Sonix::Pending(true));

        assert_eq!(atk_query(7, Link::ZeroReceiver)[1], 0x7D);
        assert_eq!(parse_atk(&[8, 0x72, 0, 0, 0, 7, 1, 90], 7, Link::ZeroCable), Some(Some(90)));
        assert_eq!(parse_atk(&[8, 0x72, 0xFF, 0, 0, 7, 1, 90], 7, Link::NearLink), Some(None));
        assert_eq!(parse_atk(&[8, 0x72, 0, 0, 0, 9, 1, 90], 7, Link::NearLink), None);
        assert_eq!(parse_nano(&[0x82, 0, 0, 0, 0, 0, 0, 0, 0x80 | 45, 1]), Some((45, true)));
        assert_eq!(parse_nano(&[0x82, 0, 0, 0, 0, 0, 0, 0, 45, 0]), None);
    }
}
