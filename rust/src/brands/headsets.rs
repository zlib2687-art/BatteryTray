//! Casques Jabra (protocole GN), JBL Quantum, Alienware AW920H et Audeze Maxwell. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/jabra.cpp, jbl.cpp, alienware.cpp, audeze.cpp (sources : jabridge, HeadsetControl, HaloBattery). Lecture seule.

use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid;
use std::cell::RefCell;
use std::time::{Duration, Instant};

fn headset(key: String, name: &str) -> Status {
    Status::new(key, name, Kind::Headset)
}

// ---------------------------------------------------------------- Jabra

const JABRA: u16 = 0x0B0E;

/// Bases et récepteurs DECT, exclus.
const DECT: [u16; 58] = [
    0x1000, 0x1016, 0x1017, 0x1018, 0x1021, 0x1022, 0x1023, 0x1037, 0x1038, 0x1039, 0x1041, 0x1042, 0x1043, 0x1110, 0x1112, 0x1113,
    0x1118, 0x111A, 0x111B, 0x111D, 0x111E, 0x111F, 0x1120, 0x1121, 0x1122, 0x1123, 0x1124, 0x1126, 0x1131, 0x1132, 0x1133, 0x1134,
    0x1135, 0x1136, 0x1140, 0x1141, 0x1142, 0x1146, 0x1147, 0x1148, 0x114A, 0x114B, 0x114C, 0x114D, 0x114E, 0x114F, 0x1150, 0x1151,
    0xA700, 0xA701, 0xA702, 0xA703, 0xA704, 0xA705, 0xA706, 0xAE60, 0xAE62, 0xAE63,
];

#[derive(Debug, PartialEq)]
pub enum Gn {
    Ok(Vec<u8>),
    /// Refus, avec son motif (F3 : casque hors ligne).
    Nak(u8),
}

/// `None` : pas la réponse attendue.
pub fn parse_gn(r: &[u8], destination: u8, sequence: u8, class: u8, operation: u8) -> Option<Gn> {
    if r.len() < 7 || r[0] != 0x05 || r[1] != 0 || r[2] != destination || r[3] != sequence || r[4] & 0xC0 != 0xC0 {
        return None;
    }
    if r[5] == 0xFE {
        return Some(Gn::Nak(r[6]));
    }
    let len = usize::from(r[4] & 0x3F);
    if r[5] != class || r[6] != operation || len < 6 || len + 1 > r.len() {
        return None;
    }
    Some(Gn::Ok(r[7..len + 1].to_vec()))
}

/// Nom GN : [n][n octets UTF-8].
pub fn gn_name(p: &[u8]) -> String {
    let Some(&n) = p.first().filter(|&&n| n > 0 && usize::from(n) < p.len()) else { return String::new() };
    String::from_utf8_lossy(&p[1..=usize::from(n)]).chars().filter(|c| !c.is_control()).collect::<String>().trim().to_string()
}

fn gn_query(d: &Dev, sequence: &mut u8, destination: u8, class: u8, operation: u8, timeout: u64) -> Option<Gn> {
    let s = *sequence;
    *sequence = sequence.wrapping_add(1);
    d.flush();
    let request = [0x05, destination, 0x00, s, 0x46, class, operation];
    let r = d.query(&request, timeout, |r| parse_gn(r, destination, s, class, operation).is_some())?;
    parse_gn(&r, destination, s, class, operation)
}

pub fn poll_jabra(ctx: &mut Context) {
    const DONGLE: u8 = 0x04;
    const USB: u8 = 0x08;
    let mut sequence = 0x20u8;
    for pid in ctx.products(JABRA) {
        if DECT.contains(&pid) {
            continue;
        }
        let mut found = ctx.find(JABRA, pid, |c| {
            c.usage_page == 0xFF00 && (c.usage == 0x05 || c.usage == 0x01) && c.output_len >= 64 && c.input_len >= 64
        });
        found.sort_by_key(|&i| ctx.colls[i].usage != 0x05);
        for i in found {
            if ctx.out_of_time() {
                return;
            }
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let dongle = ctx.colls[i].product.to_lowercase().contains("link");
            let order = if dongle { [DONGLE, USB] } else { [USB, DONGLE] };
            // (adresse, batterie) ; `None` pour la batterie : casque hors ligne derrière son récepteur
            let mut answer: Option<(u8, Option<Vec<u8>>)> = None;
            for address in order {
                match gn_query(&d, &mut sequence, address, 0x12, 0x02, 700) {
                    Some(Gn::Ok(p)) if p.len() >= 2 => answer = Some((address, Some(p))),
                    Some(Gn::Nak(reason)) if address == DONGLE && (dongle || reason == 0xF3) => answer = Some((address, None)),
                    _ => continue,
                }
                break;
            }
            let Some((address, battery)) = answer else { continue };
            let mut s = headset(format!("jabra:{pid:04X}"), "Jabra headset");
            match battery {
                Some(p) => {
                    s.percent = Some(p[1]).filter(|&v| v <= 100);
                    s.charging = p[0] & 1 != 0;
                    if let Some(Gn::Ok(n)) = gn_query(&d, &mut sequence, address, 0x02, 0x00, 400) {
                        let name = gn_name(&n);
                        if !name.is_empty() {
                            s.name = name;
                        }
                    }
                }
                None => s.connected = false,
            }
            ctx.add(s, JABRA, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- JBL Quantum

const JBL: u16 = 0x0ECB;

/// HIDV3 : `Some(None)` = erreur annoncée, `None` = pas cette réponse.
pub fn parse_jbl(r: &[u8]) -> Option<Option<(u8, bool)>> {
    if r.len() < 16 || r[..6] != [0x86, 0x00, 0xDD, 0x01, 0x00, 0x01] {
        return None;
    }
    if r[10] & 0x80 != 0 {
        return Some(None);
    }
    if r[11] != 0x0D || r[12] != 0 || (r[13] == 0 && r[14] == 0) {
        return None;
    }
    let percent = r[15] & 0x7F;
    Some((percent <= 100).then(|| (percent, r[15] & 0x80 != 0)))
}

fn jbl_v3_name(pid: u16) -> Option<&'static str> {
    Some(match pid {
        0x2125 | 0x2126 => "JBL Quantum 650 Wireless",
        0x213B | 0x213D => "JBL Quantum 650X Wireless",
        0x2127 | 0x2128 => "JBL Quantum 950 Wireless",
        0x213E | 0x2140 => "JBL Quantum 950X Wireless",
        0x218D | 0x218E => "JBL Quantum 520 Wireless",
        0x21A7 | 0x21A9 => "JBL Quantum 520X Wireless",
        0x219A | 0x219B => "JBL Quantum 850 Wireless",
        _ => return None,
    })
}

pub fn poll_jbl(ctx: &mut Context) {
    // HIDV1 : (produit, interface, page ou 0, nom)
    for (pid, iface, page, name) in [
        (0x206B, 3, 0x0001, "JBL Quantum 350 Wireless"),
        (0x205C, 5, 0, "JBL Quantum 610 Wireless"),
        (0x2115, 5, 0x0001, "JBL Quantum 360 Wireless"),
    ] {
        for i in ctx.find(JBL, pid, |c| c.iface == iface && c.feature_len >= 2 && (page == 0 || (c.usage_page == page && c.usage == 0))) {
            let c = &ctx.colls[i];
            // le micrologiciel répond à un identifiant inconnu par le précédent : le premier octet doit correspondre
            let get = |id: u8| hid::feature_read(c, id, c.feature_len).filter(|r| r[0] == id).map(|r| r[1]);
            if get(0x68) != Some(1) {
                continue;
            }
            let Some(percent) = get(0x49).filter(|&v| v <= 100) else { continue };
            let mut s = headset(format!("jbl:{pid:04X}"), name);
            s.percent = Some(percent);
            ctx.add(s, JBL, pid);
            break;
        }
    }
    for pid in ctx.products(JBL) {
        let Some(name) = jbl_v3_name(pid) else { continue };
        for i in ctx.find(JBL, pid, |c| c.usage_page == 0xFF12 && c.feature_len >= 11 && c.input_len >= 16) {
            let c = &ctx.colls[i];
            let Some(d) = Dev::open(c) else { continue };
            d.flush();
            if !hid::feature_write(c, &[0x88, 0x00, 0xDD, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x0D, 0x00]) {
                continue;
            }
            let reply = d.wait_for(800, |r| parse_jbl(r).is_some()).and_then(|r| parse_jbl(&r)).flatten();
            let Some((percent, charging)) = reply else { continue };
            let mut s = headset(format!("jbl:{pid:04X}"), name);
            (s.percent, s.charging) = (Some(percent), charging);
            ctx.add(s, JBL, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- Alienware AW920H

/// Réponse décodée comme Alienware Command Center (somme XOR des octets 3..12).
pub fn parse_alienware(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 14 || r[..4] != [0x07, 0xC0, 0x0A, 0x09] || r[3..13].iter().fold(0, |x, &v| x ^ v) != r[13] || r[6] == 0 {
        return None;
    }
    let percent = if r[8] == 1 { r[11] } else { r[6] };
    (percent <= 100).then(|| (percent, r[7] == 1))
}

pub fn poll_alienware(ctx: &mut Context) {
    const VID: u16 = 0x413C;
    const PID: u16 = 0xA515;
    let fits = |c: &hid::Info| c.iface == 5 && c.usage_page == 0xFF13 && c.usage == 1 && c.input_len == 62 && c.output_len == 62;
    let Some(&i) = ctx.find(VID, PID, fits).first() else { return };
    let c = &ctx.colls[i];
    let Some(d) = Dev::open(c) else { return };
    let deadline = Instant::now() + Duration::from_millis(600);
    let mut level = None;
    for _ in 0..3 {
        if level.is_some() || Instant::now() >= deadline || !d.write(&[0x06, 0xC0, 0x0A]) {
            break;
        }
        let attempt_end = deadline.min(Instant::now() + Duration::from_millis(180));
        while level.is_none() && Instant::now() < attempt_end {
            pause(15);
            level = hid::input_report(c, 0x07, c.input_len).and_then(|r| parse_alienware(&r));
        }
    }
    match level {
        Some((percent, charging)) => {
            let mut s = headset("alienware:A515".into(), "Alienware AW920H");
            (s.percent, s.charging) = (Some(percent), charging);
            ctx.add(s, VID, PID);
        }
        None => ctx.claim(VID, PID),
    }
}

// ---------------------------------------------------------------- Audeze Maxwell

/// Marqueur de batterie « D6 0C 00 00 pourcentage » dans le tampon de réponses.
pub fn audeze_level(b: &[u8]) -> Option<u8> {
    b.windows(5).filter(|w| w[..4] == [0xD6, 0x0C, 0x00, 0x00] && w[4] <= 100).map(|w| w[4]).next()
}

thread_local!(static FIRST_SEEN: RefCell<Vec<(u16, Instant)>> = const { RefCell::new(Vec::new()) });

pub fn poll_audeze(ctx: &mut Context) {
    const VID: u16 = 0x3329;
    // câbles d'abord : la connexion directe l'emporte
    const MODELS: [(u16, bool, &str); 6] = [
        (0x4B1A, true, "Audeze Maxwell"), (0x4B1E, true, "Audeze Maxwell"), (0x4B19, false, "Audeze Maxwell"),
        (0x4B18, false, "Audeze Maxwell"), (0x4B29, false, "Audeze Maxwell 2"), (0x4B28, false, "Audeze Maxwell 2"),
    ];
    for (pid, cable, name) in MODELS {
        let Some(&i) = ctx.find(VID, pid, |c| c.usage_page == 0xFF13 && c.usage == 1 && c.input_len > 0).first() else { continue };
        let c = &ctx.colls[i];
        // un récepteur nommé « … Dongle » n'a pas de casque lié et répond une valeur périmée
        if !cable && c.product.trim_end().to_lowercase().ends_with("dongle") {
            ctx.claim(VID, pid);
            continue;
        }
        let Some(d) = Dev::open(c) else { continue };
        let mut level = None;
        for _ in 0..2 {
            if !d.write(&[0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C]) {
                break;
            }
            for read in 0..4 {
                pause(60);
                level = hid::input_report(c, 0x07, c.input_len).and_then(|r| audeze_level(&r)).or(level);
                if read >= 2 && level.is_some() {
                    break;
                }
            }
            if level.is_some() {
                break;
            }
        }
        let first = FIRST_SEEN.with_borrow_mut(|seen| match seen.iter().find(|e| e.0 == pid) {
            Some(e) => e.1,
            None => {
                seen.push((pid, Instant::now()));
                Instant::now()
            }
        });
        let mut s = headset("audeze:maxwell".into(), name);
        match level {
            // un casque affiche 0 % un instant à sa mise sous tension
            Some(percent) if percent > 0 || first.elapsed() >= Duration::from_secs(90) => {
                (s.percent, s.charging) = (Some(percent), cable);
            }
            _ => s.connected = false,
        }
        ctx.add(s, VID, pid);
        MODELS.iter().for_each(|m| ctx.claim(VID, m.0));
        return;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn jabra_frames() {
        assert_eq!(parse_gn(&[5, 0, 8, 0x20, 0xC8, 0x12, 2, 1, 77], 8, 0x20, 0x12, 2), Some(Gn::Ok(vec![1, 77])));
        assert_eq!(parse_gn(&[5, 0, 4, 0x20, 0xC6, 0xFE, 0xF3], 4, 0x20, 0x12, 2), Some(Gn::Nak(0xF3)));
        assert_eq!(parse_gn(&[5, 0, 8, 0x21, 0xC8, 0x12, 2, 1, 77], 8, 0x20, 0x12, 2), None);
        assert_eq!(gn_name(b"\x04Evol"), "Evol");
        assert_eq!(gn_name(b"\x09ab"), "");
    }

    #[test]
    fn other_headsets() {
        let mut j = [0u8; 16];
        j[..6].copy_from_slice(&[0x86, 0, 0xDD, 1, 0, 1]);
        (j[11], j[13], j[15]) = (0x0D, 1, 0x80 | 45);
        assert_eq!(parse_jbl(&j), Some(Some((45, true))));
        j[10] = 0x80;
        assert_eq!(parse_jbl(&j), Some(None));

        let mut a = [0x07, 0xC0, 0x0A, 0x09, 0, 1, 0x5F, 0, 0, 0x1C, 0x10, 0x5F, 0x02, 0];
        a[13] = a[3..13].iter().fold(0, |x, &v| x ^ v);
        assert_eq!(parse_alienware(&a), Some((95, false)));
        a[13] ^= 1;
        assert_eq!(parse_alienware(&a), None);

        assert_eq!(audeze_level(&[1, 2, 0xD6, 0x0C, 0, 0, 64, 9]), Some(64));
        assert_eq!(audeze_level(&[0xD6, 0x0C, 0, 0, 200]), None);
    }
}
