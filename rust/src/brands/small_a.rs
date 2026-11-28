//! AJAZZ AK992, AKG, Akko, Arbiter, Areson, BEITONG, CHERRY, Chimera, DARKRIM, Darmoshark, Endgame Gear, ENDORFY,
//! EWEADN, Fantech, Finalmouse. Dérivé de BatteryHub (GPL-3.0), src/drivers/ (un fichier d'origine par marque). Lecture seule.

use super::corsair::{compx_checksum, compx_request};
use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};
use std::cell::{Cell, RefCell};
use std::time::{Duration, Instant};

fn put(mut s: Status, percent: Option<u8>, charging: bool) -> Status {
    (s.percent, s.charging) = (percent, charging);
    s
}

fn level(value: u8) -> Option<u8> {
    Some(value).filter(|&v| v <= 100)
}

fn sum(b: &[u8]) -> u8 {
    b.iter().fold(0u8, |s, &v| s.wrapping_add(v))
}

/// Ouvre la première collection convenable et applique l'échange ; ajoute le résultat.
fn first<F>(ctx: &mut Context, vid: u16, pid: u16, fits: impl Fn(&Info) -> bool, mut exchange: F) -> bool
where
    F: FnMut(&Info, &Dev) -> Option<Status>,
{
    if ctx.is_claimed(vid, pid) {
        return false;
    }
    for i in ctx.find(vid, pid, fits) {
        if ctx.out_of_time() {
            return false;
        }
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        if let Some(s) = exchange(&ctx.colls[i], &d) {
            ctx.add(s, vid, pid);
            return true;
        }
    }
    false
}

fn on(page: u16, usage: u16, input: usize, output: usize) -> impl Fn(&Info) -> bool {
    move |c| c.usage_page == page && c.usage == usage && c.input_len == input && c.output_len == output
}

// ---------------------------------------------------------------- AJAZZ AK992, AKG N9, Akko, CHERRY

pub fn ak992(r: &[u8], sequence: u16) -> Option<(u8, bool)> {
    let s = sequence.to_le_bytes();
    let ok = r.len() >= 10 && r[0] == 0x04 && r[1..3] == s && r[3] == 0x1A && r[5] == 0 && r[6] == 0 && r[8] <= 100 && r[9] <= 2;
    ok.then(|| (r[8], r[9] != 0))
}

/// Marqueur de batterie du récepteur AKG N9 Hybrid.
pub fn akg_level(b: &[u8]) -> Option<u8> {
    let end = b.len().min(3 + usize::from(*b.get(1)?));
    (3..end.saturating_sub(8))
        .filter(|&i| b[i..i + 4] == [0x05, 0x5D, 0x05, 0x00] && b[i + 4..i + 7] == [0xD6, 0x0C, 0x00] && b[i + 8] <= 100)
        .map(|i| b[i + 8])
        .last()
}

pub fn akko_query() -> [u8; 64] {
    let mut f = [0u8; 64];
    (f[0], f[3], f[4], f[7]) = (0x04, 0x1A, 0x03, 0x55);
    let total: u16 = f[3..].iter().fold(0u16, |s, &v| s.wrapping_add(u16::from(v)));
    let bytes = total.to_le_bytes();
    (f[1], f[2]) = (bytes[0], bytes[1]);
    f
}

fn akko_status(r: &[u8]) -> bool {
    r.len() >= 10 && r[0] == 0x04 && r[3] == 0x1A
}

fn poll_keyboards_and_dongles(ctx: &mut Context) {
    let fits = |c: &Info| c.iface == 1 && on(0xFF1C, 0x92, 64, 64)(c) && c.feature_len == 0;
    first(ctx, 0x320F, 0x50C3, fits, |_, d| {
        let sequence = crate::state::now() as u16;
        d.flush();
        let request = [0x04, sequence.to_le_bytes()[0], sequence.to_le_bytes()[1], 0x1A];
        let r = d.query(&request, 600, |r| ak992(r, sequence).is_some())?;
        let (percent, charging) = ak992(&r, sequence)?;
        Some(put(Status::new("ajazz-ak992:320F:50C3", "AJAZZ AK992", Kind::Keyboard), Some(percent), charging))
    });

    let akg = |c: &Info| c.iface == 3 && on(0xFF13, 1, 62, 62)(c);
    first(ctx, 0x0ECB, 0x20D9, akg, |c, d| {
        if !d.write(&[0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00]) {
            return None;
        }
        let end = Instant::now() + Duration::from_millis(600);
        while Instant::now() < end {
            pause(15);
            if let Some(percent) = hid::input_report(c, 0x07, c.input_len).and_then(|r| akg_level(&r)) {
                return Some(put(Status::new("akg:20D9", "AKG N9 Hybrid", Kind::Headset), Some(percent), false));
            }
        }
        None
    });

    for (pid, name) in [(0x000C, "Akko V9 Dash Pro"), (0x000F, "Akko Dash V9 Ultra")] {
        first(ctx, 0x38EE, pid, on(0xFF1C, 0x92, 64, 64), |_, d| {
            d.flush();
            let ok = |r: &[u8]| akko_status(r) && r[8] <= 100;
            let r = (0..2).find_map(|_| d.query(&akko_query(), 400, ok))?;
            let s = Status::new(format!("akko:38ee:{pid:04x}"), name, Kind::Mouse);
            Some(put(s, Some(r[8]), r[9] != 0))
        });
    }

    // clavier CHERRY MX 3.0S : même requête qu'Akko ; une seule entrée pour câble et récepteur
    for pid in [0x00E3, 0x00EA] {
        let added = first(ctx, 0x046A, pid, on(0xFF1C, 0x92, 64, 64), |_, d| {
            d.flush();
            let r = (0..2).find_map(|_| d.query(&akko_query(), 400, akko_status))?;
            let mut s = put(Status::new("cherry:mx30s", "CHERRY MX 3.0S Wireless", Kind::Keyboard), level(r[8]), r[9] != 0);
            s.connected = s.percent.is_some();
            Some(s)
        });
        if added {
            break;
        }
    }

    // souris CHERRY XTRFY : protocole Compx
    first(ctx, 0x046A, 0x0330, on(0xFF02, 2, 17, 17), |_, d| {
        let ask = |command: u8| {
            d.flush();
            d.query(&compx_request(command), 500, |r| r.len() == 17 && r[0] == 0x08 && r[1] == command && r[16] == compx_checksum(r))
        };
        let link = ask(0x03).map(|r| r[6] != 0);
        let power = if link != Some(false) { ask(0x04) } else { None };
        if link.is_none() && power.is_none() {
            return None;
        }
        let mut s = Status::new("cherry:xtrfy", "Cherry XTRFY wireless mouse", Kind::Mouse);
        match power {
            Some(p) => s = put(s, level(p[6]), p[7] != 0),
            None => s.connected = false,
        }
        Some(s)
    });
}

// ---------------------------------------------------------------- Arbiter, Areson, BEITONG, Chimera, DARKRIM

/// Arbiter : `Some(None)` souris endormie.
pub fn arbiter(r: &[u8]) -> Option<Option<(u8, bool)>> {
    let (status, charge, value) = if r.len() >= 9 && r[4] == 0x02 && r[6] == 0x83 {
        (r[1], r[7], r[8])
    } else if r.len() >= 8 && r[3] == 0x02 && r[5] == 0x83 {
        (r[0], r[6], r[7])
    } else {
        return None;
    };
    match status {
        0xA0 => Some(None),
        0xA1 | 0xA2 if value <= 100 => Some(Some((value, charge == 1))),
        _ => None,
    }
}

fn poll_arbiter(ctx: &mut Context) {
    const VID: u16 = 0x373E;
    for (pid, receiver, name, paired) in [
        (0x0009, false, "Arbiter Studio Akitsu", 0x000A), (0x0038, false, "Arbiter Studio Akitsu 3950", 0x0039),
        (0x0066, false, "Arbiter Studio Akitsu Medium", 0x0067), (0x000A, true, "Arbiter Studio Akitsu", 0),
        (0x0039, true, "Arbiter Studio Akitsu 3950", 0), (0x0067, true, "Arbiter Studio Akitsu Medium", 0),
    ] {
        let mut found = ctx.find(VID, pid, |c| c.usage_page == 0xFFFF && c.feature_len == 65);
        if ctx.is_claimed(VID, pid) || found.is_empty() {
            continue;
        }
        found.sort_by_key(|&i| ctx.colls[i].usage != 0);
        let answer = found.iter().find_map(|&i| {
            let c = &ctx.colls[i];
            let mut request = [0u8; 65];
            (request[3], request[4], request[6]) = (0x02, 0x02, 0x83);
            (0..2).find_map(|_| {
                if !hid::feature_write(c, &request) {
                    return None;
                }
                [100, 60, 60].into_iter().find_map(|wait| {
                    pause(wait);
                    arbiter(&hid::feature_read(c, 0, c.feature_len)?)
                })
            })
        });
        if let Some(answer) = answer {
            let key = if paired != 0 { paired } else { pid };
            let mut s = Status::new(format!("arbiter:{VID:04X}:{key:04X}"), name, Kind::Mouse);
            match answer {
                Some((percent, charging)) => {
                    s.connected = percent > 0 || !receiver;
                    s = put(s, Some(percent), charging);
                }
                None => s.connected = false,
            }
            ctx.add(s, VID, pid);
            if paired != 0 {
                ctx.claim(VID, paired);
            }
        } else {
            ctx.claim(VID, pid);
        }
    }
}

/// Areson / Redragon : niveau sur 10.
pub fn areson(r: &[u8]) -> Option<(u8, bool)> {
    let ok = r.len() >= 17 && sum(r) == 0x55 && r[..3] == [0x09, 0x04, 0x00] && r[6] <= 10;
    ok.then(|| (r[6] * 10, r[7] != 0))
}

fn poll_areson(ctx: &mut Context) {
    const VID: u16 = 0x25A7;
    for pid in [0xFA07, 0xFA08] {
        let commands = ctx.find(VID, pid, |c| c.iface == 1 && c.usage_page == 0xFF02 && c.usage == 2 && c.feature_len >= 17);
        let replies = ctx.find(VID, pid, |c| c.iface == 1 && c.usage_page == 0xFF01 && c.usage == 0 && c.input_len >= 17);
        let (Some(&command), Some(&reply)) = (commands.first(), replies.first()) else { continue };
        let Some(d) = Dev::open(&ctx.colls[reply]) else { continue };
        let found = (0..2).find_map(|attempt| {
            if attempt > 0 {
                pause(200);
            }
            d.flush();
            if !hid::feature_write(&ctx.colls[command], &compx_request(0x04)) {
                return None;
            }
            d.read(1800).and_then(|r| areson(&r))
        });
        let Some((percent, charging)) = found else { continue };
        let s = Status::new(format!("areson:{VID:04X}:{pid:04X}"), "Redragon wireless mouse", Kind::Mouse);
        ctx.add(put(s, Some(percent), charging), VID, pid);
    }
}

const NIBBLE: [u8; 16] = [0x0, 0xA, 0x6, 0xF, 0x1, 0xC, 0x2, 0x9, 0xE, 0x3, 0x7, 0xD, 0x4, 0xB, 0x5, 0x8];

/// Chiffrement des octets BEITONG.
pub fn bt_encode(x: u8) -> u8 {
    (NIBBLE[usize::from(x >> 4)] << 4) | ((x & 0x0F) ^ 0x08)
}

pub fn bt_decode(x: u8) -> u8 {
    let high = NIBBLE.iter().position(|&n| n == x >> 4).unwrap_or(0) as u8;
    (high << 4) | ((x & 0x0F) ^ 0x08)
}

fn poll_pads_and_headsets(ctx: &mut Context) {
    for (pid, name) in [(0x5128, "BEITONG KP20"), (0x515C, "BEITONG KP40D")] {
        let fits = |c: &Info| c.usage_page == 0x00FF && c.usage == 3 && c.output_len >= 3 && c.input_len >= 5;
        first(ctx, 0x20BC, pid, fits, |_, d| {
            d.flush();
            let ok = |r: &[u8]| r.len() >= 5 && r[0] == 0x02 && bt_decode(r[1]) == 0x15;
            let r = d.query(&[0x02, bt_encode(0x15)], 800, ok)?;
            let percent = level(bt_decode(r[2]))?;
            let mut state = bt_decode(r[4]);
            if pid == 0x515C {
                state &= 0x0F;
            }
            Some(put(Status::new(format!("beitong:{pid:04X}"), name, Kind::Gamepad), Some(percent), state == 1))
        });
    }

    let chimera = |c: &Info| c.usage_page == 0xFF02 && c.usage == 2 && c.input_len >= 17 && c.output_len >= 17;
    first(ctx, 0x3554, 0xF512, chimera, |_, d| {
        let ask = |command: u8| {
            d.flush();
            (0..1).find_map(|_| d.query(&compx_request(command), 800, |r| r.len() >= 17 && r[1] == command && r[16] == compx_checksum(r)))
        };
        ask(0x03).filter(|r| r[5] != 0)?;
        let r = ask(0x04).filter(|r| r[5] == 0x02 && (1..=100).contains(&r[6]))?;
        let s = Status::new("chimera:3554:F512", "Chimera V2", Kind::Mouse);
        Some(put(s, Some(r[6]), r[7] != 0))
    });

    thread_local!(static SEQUENCE: Cell<u8> = const { Cell::new(0) });
    let darkrim_fits = |c: &Info| c.iface == 3 && on(0xFF02, 2, 65, 65)(c);
    first(ctx, 0x3654, 0x6901, darkrim_fits, |_, d| {
        let sequence = SEQUENCE.get() % 15 + 1;
        SEQUENCE.set(sequence);
        d.flush();
        let r = d.query(&darkrim_request(sequence), 800, |r| darkrim(r, sequence).is_some())?;
        let (percent, charging) = darkrim(&r, sequence)?;
        Some(put(Status::new("darkrim:SHADOW", "DARKRIM SHADOW", Kind::Headset), Some(percent), charging))
    });
}

fn crc_ffff(data: &[u8]) -> u16 {
    data.iter().fold(0xFFFFu16, |mut crc, &b| {
        crc ^= u16::from(b) << 8;
        for _ in 0..8 {
            crc = (crc << 1) ^ if crc & 0x8000 != 0 { 0x1021 } else { 0 };
        }
        crc
    })
}

pub fn darkrim_request(sequence: u8) -> Vec<u8> {
    let body = [0xFE, sequence, 0x10, 0, 1, 0, 1, 2, 0, 1, 0];
    let mut r = vec![0];
    r.extend_from_slice(&body);
    r.extend_from_slice(&crc_ffff(&body).to_be_bytes());
    r
}

/// Réponse DARKRIM : champs (type, taille, valeur) protégés par un CRC.
pub fn darkrim(r: &[u8], sequence: u8) -> Option<(u8, bool)> {
    if r.len() < 11 || r[..6] != [0xFE, sequence, 0x10, 0, 2, 0] || r[6] > 1 {
        return None;
    }
    let len = usize::from(u16::from_le_bytes([r[7], r[8]]));
    if len > 53 || len + 11 > r.len() {
        return None;
    }
    let end = 9 + len;
    if crc_ffff(&r[..end]) != u16::from_be_bytes([r[end], r[end + 1]]) {
        return None;
    }
    let (mut at, mut value) = (9, None);
    while at < end {
        if end - at < 2 {
            return None;
        }
        let (kind, size) = (r[at], usize::from(r[at + 1]));
        at += 2;
        if size > end - at {
            return None;
        }
        if kind == 1 {
            if size != 1 || value.is_some() || r[at] & 0x7F > 100 {
                return None;
            }
            value = Some(r[at]);
        }
        at += size;
    }
    value.map(|v| (v & 0x7F, v & 0x80 != 0))
}

// ---------------------------------------------------------------- Darmoshark

fn word(r: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([r[at], r[at + 1]])
}

fn nrf_checksum(frame: &[u8]) -> u8 {
    0xA1u8.wrapping_sub(sum(&frame[..frame.len().min(63)]))
}

pub fn darmoshark_nrf(r: &[u8]) -> Option<u8> {
    let ok = r.len() >= 64 && r[0] == 0x41 && r[2] == 0x8C && r[3] == 0x01 && r[63] == nrf_checksum(r) && r[6] <= 100;
    ok.then(|| r[6])
}

fn poll_darmoshark(ctx: &mut Context) {
    const VID: u16 = 0x248A;
    for (receiver, mouse, name) in [(0xFF30, 0xFF12, "Darmoshark M3"), (0xFF31, 0xFF18, "Darmoshark M3S")] {
        let fits = |c: &Info| c.iface == 1 && on(0x008C, 1, 33, 33)(c) && c.feature_len == 1001;
        first(ctx, VID, receiver, fits, |c, d| {
            let deadline = Instant::now() + Duration::from_secs(1);
            let query = |opcode: u8| {
                let end = deadline.min(Instant::now() + Duration::from_millis(500));
                d.flush();
                if Instant::now() >= end || !hid::feature_write(c, &[0x51, opcode]) {
                    return None;
                }
                while Instant::now() < end {
                    if d.read(20).is_some_and(|i| i.len() >= 3 && i[..3] == [0x54, 0xE4, 2]) {
                        return None; // le récepteur abandonne
                    }
                    let r = hid::feature_read(c, 0x51, c.feature_len).filter(|r| r.len() >= 12 && r[0] == 0x51 && r[1] == opcode);
                    if r.is_some() {
                        return r;
                    }
                }
                None
            };
            let bond = query(0x03).filter(|r| word(r, 3) == VID && word(r, 5) == mouse && r[7] != 0);
            bond?;
            let r = query(0x06).filter(|r| r[2] == 1 && word(r, 4) == VID && word(r, 6) == mouse && r[11] <= 100)?;
            Some(put(Status::new(format!("darmoshark:{receiver:04X}"), name, Kind::Mouse), Some(r[11]), false))
        });
    }
    let fits = |c: &Info| c.iface == 3 && on(0xFF0A, 1, 65, 65)(c);
    first(ctx, 0x1915, 0x0723, fits, |_, d| {
        let mut request = [0u8; 65];
        (request[1], request[3], request[4]) = (0x41, 0x81, 0x01);
        request[64] = nrf_checksum(&request[1..]);
        d.flush();
        let r = d.query(&request, 600, |r| darmoshark_nrf(r).is_some())?;
        let s = Status::new("darmoshark:1915:0723", "Darmoshark M3S Pro", Kind::Mouse);
        Some(put(s, darmoshark_nrf(&r), false))
    });
}

// ---------------------------------------------------------------- Endgame Gear, ENDORFY, EWEADN, Fantech

thread_local!(static ENDGAME_NAME: RefCell<Vec<(String, &'static str)>> = const { RefCell::new(Vec::new()) });

fn endgame(c: &Info, command: u8, target: u8) -> Option<(u8, Vec<u8>)> {
    let mut request = [0u8; 64];
    (request[0], request[1], request[2]) = (0xA1, command, target);
    if !hid::feature_write(c, &request) {
        return None;
    }
    for attempt in 0..3 {
        pause(150 + attempt * 100);
        let r = hid::feature_read(c, 0xA1, c.feature_len).filter(|r| r.len() >= 20 && r[0] == 0xA1)?;
        if r[1] != 0x03 {
            return Some((r[1], r)); // 03 : occupé
        }
    }
    None
}

fn poll_endgame(ctx: &mut Context) {
    const VID: u16 = 0x3367;
    const PID: u16 = 0x1970;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    for i in ctx.find(VID, PID, |c| c.usage_page == 0xFF01 && c.usage == 2 && c.feature_len >= 64) {
        let c = &ctx.colls[i];
        let mut answer = endgame(c, 0xB4, 0x00);
        if !answer.as_ref().is_some_and(|a| matches!(a.0, 0x01 | 0x08 | 0x11)) {
            endgame(c, 0x0F, 0x01);
            endgame(c, 0x0F, 0x0F);
            answer = endgame(c, 0xB4, 0x00);
        }
        let Some((state, r)) = answer else { continue };
        let percent = (state == 0x01 && r[16] <= 100).then(|| r[16]);
        if percent.is_none() && state != 0x08 && state != 0x11 {
            continue;
        }
        let path = c.path_text.clone();
        if percent.is_some() {
            if let Some((0x01, info)) = endgame(c, 0x0E, 0x00) {
                let model = (info[16] == 0x67 && info[17] == 0x33).then(|| word(&info, 18));
                let name = match model {
                    Some(0x1972) => Some("Endgame Gear OP1w 4k"),
                    Some(0x1984) => Some("Endgame Gear OP1w 4k v2"),
                    Some(0x1968) => Some("Endgame Gear XM2w 4k"),
                    Some(0x1982) => Some("Endgame Gear XM2w 4k v2"),
                    _ => None,
                };
                if let Some(name) = name {
                    ENDGAME_NAME.with_borrow_mut(|n| n.push((path.clone(), name)));
                }
            }
        }
        let name = ENDGAME_NAME.with_borrow(|n| n.iter().rev().find(|e| e.0 == path).map_or("Endgame Gear 4k wireless mouse", |e| e.1));
        let mut s = put(Status::new("endgame:1970", name, Kind::Mouse), percent, false);
        s.connected = percent.is_some();
        ctx.add(s, VID, PID);
        break;
    }
}

/// ENDORFY : paquets « 05 5D len op » dans le rapport 07.
pub fn endorfy(r: &[u8]) -> Option<u8> {
    if r.len() < 9 || r[0] != 0x07 {
        return None;
    }
    let mut i = 3;
    while i + 6 <= r.len() {
        if r[i] != 0x05 {
            i += 1;
            continue;
        }
        let len = usize::from(u16::from_le_bytes([r[i + 2], r[i + 3]]));
        if len < 2 || i + 4 + len > r.len() {
            break;
        }
        if r[i + 1] == 0x5D && word(r, i + 4) == 0x0CD6 && len >= 5 && r[i + 6] == 0 && r[i + 8] <= 100 {
            return Some(r[i + 8]);
        }
        i += 4 + len;
    }
    None
}

fn poll_endorfy(ctx: &mut Context) {
    const VID: u16 = 0x3738;
    const PID: u16 = 0x0072;
    let Some(&i) = ctx.find(VID, PID, on(0xFF13, 1, 62, 62)).first() else { return };
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let c = &ctx.colls[i];
    let Some(d) = Dev::open(c) else { return };
    let before = hid::input_report(c, 0x07, c.input_len);
    let mut found = None;
    if d.write(&[0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00]) {
        for attempt in 0..12 {
            pause(60);
            let Some(r) = hid::input_report(c, 0x07, c.input_len) else { continue };
            if before.as_ref() == Some(&r) && attempt + 1 < 12 {
                continue;
            }
            found = endorfy(&r);
            if found.is_some() {
                break;
            }
        }
    }
    ctx.claim(VID, PID);
    if let Some(percent) = found {
        ctx.out.push(put(Status::new("endorfy:viro", "ENDORFY Viro Wireless", Kind::Headset), Some(percent), false));
    }
}

pub fn eweadn_request(command: u8, sub: u8) -> [u8; 64] {
    let mut r = [0u8; 64];
    (r[0], r[2], r[5], r[6]) = (0x03, 0x50, command, sub);
    r[1] = sum(&r[1..]);
    r
}

pub fn eweadn_reply(r: &[u8]) -> Option<&[u8]> {
    if r.len() < 8 || r[0] != 0x03 || r[2] != 0 || r[4] != 0 {
        return None;
    }
    let len = usize::from(r[3]);
    (len > 0 && 6 + len <= r.len()).then(|| &r[6..6 + len])
}

fn poll_eweadn(ctx: &mut Context) {
    const VID: u16 = 0x372E;
    for pid in [0x103D, 0x1027] {
        let cable = pid == 0x1027;
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF00 && c.usage == 1 && c.feature_len == 64) {
            let c = &ctx.colls[i];
            let query = |command: u8, sub: u8| {
                if !hid::feature_write(c, &eweadn_request(command, sub)) {
                    return None;
                }
                (0..3).find_map(|_| {
                    pause(100);
                    hid::feature_read(c, 0x03, c.feature_len).and_then(|r| eweadn_reply(&r).map(<[u8]>::to_vec))
                })
            };
            let name = if cable { "EWEADN S9 Ultra" } else { "EWEADN wireless mouse" };
            let mut s = Status::new(format!("eweadn:{pid:04X}"), name, Kind::Mouse);
            if !cable {
                let Some(link) = query(0x47, 0x00) else { continue };
                if link[0] != 1 {
                    s.connected = false;
                    ctx.add(s, VID, pid);
                    break;
                }
            }
            let Some(basics) = query(0x4F, 0x81) else { continue };
            let Some(percent) = level(basics[0] & 0x7F) else { continue };
            ctx.add(put(s, Some(percent), basics[0] & 0x80 != 0), VID, pid);
            break;
        }
    }
}

/// Fantech : (pourcentage, en charge, somme correcte).
pub fn fantech(r: &[u8]) -> Option<(u8, bool, bool)> {
    let checked = r.len() >= 17 && sum(&r[..17]) == 0x55;
    let plausible = |power: usize, charge: usize| {
        let (p, c) = (*r.get(power)?, *r.get(charge)?);
        ((1..=100).contains(&p) && c <= 1).then_some((p, c != 0, checked))
    };
    if r.len() >= 7 && r[0] == 0x04 {
        if let Some(found) = plausible(5, 6).or_else(|| plausible(4, 5)) {
            return Some(found);
        }
    }
    (r.len() >= 8 && r[1] == 0x04).then(|| plausible(6, 7)).flatten()
}

fn poll_fantech(ctx: &mut Context) {
    const VID: u16 = 0x25A7;
    for pid in [0xFA7B, 0xFA7C, 0xFA7E] {
        let all = ctx.find(VID, pid, |_| true);
        let Some(&feature) = all.iter().find(|&&i| ctx.colls[i].feature_len >= 17) else { continue };
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let c = &ctx.colls[feature];
        let readers: Vec<Dev> = all.iter().filter(|&&i| ctx.colls[i].input_len >= 6).filter_map(|&i| Dev::open(&ctx.colls[i])).collect();
        if !hid::feature_write(c, &compx_request(0x04)) {
            continue;
        }
        let mut found: Option<(u8, bool, bool)> = None;
        let keep = |new: (u8, bool, bool), found: &mut Option<(u8, bool, bool)>| {
            if found.is_none() || (new.2 && !found.is_some_and(|f| f.2)) {
                *found = Some(new);
            }
        };
        let end = Instant::now() + Duration::from_millis(500);
        while Instant::now() < end && !found.is_some_and(|f| f.2) && !readers.is_empty() {
            for d in &readers {
                if let Some(new) = d.read(40).filter(|r| r.len() >= 6).and_then(|r| fantech(&r)) {
                    keep(new, &mut found);
                }
            }
        }
        if let Some(new) = hid::feature_read(c, 0x08, c.feature_len).and_then(|r| fantech(&r)) {
            keep(new, &mut found);
        }
        let Some((percent, charging, _)) = found else { continue };
        let redragon = pid == 0xFA7E;
        let percent = if redragon { percent.saturating_mul(10).min(100) } else { percent };
        let (prefix, name) = if redragon { ("redragon", "Redragon Invader Pro") } else { ("fantech", "Fantech Helios Go XD5") };
        ctx.add(put(Status::new(format!("{prefix}:{pid:04X}"), name, Kind::Mouse), Some(percent), charging), VID, pid);
    }
}

// ---------------------------------------------------------------- Finalmouse

pub fn finalmouse_percent(mv: u16) -> u8 {
    const MV: [f64; 7] = [3000.0, 3620.0, 3660.0, 3740.0, 3880.0, 4170.0, 4380.0];
    const PCT: [f64; 7] = [0.2, 5.0, 10.0, 25.0, 50.0, 75.0, 100.0];
    let mv = f64::from(mv);
    if mv <= MV[0] {
        return 0;
    }
    match (1..MV.len()).find(|&i| mv <= MV[i]) {
        Some(i) => (PCT[i - 1] + (PCT[i] - PCT[i - 1]) * (mv - MV[i - 1]) / (MV[i] - MV[i - 1]) + 0.5).floor() as u8,
        None => 100,
    }
}

#[derive(Default)]
pub struct Finalmouse {
    pub soc: Option<u8>,
    pub mv: Option<u16>,
    pub charging: Option<u8>,
    pub link: Option<u8>,
}

impl Finalmouse {
    pub fn decode(&mut self, r: &[u8], input_id: u8) {
        if r.len() < 4 || r[0] != input_id {
            return;
        }
        let inner = usize::from(r[1]).min(r.len() - 2);
        if inner < 2 {
            return;
        }
        let n = usize::from(r[3]).min(inner - 2);
        if 4 + n > r.len() {
            return;
        }
        let p = &r[4..4 + n];
        let volts = |at: usize| Some(u16::from_le_bytes([p[at], p[at + 1]])).filter(|&v| v > 0 && v < 5000);
        match r[2] {
            5 if n >= 2 => self.mv = volts(0).or(self.mv),
            38 if n >= 3 && p[0] <= 100 => {
                self.soc = Some(p[0]);
                self.mv = volts(1).or(self.mv);
            }
            37 if n >= 1 => self.charging = Some(p[0]),
            36 if n >= 1 => self.link = Some(p[0]),
            _ => {}
        }
    }
}

fn poll_finalmouse(ctx: &mut Context) {
    const VID: u16 = 0x361D;
    for (pid, name, slx, out_id, in_id, first_get) in [
        (0x0100, "Finalmouse UltralightX", false, 0x04, 0x05, 21), (0x0300, "Finalmouse Starlight X", true, 0x01, 0x02, 57),
        (0x0301, "Finalmouse Starlight X", true, 0x01, 0x02, 57),
    ] {
        let mut found = ctx.find(VID, pid, |c| c.usage_page == 0xFF00 && (slx || c.usage == 1) && c.input_len >= 4 && c.output_len >= 4);
        found.sort_by_key(|&i| ctx.colls[i].usage != 1);
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        for i in found {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let get = |command: u8| d.write(&[out_id, 2, 0x80 | command, 0]);
            if !get(first_get) || !get(96) {
                continue;
            }
            let mut state = Finalmouse::default();
            let mut end = Instant::now() + Duration::from_secs(1);
            let mut shortened = false;
            loop {
                let has_level = state.soc.is_some() || (!slx && state.mv.is_some());
                if has_level && state.charging.is_some() {
                    break;
                }
                if has_level && !shortened {
                    end = end.min(Instant::now() + Duration::from_millis(150));
                    shortened = true;
                }
                let left = end.saturating_duration_since(Instant::now()).as_millis() as u64;
                let Some(r) = (left > 0).then(|| d.read(left)).flatten() else { break };
                state.decode(&r, in_id);
            }
            let mut s = Status::new(format!("finalmouse:{pid:04X}"), name, Kind::Mouse);
            s.charging = state.charging == Some(1);
            s.percent = state.soc.or_else(|| state.mv.map(finalmouse_percent));
            if s.percent.is_none() {
                if state.link != Some(0) {
                    continue;
                }
                s.connected = false;
            }
            ctx.add(s, VID, pid);
            break;
        }
    }
}

pub fn poll(ctx: &mut Context) {
    for step in [
        poll_keyboards_and_dongles, poll_arbiter, poll_areson, poll_pads_and_headsets, poll_darmoshark, poll_endgame, poll_endorfy,
        poll_eweadn, poll_fantech, poll_finalmouse,
    ] {
        if !ctx.out_of_time() {
            step(ctx);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames() {
        assert_eq!(ak992(&[4, 2, 1, 0x1A, 0, 0, 0, 0, 60, 0], 0x0102), Some((60, false)));
        let q = akko_query();
        assert_eq!(u16::from_le_bytes([q[1], q[2]]), 0x1A + 0x03 + 0x55);
        assert_eq!(arbiter(&[0, 0xA1, 0, 0, 2, 0, 0x83, 1, 55]), Some(Some((55, true))));
        assert_eq!(arbiter(&[0, 0xA0, 0, 0, 2, 0, 0x83, 0, 0]), Some(None));
        assert!((0..=255u8).all(|x| bt_decode(bt_encode(x)) == x));
        assert_eq!(eweadn_reply(&[3, 0, 0, 1, 0, 0, 0xC0, 0]), Some(&[0xC0][..]));
        assert_eq!(endorfy(&[7, 0, 0, 0x05, 0x5D, 5, 0, 0xD6, 0x0C, 0, 0, 80]), Some(80));
        assert_eq!(fantech(&[4, 0, 0, 0, 0, 70, 1]), Some((70, true, false)));
    }

    #[test]
    fn darkrim_and_finalmouse() {
        let body = [0xFE, 3, 0x10, 0, 2, 0, 0, 3, 0, 1, 1, 0x80 | 42];
        let mut r = body.to_vec();
        r.extend_from_slice(&crc_ffff(&body).to_be_bytes());
        assert_eq!(darkrim(&r, 3), Some((42, true)));
        assert_eq!(darkrim(&r, 4), None);
        assert_eq!(finalmouse_percent(3000), 0);
        assert_eq!(finalmouse_percent(3880), 50);
        assert_eq!(finalmouse_percent(4500), 100);
        let mut f = Finalmouse::default();
        f.decode(&[5, 8, 38, 3, 77, 0x34, 0x0F, 0, 0, 0], 5);
        assert_eq!((f.soc, f.mv), (Some(77), Some(3892)));
    }
}
