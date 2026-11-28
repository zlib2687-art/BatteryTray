//! Casques ASTRO (A50 X, A50 Gen 3/4/5, A20). Dérivé de BatteryHub (GPL-3.0), src/drivers/astro.cpp
//! (protocoles : HeadsetControl, eh-fifty, AstroBar, OpenAstroCC). Lecture seule.

use super::{Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::Info;
use std::cell::RefCell;
use std::time::{Duration, Instant};

const ASTRO: u16 = 0x9886;
const LOGITECH: u16 = 0x046D;

const LEGACY: [(u16, &str); 14] = [
    (0x002C, "ASTRO A50 Gen 4"), (0x002D, "ASTRO A50 Gen 4"), (0x002A, "ASTRO A50 Gen 4"),
    (0x0015, "ASTRO A50 Gen 3"), (0x0014, "ASTRO A50 Gen 3"), (0x0018, "ASTRO A50 Gen 3"), (0x001D, "ASTRO A50 Gen 3"),
    (0x0026, "ASTRO A50 Gen 3"), (0x0037, "ASTRO A20 Wireless"), (0x0038, "ASTRO A20 Wireless"), (0x0039, "ASTRO A20 Wireless"),
    (0x0043, "ASTRO A20 Wireless"), (0x0044, "ASTRO A20 Wireless"), (0x0045, "ASTRO A20 Wireless"),
];

#[derive(Debug, PartialEq)]
pub enum Reply {
    /// Compris mais refusé.
    Refused,
    Value(Vec<u8>),
}

/// `None` : rapport à ignorer.
pub fn classify(r: &[u8]) -> Option<Reply> {
    if r.len() < 4 || r[0] != 0x02 {
        return None;
    }
    match r[1] {
        0x01 => Some(Reply::Refused),
        0x02 => Some(Reply::Value(r.to_vec())),
        _ => None,
    }
}

/// Octet de batterie : bit 7 en charge, bits 0..6 pourcentage ; posé sur la base et pas plein : en charge.
pub fn battery_byte(value: u8, docked: bool) -> Option<(u8, bool)> {
    let percent = value & 0x7F;
    (percent <= 100).then(|| (percent, value & 0x80 != 0 || (docked && percent < 100)))
}

/// A50 Gen 3 : lecture 0x72, le groupe du champ 04 donne la batterie.
pub fn gen3_battery(r: &[u8]) -> Option<u8> {
    if r.len() < 19 || r[2] & 0x3F < 16 {
        return None;
    }
    let data = &r[3..];
    (data[12] == 0x02 && data[13] == 0x04 && data[14] & 0x7F <= 100).then_some(data[14])
}

/// A50 X et Gen 5 : (pourcentage, en charge) ; les évènements non sollicités sont ignorés.
pub fn a50x(r: &[u8]) -> Option<(u8, bool)> {
    let ok = r.len() >= 9
        && r[0] == 0x02
        && r[1] == 0x0C
        && r[2] >= 6
        && usize::from(r[2]) + 3 <= r.len()
        && r[3] == 0
        && r[4] == 0x06
        && (r[5] == 0 || r[5] == 0x0C)
        && r[6] <= 100;
    ok.then(|| (r[6], r[8] != 0))
}

thread_local! {
    /// Dernière lecture par produit : (pourcentage, en charge).
    static CACHE: RefCell<Vec<(u16, u8, bool)>> = const { RefCell::new(Vec::new()) };
}

/// Certains micrologiciels Gen 3 ne numérotent pas leurs rapports : un 00 précède alors la trame.
fn ask(d: &Dev, command: u8, payload: &[u8]) -> Option<Reply> {
    for numbered in [true, false] {
        let mut frame = if numbered { vec![0x02, command] } else { vec![0x00, 0x02, command] };
        if !payload.is_empty() {
            frame.push(payload.len() as u8);
            frame.extend_from_slice(payload);
        }
        d.flush();
        let reply = d.query(&frame, 500, |r| classify(r).is_some()).and_then(|r| classify(&r));
        if reply.is_some() {
            return reply;
        }
    }
    None
}

fn legacy(c: &Info, name: &str) -> Option<Status> {
    let pid = c.pid;
    let mut s = Status::new(format!("astro:{pid:04X}"), name, Kind::Headset);
    let Some(d) = Dev::open(c) else {
        // le logiciel Astro tient un verrou exclusif : dernière lecture connue
        let (percent, charging) = CACHE.with_borrow(|c| c.iter().find(|e| e.0 == pid).map(|e| (e.1, e.2)))?;
        (s.percent, s.charging) = (Some(percent), charging);
        return Some(s);
    };
    // état du casque (0x54), facultatif : bit 0 posé sur la base, bit 1 liaison 2,4 GHz
    let status = match ask(&d, 0x54, &[]) {
        Some(Reply::Value(r)) => Some((r[3] & 1 != 0, r[3] & 2 != 0)),
        _ => None,
    };
    let docked = status.is_some_and(|st| st.0);
    if status.is_some_and(|st| !st.0 && !st.1) {
        s.connected = false;
        return Some(s);
    }
    let battery = ask(&d, 0x7C, &[]);
    let value = match &battery {
        Some(Reply::Value(r)) => Some(r[3]),
        Some(Reply::Refused) if status.is_some() => match ask(&d, 0x72, &[0x01]) {
            Some(Reply::Value(r)) => gen3_battery(&r),
            _ => None,
        },
        _ => None,
    };
    if let Some((percent, charging)) = value.and_then(|v| battery_byte(v, docked)) {
        (s.percent, s.charging) = (Some(percent), charging);
        CACHE.with_borrow_mut(|cache| {
            cache.retain(|e| e.0 != pid);
            cache.push((pid, percent, charging));
        });
        return Some(s);
    }
    if status.is_some() {
        return Some(s);
    }
    (battery == Some(Reply::Refused)).then(|| {
        s.connected = false;
        s
    })
}

pub fn poll(ctx: &mut Context) {
    for pid in [0x0B0B, 0x0B1C] {
        if ctx.is_claimed(LOGITECH, pid) {
            continue;
        }
        let found = ctx.find(LOGITECH, pid, |c| c.usage_page == 0xFF32 && c.usage == 0x0074 && c.input_len == 64 && c.output_len == 64);
        for i in found {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let mut request = [0u8; 64];
            request[..6].copy_from_slice(&[0x02, 0x0C, 3, 0, 0x06, 0x0C]);
            if !d.write(&request) {
                continue;
            }
            let end = Instant::now() + Duration::from_secs(1);
            let mut level = None;
            for _ in 0..8 {
                let left = end.saturating_duration_since(Instant::now()).as_millis() as u64;
                let Some(r) = (left > 0).then(|| d.read(left)).flatten() else { break };
                level = a50x(&r);
                if level.is_some() {
                    break;
                }
            }
            let Some((percent, charging)) = level else { continue };
            let name = if pid == 0x0B0B { "ASTRO A50 X" } else { "ASTRO A50 Gen 5" };
            let mut s = Status::new(format!("astro:{pid:04X}"), name, Kind::Headset);
            (s.percent, s.charging) = (Some(percent), charging);
            ctx.add(s, LOGITECH, pid);
            break;
        }
    }
    for (pid, name) in LEGACY {
        if ctx.is_claimed(ASTRO, pid) {
            continue;
        }
        let found = ctx.find(ASTRO, pid, |c| {
            let vendor = matches!(c.usage_page, 0xFF32 | 0xFF31 | 0xFF00) && c.input_len >= 64 && c.output_len >= 64;
            vendor || (c.usage_page == 0 && (c.iface == 6 || c.iface == -1))
        });
        for i in found {
            if ctx.out_of_time() {
                return;
            }
            if let Some(s) = legacy(&ctx.colls[i], name) {
                ctx.add(s, ASTRO, pid);
                break;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames() {
        assert_eq!(classify(&[2, 1, 0, 0]), Some(Reply::Refused));
        assert_eq!(classify(&[2, 0, 0, 0]), None);
        assert_eq!(battery_byte(0x80 | 55, false), Some((55, true)));
        assert_eq!(battery_byte(40, true), Some((40, true)));
        assert_eq!(battery_byte(100, true), Some((100, false)));
        let mut g = vec![0u8; 19];
        (g[2], g[15], g[16], g[17]) = (16, 2, 4, 66);
        assert_eq!(gen3_battery(&g), Some(66));
        assert_eq!(a50x(&[2, 0x0C, 6, 0, 6, 0x0C, 81, 0, 1]), Some((81, true)));
        assert_eq!(a50x(&[2, 0x0C, 6, 0, 7, 0x0C, 81, 0, 1]), None);
    }
}
