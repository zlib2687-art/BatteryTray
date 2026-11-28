//! Keychron (VID 3434) : souris, Nape Pro et claviers sur récepteur Link. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/keychron.cpp (sources : launcher.keychron.com, nape_pro, KeychronNapeBatteryWidget, NapeBar). Lecture seule.

use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid;

const VID: u16 = 0x3434;
const LINK_KM: u16 = 0xD026;
const NAPE_USB: u16 = 0x0440;
const ULTRA_8K: u16 = 0xD028;

/// (pourcentage, en charge)
pub type Battery = (u8, bool);

/// Requête des souris : somme (octet 63) = 0xA1 − somme des octets 0..62.
pub fn mouse_query(command: u8, sub: u8) -> [u8; 64] {
    let mut p = [0u8; 64];
    (p[0], p[2], p[3]) = (command, 0x81, sub);
    p[63] = 0xA1u8.wrapping_sub(p[..63].iter().fold(0u8, |s, &v| s.wrapping_add(v)));
    p
}

fn word(r: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([r[at], r[at + 1]])
}

pub fn classic(r: &[u8]) -> Option<Battery> {
    (r.len() >= 13 && word(r, 6) == VID && r[11] <= 100).then(|| (r[11], r[10] != 0))
}

pub fn eight_k(r: &[u8]) -> Option<Battery> {
    (r.len() >= 13 && r[10] <= 3 && r[11] <= 100).then(|| (r[11], r[10] == 1))
}

pub fn eight_k_cable(r: &[u8]) -> Option<Battery> {
    (r.len() >= 13 && r[10] <= 3 && (1..=100).contains(&r[11]) && (1..=8).contains(&r[5])).then(|| (r[11], r[10] == 1))
}

pub fn four_k(r: &[u8]) -> Option<Battery> {
    (r.len() >= 7 && (1..=100).contains(&r[6])).then(|| (r[6], r[5] != 0))
}

/// Anciennes souris : (produit, pourcentage, en charge).
pub fn legacy(r: &[u8]) -> Option<(u16, u8, bool)> {
    let ok = r.len() >= 13 && r[0] == 0x51 && r[1] == 0x06 && r[2] == 0x01 && word(r, 4) == VID && r[11] <= 100;
    ok.then(|| (word(r, 6), r[11], r[12] == 0x01))
}

fn legacy_name(pid: u16) -> &'static str {
    match pid {
        0xD032 | 0xD033 => "Keychron M3",
        0xD035 => "Keychron M1",
        0xD036 | 0xD03E => "Keychron M3 mini",
        0xD03B => "Keychron M2",
        0xD03D => "Keychron M2 mini",
        0xD03F => "Keychron M6",
        0xD044 => "Keychron M7",
        _ => "Keychron mouse",
    }
}

fn clean_name(name: &str) -> String {
    if name.is_empty() || name.contains("Link") || name.contains("Receiver") { "Keychron mouse".into() } else { name.into() }
}

/// Premier clavier connecté derrière un récepteur Link (0 : aucun).
pub fn first_keyboard(r: &[u8]) -> u16 {
    let slots = r.get(1).map_or(0, |&n| usize::from(n.min(5)));
    (0..slots)
        .map(|i| 2 + 5 * i)
        .filter(|&o| o + 4 < r.len())
        .find(|&o| word(r, o) == VID && r[o + 4] != 0)
        .map_or(0, |o| word(r, o + 2))
}

pub fn nape_connected(r: &[u8]) -> bool {
    r.len() >= 17 && [2, 7, 12].iter().any(|&o| u16::from_be_bytes([r[o + 2], r[o + 3]]) == 0x4004 && r[o + 4] == 1)
}

pub fn ultra_connected(r: &[u8]) -> bool {
    [3, 8, 13].iter().any(|&o| o + 4 < r.len() && (r[o] | r[o + 1]) != 0 && r[o + 4] != 0)
}

pub fn ultra_power(r: &[u8]) -> Option<Battery> {
    let value = *r.get(20)?;
    (1..=100).contains(&(value & 0x7F)).then(|| (value & 0x7F, value & 0x80 != 0))
}

fn ask_mouse(d: &Dev, command: u8, sub: u8) -> Option<Vec<u8>> {
    let mut request = [0u8; 65];
    request[1..].copy_from_slice(&mouse_query(command, sub));
    d.flush();
    d.query(&request, 600, |r| r.len() < 13 || (r[0] == command && r[3] == sub)).filter(|r| r.len() >= 13)
}

fn ask_raw(d: &Dev, command: &[u8], timeout: u64) -> Option<Vec<u8>> {
    let mut request = [0u8; 33];
    request[1..=command.len()].copy_from_slice(command);
    d.flush();
    d.query(&request, timeout, |r| r.len() >= command.len() + 2 && r.starts_with(command))
}

fn is_raw(c: &hid::Info) -> bool {
    c.usage_page == 0xFF60 && c.usage == 0x61 && c.output_len == 33
}

fn status(key: String, name: impl Into<String>, kind: Kind, battery: Battery) -> Status {
    let mut s = Status::new(key, name, kind);
    (s.percent, s.charging) = (Some(battery.0), battery.1);
    s
}

fn poll_nape(ctx: &mut Context) {
    for pid in [LINK_KM, NAPE_USB] {
        for i in ctx.find(VID, pid, is_raw) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            // sur le récepteur, on ne demande que si un Nape Pro y est connecté
            if pid == LINK_KM && !ask_raw(&d, &[0xB2], 600).is_some_and(|r| nape_connected(&r)) {
                continue;
            }
            let Some(r) = ask_raw(&d, &[0xA7, 0x31], 600).filter(|r| r.len() >= 4 && r[2] <= 100) else { continue };
            ctx.add(status("keychron:nape".into(), "Keychron Nape Pro", Kind::Mouse, (r[2], r[3] != 0)), VID, pid);
            return;
        }
    }
}

fn poll_legacy(ctx: &mut Context) {
    for receiver in [0xD030, 0xD031] {
        for i in ctx.find(VID, receiver, |c| c.usage_page == 0x8C && c.feature_len >= 16) {
            let c = &ctx.colls[i];
            if !hid::feature_write(c, &[0x51, 0x06]) {
                continue;
            }
            let found = (0..5).find_map(|_| {
                pause(40);
                hid::feature_read(c, 0x51, c.feature_len).and_then(|r| legacy(&r))
            });
            let Some((pid, percent, charging)) = found else { continue };
            ctx.add(status(format!("keychron:{pid:04X}"), legacy_name(pid), Kind::Mouse, (percent, charging)), VID, receiver);
            break;
        }
    }
}

fn poll_ultra(ctx: &mut Context) {
    let fits = |c: &hid::Info| c.usage_page == 0xFFC1 && c.usage == 1 && c.output_len >= 21 && c.input_len >= 21;
    for i in ctx.find(VID, ULTRA_8K, fits) {
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        let ask = |id: u8, command: u8| {
            d.flush();
            d.query(&[id, command], 600, |r| r.len() >= 21 && r[1] == command)
        };
        if !ask(0xB5, 0x03).is_some_and(|r| ultra_connected(&r)) {
            continue;
        }
        let Some(battery) = ask(0xB3, 0x06).and_then(|r| ultra_power(&r)) else { continue };
        let name = clean_name(&ctx.colls[i].product);
        ctx.add(status(format!("keychron:{ULTRA_8K:04X}"), name, Kind::Mouse, battery), VID, ULTRA_8K);
        return;
    }
}

fn poll_keyboard(ctx: &mut Context, pid: u16) {
    if pid == LINK_KM || pid == NAPE_USB {
        return;
    }
    for i in ctx.find(VID, pid, is_raw) {
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        let list = ask_raw(&d, &[0xB2], 300).filter(|r| r.len() >= 8);
        let (keyboard, reply) = match &list {
            Some(slots) => {
                let keyboard = first_keyboard(slots);
                if keyboard == 0 {
                    return;
                }
                // la première requête se perd souvent pendant le réveil
                (keyboard, ask_raw(&d, &[0xA4], 400).or_else(|| ask_raw(&d, &[0xA4], 400)))
            }
            None => (pid, ask_raw(&d, &[0xA4], 400)),
        };
        let Some(r) = reply else {
            if list.is_some() {
                return; // micrologiciel d'origine : pas de batterie
            }
            continue;
        };
        if r.len() < 5 || r[1] > 100 {
            return;
        }
        let name = if (0x0A50..=0x0A58).contains(&keyboard) { "Keychron K5 Max" } else { "Keychron keyboard" };
        ctx.add(status(format!("keychron-kb:{keyboard:04X}"), name, Kind::Keyboard, (r[1], r[4] == 1)), VID, pid);
        return;
    }
}

pub fn poll(ctx: &mut Context) {
    if ctx.products(VID).is_empty() {
        return;
    }
    poll_nape(ctx);
    poll_legacy(ctx);
    poll_ultra(ctx);
    for pid in ctx.products(VID) {
        for i in ctx.find(VID, pid, |c| c.usage_page >= 0xFF00 && c.output_len == 65 && c.input_len == 65) {
            if ctx.out_of_time() {
                return;
            }
            let (page, usage, product) = (ctx.colls[i].usage_page, ctx.colls[i].usage, clean_name(&ctx.colls[i].product));
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let first = ask_mouse(&d, 0x01, 0x01);
            let tag = first.as_ref().map_or(0, |r| word(r, 6));
            if first.is_some() && page == 0xFF0A && usage == 0x01 && tag != VID && tag != 0x362D {
                // souris 4K sur son récepteur : autre disposition
                let Some(battery) = ask_mouse(&d, 0x41, 0x01).and_then(|r| four_k(&r)) else { continue };
                let name = ask_mouse(&d, 0x40, 0x00).filter(|r| r.len() >= 6).and_then(|r| match word(&r, 4) {
                    0x0620 | 0x0622 => Some("Keychron M3 mini 4K"),
                    0x0621 => Some("Keychron M4 4K"),
                    0x0624 => Some("Keychron M6 4K"),
                    0x07A0 => Some("Keychron M3 4K"),
                    _ => None,
                });
                let name = name.map_or(product, String::from);
                ctx.add(status(format!("keychron:{pid:04X}"), name, Kind::Mouse, battery), VID, pid);
                break;
            }
            let battery = first
                .as_ref()
                .filter(|_| tag == VID)
                .and_then(|r| classic(r))
                .or_else(|| ask_mouse(&d, 0x41, 0x01).and_then(|r| eight_k(&r)))
                .or_else(|| first.as_ref().and_then(|r| eight_k_cable(r)));
            let Some(battery) = battery else { continue };
            ctx.add(status(format!("keychron:{pid:04X}"), product, Kind::Mouse, battery), VID, pid);
            break;
        }
        poll_keyboard(ctx, pid);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames() {
        let q = mouse_query(0x01, 0x01);
        assert_eq!(q[63], 0xA1 - (0x01 + 0x81 + 0x01));
        let mut r = [0u8; 21];
        (r[6], r[7], r[10], r[11]) = (0x34, 0x34, 1, 80);
        assert_eq!(classic(&r), Some((80, true)));
        assert_eq!(eight_k(&r), Some((80, true)));
        r[20] = 0x80 | 55;
        assert_eq!(ultra_power(&r), Some((55, true)));
        assert_eq!(first_keyboard(&[0xB2, 1, 0x34, 0x34, 0x50, 0x0A, 1]), 0x0A50);
        assert_eq!(first_keyboard(&[0xB2, 1, 0x34, 0x34, 0x50, 0x0A, 0]), 0);
    }
}
