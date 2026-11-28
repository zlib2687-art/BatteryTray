//! ASUS ROG : claviers, souris et casques (VID 0B05), y compris via le ROG Omni Receiver. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/rog.cpp (protocole et modèles : peripheral-battery-tray, MIT, d'après G-Helper ; BatteryTool). Lecture seule.

use super::{Context, Dev, Status};
use crate::devices::Kind;
use std::cell::RefCell;
use std::time::{Duration, Instant};

const VID: u16 = 0x0B05;
const OMNI: u16 = 0x1ACE;
const CETRA_OPEN: u16 = 0x1CAC;

#[derive(Clone, Copy)]
pub struct Model {
    pid: u16,
    name: &'static str,
    keyboard: bool,
    /// 0 : rapports non numérotés.
    report: u8,
    /// -1 : n'importe quelle collection vendeur.
    iface: i32,
    collection: &'static str,
    offset: usize,
    scale: u32,
}

const fn kb(pid: u16, name: &'static str, report: u8, iface: i32, collection: &'static str, offset: usize) -> Model {
    Model { pid, name, keyboard: true, report, iface, collection, offset, scale: 1 }
}

const fn ms(pid: u16, name: &'static str, report: u8, iface: i32, collection: &'static str, offset: usize, scale: u32) -> Model {
    Model { pid, name, keyboard: false, report, iface, collection, offset, scale }
}

pub const MODELS: [Model; 63] = [
    // claviers (Omni : col02 rapport 02 ; récepteur ou câble : interface 1 non numérotée)
    kb(0x1A85, "ROG Azoth", 2, 2, "col02", 6), kb(0x1A85, "ROG Azoth", 0, 1, "", 6), kb(0x1A83, "ROG Azoth", 0, 1, "", 6),
    kb(0x1B42, "ROG Azoth Extreme", 2, 2, "col02", 6), kb(0x1B3F, "ROG Azoth Extreme", 0, 1, "", 6),
    kb(0x1CF1, "ROG Azoth Extreme SE", 2, 2, "col02", 6), kb(0x1CEF, "ROG Azoth Extreme SE", 0, 1, "", 6),
    kb(0x1C25, "ROG Azoth X", 0, 1, "", 6), kb(0x1C24, "ROG Azoth X", 0, 1, "", 6),
    kb(0x193E, "ROG Falchion", 0, 1, "", 11), kb(0x193C, "ROG Falchion", 0, 1, "", 11),
    kb(0x1B04, "ROG Falchion RX", 0, -1, "", 11), kb(0x1B06, "ROG Falchion RX Low Profile", 2, 2, "col02", 11),
    kb(0x1AB0, "ROG Strix Scope II 96", 2, 2, "col02", 6), kb(0x1AAE, "ROG Strix Scope II 96", 0, 1, "", 6),
    kb(0x1B7A, "ROG Strix Scope II 96 RX", 2, 2, "col02", 6), kb(0x1B78, "ROG Strix Scope II 96 RX", 0, 1, "", 6),
    kb(0x1A07, "ROG Strix Scope RX TKL", 0, -1, "", 6),
    // souris (Omni : col03 rapport 03 ; récepteur ou câble : interface 0 non numérotée)
    ms(0x18E5, "ROG Chakram", 0, 0, "", 5, 25), ms(0x18E3, "ROG Chakram", 0, 0, "", 5, 25),
    ms(0x1A1A, "ROG Chakram X", 0, 0, "", 5, 1), ms(0x1A18, "ROG Chakram X", 0, 0, "", 5, 1),
    ms(0x18A0, "ROG Gladius II Wireless", 0, 2, "", 5, 25),
    ms(0x197F, "ROG Gladius III Wireless", 0, 0, "", 5, 1), ms(0x197D, "ROG Gladius III Wireless", 0, 0, "", 5, 1),
    ms(0x1A72, "ROG Gladius III Aimpoint", 3, 2, "col03", 5, 1), ms(0x1A72, "ROG Gladius III Aimpoint", 0, 0, "", 5, 1),
    ms(0x1A70, "ROG Gladius III Aimpoint", 0, 0, "", 5, 1),
    ms(0x1B0C, "ROG Gladius III Aimpoint EVA-02", 0, 0, "", 5, 1), ms(0x1B0A, "ROG Gladius III Aimpoint EVA-02", 0, 0, "", 5, 1),
    ms(0x1A94, "ROG Harpe Ace Aim Lab Edition", 3, 2, "col03", 5, 1), ms(0x1A94, "ROG Harpe Ace Aim Lab Edition", 0, 0, "", 5, 1),
    ms(0x1A92, "ROG Harpe Ace Aim Lab Edition", 0, 0, "", 5, 1),
    ms(0x1B68, "ROG Harpe Ace Extreme", 3, 2, "col03", 5, 1), ms(0x1B69, "ROG Harpe Ace Extreme", 3, 2, "col03", 5, 1),
    ms(0x1B67, "ROG Harpe Ace Extreme", 0, 0, "", 5, 1),
    ms(0x1B65, "ROG Harpe Ace Mini", 3, 2, "col03", 5, 1), ms(0x1B63, "ROG Harpe Ace Mini", 0, 0, "", 5, 1),
    ms(0x1AD0, "ROG Harpe II Ace", 3, 2, "col03", 5, 1), ms(0x1C69, "ROG Harpe II Ace", 0, 0, "", 5, 1),
    ms(0x1960, "ROG Keris Wireless", 0, 0, "", 5, 25), ms(0x195E, "ROG Keris Wireless", 0, 0, "", 5, 25),
    ms(0x1A59, "ROG Keris Wireless EVA Edition", 0, 0, "", 5, 25), ms(0x1A57, "ROG Keris Wireless EVA Edition", 0, 0, "", 5, 25),
    ms(0x1A68, "ROG Keris Wireless Aimpoint", 3, 2, "col03", 5, 1), ms(0x1A6A, "ROG Keris Wireless Aimpoint", 3, 2, "col03", 5, 1),
    ms(0x1A68, "ROG Keris Wireless Aimpoint", 0, 0, "", 5, 1), ms(0x1A66, "ROG Keris Wireless Aimpoint", 0, 0, "", 5, 1),
    ms(0x1B1A, "ROG Keris II Ace", 3, 2, "col03", 5, 1), ms(0x1B18, "ROG Keris II Ace", 3, 2, "col03", 5, 1),
    ms(0x1B16, "ROG Keris II Ace", 0, 0, "", 5, 1),
    ms(0x1C0E, "ROG Keris II Origin", 3, 2, "col03", 5, 1), ms(0x1C0C, "ROG Keris II Origin", 0, 0, "", 5, 1),
    ms(0x1D4E, "ROG Keris II Origin KJP", 3, 2, "col03", 5, 1), ms(0x1D4C, "ROG Keris II Origin KJP", 0, 0, "", 5, 1),
    ms(0x1908, "ROG Pugio II", 0, 0, "", 5, 25), ms(0x1906, "ROG Pugio II", 0, 0, "", 5, 25),
    ms(0x1979, "ROG Spatha X", 0, 0, "", 5, 1), ms(0x1977, "ROG Spatha X", 0, 0, "", 5, 1),
    ms(0x18B4, "ROG Strix Carry", 0, 1, "", 7, 25),
    ms(0x1949, "ROG Strix Impact II Wireless", 0, 0, "", 5, 25), ms(0x1947, "ROG Strix Impact II Wireless", 0, 0, "", 5, 25),
    ms(0x1AD7, "ROG Strix Impact III Wireless", 3, 2, "col03", 5, 1),
];

#[derive(Debug, PartialEq)]
pub enum Reply {
    /// Rapport complet (identifiant remis en place).
    Ok(Vec<u8>),
    /// L'appareil derrière le récepteur n'a pas répondu.
    Asleep,
    Skip,
    Invalid,
}

pub fn parse_reply(raw: &[u8], report: u8, command: u8, sub: u8) -> Reply {
    if raw.len() < 11 {
        return Reply::Invalid;
    }
    let mut r = Vec::with_capacity(raw.len() + 1);
    if report == 0 {
        r.push(0);
    }
    r.extend_from_slice(raw);
    if r.len() < 12 {
        Reply::Skip
    } else if r[1] == 0xFF && r[2] == 0xAA {
        Reply::Asleep
    } else if r[1] == command && r[2] == sub {
        Reply::Ok(r)
    } else {
        Reply::Skip
    }
}

/// (niveau, en charge, endormie).
pub fn read_level(m: &Model, r: &[u8]) -> (Option<u8>, bool, bool) {
    if m.offset >= r.len() || r.len() <= 10 {
        return (None, false, true);
    }
    if m.keyboard {
        return (Some(r[m.offset]).filter(|&v| v <= 100), r[9] == 1, false);
    }
    let value = u32::from(r[m.offset]) * m.scale;
    if (1..=100).contains(&value) { (Some(value as u8), r[10] != 0, false) } else { (None, r[10] != 0, true) }
}

/// Identifiants appairés à l'Omni Receiver : créneaux de 4 octets à partir de l'octet 5.
pub fn omni_list(r: &[u8]) -> Vec<u16> {
    (5..r.len().saturating_sub(1)).step_by(4).map(|at| u16::from_le_bytes([r[at], r[at + 1]])).take_while(|&p| p != 0).collect()
}

fn query(d: &Dev, report: u8, command: u8, sub: u8) -> Reply {
    d.flush();
    d.query(&[report, command, sub], 500, |r| parse_reply(r, report, command, sub) != Reply::Skip)
        .map_or(Reply::Invalid, |r| parse_reply(&r, report, command, sub))
}

fn read_model(d: &Dev, m: &Model) -> Option<Status> {
    let reply = query(d, m.report, 0x12, if m.keyboard { 0x01 } else { 0x07 });
    let kind = if m.keyboard { Kind::Keyboard } else { Kind::Mouse };
    let mut s = Status::new(format!("rog:{:04X}:{}", m.pid, if m.keyboard { "kb" } else { "ms" }), m.name, kind);
    match reply {
        Reply::Asleep => s.connected = false,
        Reply::Ok(r) => {
            let (level, charging, asleep) = read_level(m, &r);
            (s.percent, s.charging, s.connected) = (level, charging, !asleep);
        }
        Reply::Skip | Reply::Invalid => return None,
    }
    Some(s)
}

/// Une seule entrée par nom (câble et récepteur présents pendant la charge).
fn add_once(ctx: &mut Context, status: Status) {
    if !ctx.out.iter().any(|o| o.name == status.name) {
        ctx.out.push(status);
    }
}

fn headset_query(d: &Dev, command: u8) -> Option<Vec<u8>> {
    d.flush();
    // la première réponse après une reconnexion peut prendre 750 ms
    d.query(&[0xCC, 0x12, command], 1200, |r| {
        r.len() < 8 || (r[0] == 0xCC && ((r[1] == 0xFF && r[2] == 0xAA) || (r[1] == 0x12 && (r[2] == command || (r[5] == 0xFF && r[6] == 0xAA)))))
    })
    .filter(|r| r.len() >= 8)
}

fn headset_asleep(r: &[u8]) -> bool {
    (r[1] == 0xFF && r[2] == 0xAA) || (r[5] == 0xFF && r[6] == 0xAA)
}

fn poll_headsets(ctx: &mut Context) {
    for (pid, name) in [(0x1B84, "ROG Pelta"), (0x1AFA, "ROG Delta II"), (CETRA_OPEN, "ROG Cetra Open Wireless")] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let fits = |c: &crate::hid::Info| c.iface == 3 && c.usage_page == 0xFF00 && c.usage == 1 && c.input_len >= 8 && c.output_len >= 8;
        for i in ctx.find(VID, pid, fits) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let command = if pid == CETRA_OPEN { 0x12 } else { 0x07 };
            let Some(r) = headset_query(&d, command) else { continue };
            let mut s = Status::new(format!("rog:{pid:04X}:hs"), name, Kind::Headset);
            if headset_asleep(&r) {
                s.connected = false;
            } else if pid == CETRA_OPEN {
                // gauche et droite : le plus faible (le boîtier n'est pas compté)
                s.percent = [r[5], r[6]].into_iter().filter(|&v| v > 0 && v <= 100).min();
                s.connected = s.percent.is_some();
            } else {
                s.percent = Some(r[6]).filter(|&v| v <= 100);
                s.connected = s.percent.is_some();
                s.charging = headset_query(&d, 0x08).is_some_and(|c| c[5] == 1);
            }
            ctx.add(s, VID, pid);
            break;
        }
    }
}

fn collections(ctx: &Context, pid: u16, m: &Model) -> Vec<usize> {
    ctx.find(VID, pid, |c| {
        c.output_len >= 12
            && c.usage_page >= 0xFF00
            && (m.iface < 0 || c.iface == m.iface)
            && (m.collection.is_empty() || c.path_text.contains(m.collection))
    })
}

fn poll_omni(ctx: &mut Context) {
    if ctx.is_claimed(VID, OMNI) {
        return;
    }
    let fits = |c: &crate::hid::Info| c.iface == 2 && c.output_len >= 12 && c.path_text.contains("col01");
    let Some(&i) = ctx.find(VID, OMNI, fits).first() else { return };
    let Some(d) = Dev::open(&ctx.colls[i]) else { return };
    let Reply::Ok(list) = query(&d, 0x01, 0xA0, 0x00) else { return };
    ctx.claim(VID, OMNI);
    for pid in omni_list(&list) {
        let Some(m) = MODELS.iter().find(|m| m.pid == pid && m.report != 0) else { continue };
        for j in ctx.find(VID, OMNI, |c| c.iface == 2 && c.output_len >= 12 && c.path_text.contains(m.collection)) {
            if let Some(s) = Dev::open(&ctx.colls[j]).and_then(|d| read_model(&d, m)) {
                add_once(ctx, s);
                break;
            }
        }
    }
}

thread_local! {
    /// Un récepteur sans appareil répond aussi « en veille » : un modèle n'est montré que s'il a déjà répondu.
    static AWAKE: RefCell<Vec<u16>> = const { RefCell::new(Vec::new()) };
}

pub fn poll(ctx: &mut Context) {
    if ctx.products(VID).is_empty() {
        return;
    }
    poll_headsets(ctx);
    poll_omni(ctx);
    let started = Instant::now();
    for m in &MODELS {
        if ctx.is_claimed(VID, m.pid) || ctx.out_of_time() || started.elapsed() > Duration::from_secs(8) {
            continue;
        }
        for i in collections(ctx, m.pid, m) {
            let Some(s) = Dev::open(&ctx.colls[i]).and_then(|d| read_model(&d, m)) else { continue };
            if m.report != 0 && !s.connected && !AWAKE.with_borrow(|a| a.contains(&m.pid)) {
                continue;
            }
            if m.report != 0 && s.connected {
                AWAKE.with_borrow_mut(|a| a.push(m.pid));
            }
            add_once(ctx, s);
            ctx.claim(VID, m.pid);
            break;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replies() {
        let mut raw = vec![0u8; 20];
        (raw[0], raw[1], raw[4], raw[9]) = (0x12, 0x07, 3, 1);
        let Reply::Ok(r) = parse_reply(&raw, 0, 0x12, 0x07) else { panic!() };
        let chakram = MODELS.iter().find(|m| m.pid == 0x18E5).unwrap();
        assert_eq!(read_level(chakram, &r), (Some(75), true, false));
        assert_eq!(parse_reply(&raw, 0, 0x12, 0x01), Reply::Skip);
        (raw[0], raw[1]) = (0xFF, 0xAA);
        assert_eq!(parse_reply(&raw, 0, 0x12, 0x07), Reply::Asleep);
        assert_eq!(parse_reply(&[1, 2], 0, 0x12, 0x07), Reply::Invalid);
        assert_eq!(omni_list(&[0, 0, 0, 0, 0, 0x85, 0x1A, 0, 0, 0x72, 0x1A, 0, 0, 0, 0, 0, 0]), vec![0x1A85, 0x1A72]);
    }
}
