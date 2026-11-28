//! DualShock 4 et DualSense (VID 054C), USB ou Bluetooth : la batterie est dans le rapport d'entrée habituel.
//! Dérivé de BatteryHub (GPL-3.0), src/drivers/playstation.cpp ; disposition du pilote Linux hid-playstation.

use super::{clamp100, Context, Dev, Status};
use crate::devices::Kind;
use std::time::{Duration, Instant};

const VID: u16 = 0x054C;

fn is_ds4(pid: u16) -> bool {
    matches!(pid, 0x05C4 | 0x09CC | 0x0BA0)
}

#[derive(Debug, PartialEq)]
pub enum Outcome {
    OtherReport,
    /// (pourcentage, en charge)
    Found(u8, bool),
    /// Adaptateur sans fil sans manette.
    NoController,
}

pub fn parse(pid: u16, r: &[u8], bluetooth: bool) -> Outcome {
    if r.len() < 10 {
        return Outcome::OtherReport;
    }
    let ds4 = is_ds4(pid);
    let at = match (ds4, r[0]) {
        (true, 0x01) => 30,
        (true, 0x11) => 32,
        (false, 0x01) => 53,
        (false, 0x31) => 54,
        _ => return Outcome::OtherReport,
    };
    // en Bluetooth, le rapport 01 est le rapport réduit : pas de batterie
    if r.len() <= at || (r[0] == 0x01 && bluetooth) {
        return Outcome::OtherReport;
    }
    let level = i64::from(r[at] & 0x0F);
    if ds4 {
        if pid == 0x0BA0 && r[at] == 0 {
            return Outcome::NoController;
        }
        let cable = r[at] & 0x10 != 0;
        let percent = if !cable {
            clamp100(level * 10 + 5)
        } else if level >= 11 {
            100
        } else {
            clamp100(level * 10)
        };
        Outcome::Found(percent, cable && level < 11)
    } else {
        match r[at] >> 4 {
            0x2 => Outcome::Found(100, true),
            state => Outcome::Found(clamp100(level * 10 + 5), state == 0x1),
        }
    }
}

/// Distingue deux manettes identiques.
pub(super) fn path_hash(path: &str) -> u32 {
    path.encode_utf16().fold(2_166_136_261u32, |h, c| (h ^ u32::from(c)).wrapping_mul(16_777_619))
}

pub fn poll(ctx: &mut Context) {
    for i in 0..ctx.colls.len() {
        let c = &ctx.colls[i];
        let (pid, bluetooth, feature_len) = (c.pid, c.input_len > 64, c.feature_len);
        if c.vid != VID || !(is_ds4(pid) || pid == 0x0CE6 || pid == 0x0DF2) || c.usage_page != 0x01 || c.usage != 0x05 {
            continue;
        }
        if ctx.out_of_time() {
            return;
        }
        let key = format!("ps:{pid:04X}:{}", path_hash(&c.path_text));
        let Some(d) = Dev::open(c) else { continue };
        if bluetooth {
            // lire le rapport de calibration (05) passe la manette en rapports complets, comme le font les jeux
            crate::hid::feature_read(c, 0x05, feature_len);
        }
        let end = Instant::now() + Duration::from_millis(500);
        while Instant::now() < end {
            let Some(r) = d.read(100) else { continue };
            // la lecture a retiré un éventuel identifiant 0 ; les rapports de ces manettes sont numérotés
            match parse(pid, &r, bluetooth) {
                Outcome::NoController => break,
                Outcome::OtherReport => continue,
                Outcome::Found(percent, charging) => {
                    let name = match pid {
                        _ if is_ds4(pid) => "DualShock 4",
                        0x0DF2 => "DualSense Edge",
                        _ => "DualSense",
                    };
                    let mut s = Status::new(key.clone(), name, Kind::Gamepad);
                    s.percent = Some(percent);
                    s.charging = charging;
                    ctx.add(s, VID, pid);
                    break;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn report(id: u8, at: usize, value: u8) -> Vec<u8> {
        let mut r = vec![0; 78];
        r[0] = id;
        r[at] = value;
        r
    }

    #[test]
    fn levels() {
        assert_eq!(parse(0x09CC, &report(0x01, 30, 7), false), Outcome::Found(75, false));
        assert_eq!(parse(0x09CC, &report(0x11, 32, 0x10 | 11), true), Outcome::Found(100, false));
        assert_eq!(parse(0x0CE6, &report(0x01, 53, 0x13), false), Outcome::Found(35, true));
        assert_eq!(parse(0x0CE6, &report(0x31, 54, 0x20), true), Outcome::Found(100, true));
        assert_eq!(parse(0x0BA0, &report(0x01, 30, 0), false), Outcome::NoController);
        assert_eq!(parse(0x0CE6, &report(0x01, 53, 5), true), Outcome::OtherReport);
        assert_eq!(parse(0x0CE6, &[1, 2], false), Outcome::OtherReport);
    }
}
