//! Corsair (VID 1B1C) : casques Void, appareils « Bragi » et souris Compx. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/corsair.cpp (sources : HeadsetControl, OpenRGB, HaloBattery). Requêtes de lecture seulement.

use super::{clamp100, Context, Dev, Status};
use crate::devices::Kind;

const VID: u16 = 0x1B1C;

const VOID: [u16; 18] = [
    0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B,
    0x0A75, 0x0A56,
];
const BRAGI: [u16; 8] = [0x2A08, 0x2A02, 0x0A97, 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x2B00];

fn compx_model(pid: u16) -> Option<&'static str> {
    match pid {
        0x2B2A => Some("Corsair Sabre V2 Pro Ultralight"),
        0x2B34 => Some("Corsair Sabre v2 PRO WIRELESS MG"),
        _ => None,
    }
}

/// Famille Void : [2] niveau (bit 7 = micro), [4] état. (connecté, en charge, niveau)
pub fn parse_void(r: &[u8]) -> Option<(bool, bool, Option<u8>)> {
    let state = *r.get(4)?;
    let charging = state == 4 || state == 5;
    let percent = (state == 1 || state == 2 || charging).then(|| clamp100(i64::from(r[2] & 0x7F)));
    Some((state != 0, charging, percent))
}

#[derive(Debug, PartialEq)]
pub enum Bragi {
    /// Notification ou réponse destinée à une autre application.
    Ignore,
    /// L'appareil répond que la propriété est inconnue.
    Refused,
    Value(u32),
}

/// « plain » : source 02 état valeur ; sinon : 02 source 02 état valeur.
pub fn parse_bragi(r: &[u8], plain: bool, target: u8) -> Bragi {
    let source = u8::from(target != 0x08);
    let at = usize::from(!plain);
    if r.len() < at + 7 || r[at + 1] != 0x02 || r[at] != source {
        return Bragi::Ignore;
    }
    if r[at + 2] != 0 {
        return Bragi::Refused;
    }
    Bragi::Value(u32::from_le_bytes([r[at + 3], r[at + 4], r[at + 5], r[at + 6]]))
}

/// Somme de contrôle Compx : 0x55 moins la somme des 16 premiers octets.
pub fn compx_checksum(b: &[u8]) -> u8 {
    0x55u8.wrapping_sub(b.iter().take(16).fold(0u8, |s, &v| s.wrapping_add(v)))
}

pub fn compx_request(command: u8) -> [u8; 17] {
    let mut r = [0u8; 17];
    r[0] = 0x08;
    r[1] = command;
    r[16] = compx_checksum(&r);
    r
}

/// Type d'appareil d'après les collections qu'il expose (souris, manette, clavier).
fn kind_from_collections(ctx: &Context, pid: u16) -> Kind {
    let usages: Vec<u16> = ctx.colls.iter().filter(|c| c.vid == VID && c.pid == pid && c.usage_page == 0x01).map(|c| c.usage).collect();
    if usages.contains(&0x02) {
        Kind::Mouse
    } else if usages.contains(&0x04) || usages.contains(&0x05) {
        Kind::Gamepad
    } else if usages.contains(&0x06) {
        Kind::Keyboard
    } else {
        Kind::Other
    }
}

/// Lecture d'une propriété Bragi ; le premier cadrage qui répond est gardé.
struct Channel {
    d: Dev,
    numbered: bool,
    plain: Option<bool>,
}

impl Channel {
    fn framed(&self, plain: bool, target: u8, property: u8) -> Option<u32> {
        let request = if plain {
            vec![0x00, target, 0x02, property]
        } else if self.numbered {
            vec![0x02, target, 0x02, property]
        } else {
            vec![0x00, 0x02, target, 0x02, property]
        };
        self.d.flush();
        let reply = self.d.query(&request, 300, |r| parse_bragi(r, plain, target) != Bragi::Ignore)?;
        match parse_bragi(&reply, plain, target) {
            Bragi::Value(v) => Some(v),
            _ => None,
        }
    }

    fn get(&mut self, target: u8, property: u8) -> Option<u32> {
        if let Some(plain) = self.plain {
            return self.framed(plain, target, property);
        }
        for plain in [false, true] {
            if let Some(v) = self.framed(plain, target, property) {
                self.plain = Some(plain);
                return Some(v);
            }
        }
        None
    }
}

fn poll_void(ctx: &mut Context, i: usize, pid: u16) -> bool {
    let c = &ctx.colls[i];
    let name = if c.product.trim().is_empty() { "Corsair wireless headset".to_string() } else { c.product.clone() };
    let Some(d) = Dev::open(c) else { return false };
    d.flush();
    let Some(r) = d.query(&[0xC9, 0x64], 1000, |_| true) else { return false };
    let Some((connected, charging, percent)) = parse_void(&r) else { return false };
    let mut s = Status::new(format!("corsair:{pid:04X}"), name, Kind::Headset);
    (s.connected, s.charging, s.percent) = (connected, charging, percent);
    ctx.add(s, VID, pid);
    true
}

fn poll_compx(ctx: &mut Context, i: usize, pid: u16, model: &str) -> bool {
    let Some(d) = Dev::open(&ctx.colls[i]) else { return false };
    let ask = |command: u8| {
        d.flush();
        d.query(&compx_request(command), 500, |r| r.len() >= 17 && r[0] == 0x08 && r[1] == command && r[16] == compx_checksum(r))
    };
    let online = ask(0x03).map(|r| r[6] != 0);
    let battery = if online != Some(false) { ask(0x04) } else { None };
    if online.is_none() && battery.is_none() {
        return false;
    }
    if online == Some(false) {
        ctx.claim(VID, pid);
        return true;
    }
    let mut s = Status::new(format!("corsair:{pid:04X}"), model, Kind::Mouse);
    match battery {
        Some(b) => {
            s.percent = Some(b[6]).filter(|&p| p <= 100);
            s.charging = b[7] != 0;
        }
        None => s.connected = false, // en ligne mais endormie
    }
    ctx.add(s, VID, pid);
    true
}

fn poll_bragi(ctx: &mut Context, i: usize, pid: u16) -> bool {
    let (numbered, product) = (ctx.colls[i].output_len == 64, ctx.colls[i].product.clone());
    let Some(d) = Dev::open(&ctx.colls[i]) else { return false };
    let mut channel = Channel { d, numbered, plain: None };
    let mut any = false;
    // appareils appairés derrière le récepteur d'abord, puis l'appareil lui-même (câble ou Bluetooth)
    for target in [0x09u8, 0x0A, 0x0B, 0x08] {
        if (target == 0x08 && any) || ctx.out_of_time() {
            break;
        }
        let mut level = channel.get(target, 0x0F);
        for _ in 0..2 {
            if level.is_some_and(|v| v == 0 || v > 1000) {
                level = channel.get(target, 0x0F);
            }
        }
        let Some(level) = level.filter(|&v| v <= 1000) else { continue };
        let mut name = if product.trim().is_empty() { "Corsair wireless device".to_string() } else { product.clone() };
        let mut kind = crate::generic::guess_kind(&name);
        if target != 0x08 {
            if let Some(paired) = channel.get(target, 0x12) {
                if paired == 0x1B7E {
                    (name, kind) = ("Corsair Dark Core RGB Pro SE".into(), Kind::Mouse);
                } else if paired >> 8 == 0x0A {
                    kind = Kind::Headset;
                    if product.contains("Receiver") {
                        name = "Corsair wireless headset".into();
                    }
                }
            }
        }
        if kind == Kind::Other && pid >> 8 == 0x0A {
            kind = Kind::Headset;
        }
        if kind == Kind::Other {
            kind = kind_from_collections(ctx, pid);
        }
        let mut s = Status::new(format!("corsair:{pid:04X}:{target}"), name, kind);
        s.percent = Some(clamp100(i64::from((level + 5) / 10)));
        s.charging = matches!(channel.get(target, 0x10), Some(1 | 3));
        ctx.out.push(s);
        any = true;
    }
    if any {
        ctx.claim(VID, pid);
    }
    any
}

pub fn poll(ctx: &mut Context) {
    for pid in ctx.products(VID) {
        if ctx.out_of_time() {
            return;
        }
        if VOID.contains(&pid) {
            for i in ctx.find(VID, pid, |c| (c.usage_page == 0xFFC5 || c.usage_page == 0xFF00) && c.output_len > 0) {
                if poll_void(ctx, i, pid) {
                    break;
                }
            }
        } else if let Some(model) = compx_model(pid) {
            let fits = |c: &crate::hid::Info| c.usage_page == 0xFF02 && c.usage == 0x0002 && c.output_len >= 17 && c.input_len >= 17;
            for i in ctx.find(VID, pid, fits) {
                if poll_compx(ctx, i, pid, model) {
                    break;
                }
            }
        } else {
            let known = BRAGI.contains(&pid);
            let found = ctx.find(VID, pid, |c| {
                let sizes = matches!(c.output_len, 64 | 65) && matches!(c.input_len, 64 | 65);
                sizes && (c.usage_page == 0xFF42 || (known && c.usage_page >= 0xFF00) || (known && c.iface == 4))
            });
            for i in found {
                if poll_bragi(ctx, i, pid) {
                    break;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn void_headsets() {
        assert_eq!(parse_void(&[0, 0, 0x80 | 66, 0, 1]), Some((true, false, Some(66))));
        assert_eq!(parse_void(&[0, 0, 40, 0, 5]), Some((true, true, Some(40))));
        assert_eq!(parse_void(&[0, 0, 40, 0, 0]), Some((false, false, None)));
        assert_eq!(parse_void(&[0]), None);
    }

    #[test]
    fn bragi_frames() {
        assert_eq!(parse_bragi(&[1, 2, 0, 0xE8, 0x03, 0, 0], true, 9), Bragi::Value(1000));
        assert_eq!(parse_bragi(&[2, 0, 2, 0, 50, 0, 0, 0], false, 8), Bragi::Value(50));
        assert_eq!(parse_bragi(&[1, 2, 5, 0, 0, 0, 0], true, 9), Bragi::Refused);
        assert_eq!(parse_bragi(&[0, 2, 0, 0, 0, 0, 0], true, 9), Bragi::Ignore);
        assert_eq!(parse_bragi(&[1], true, 9), Bragi::Ignore);
    }

    #[test]
    fn compx_frames() {
        let r = compx_request(4);
        assert_eq!(r[16], 0x55 - 0x08 - 4);
        assert_eq!(compx_checksum(&r), r[16]);
    }
}
