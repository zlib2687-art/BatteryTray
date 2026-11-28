//! Casques Razer BlackShark et Barracuda Pro (VID 1532). Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/razer_headset.cpp et barracuda_pro.cpp (sources : OpenRazer, HaloBattery). Les souris Razer sont
//! lues par `devices.rs`.

use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid;
use std::cell::RefCell;
use std::time::{Duration, Instant};

const VID: u16 = 0x1532;
/// Le BlackShark V3 coupe sa liaison si on l'interroge trop souvent.
const MIN_INTERVAL: Duration = Duration::from_secs(300);

thread_local!(static CACHE: RefCell<Vec<(u16, Instant, Status)>> = const { RefCell::new(Vec::new()) });

fn cached(pid: u16) -> Option<Status> {
    CACHE.with_borrow(|c| c.iter().find(|e| e.0 == pid && e.1.elapsed() < MIN_INTERVAL).map(|e| e.2.clone()))
}

fn remember(pid: u16, status: &Status) {
    CACHE.with_borrow_mut(|c| {
        c.retain(|e| e.0 != pid);
        c.push((pid, Instant::now(), status.clone()));
    });
}

fn headset(pid: u16, name: &str) -> Status {
    Status::new(format!("blackshark:{pid:04X}"), name, Kind::Headset)
}

fn xor(b: &[u8]) -> u8 {
    b.iter().fold(0, |x, &v| x ^ v)
}

/// Requête V3 : rapport 02 de 64 octets, octet 62 = XOR des octets 0..61.
pub fn v3_packet(direction: u8, class: u8) -> [u8; 64] {
    let mut b = [0u8; 64];
    b[0] = 0x02;
    b[2] = 0x60;
    b[6] = 0x04;
    b[9] = direction;
    b[10] = class;
    b[62] = xor(&b[..62]);
    b
}

fn is_v3_reply(r: &[u8], class: u8) -> bool {
    r.len() >= 15 && r[0] == 0x02 && r[2] == 0x60 && r[10] == class
}

pub fn parse_v3(pid: u16, battery: &[u8], cable: Option<&[u8]>) -> Option<Status> {
    if battery.len() < 15 || battery[13] > 100 {
        return None;
    }
    let name = match pid {
        0x0A4E | 0x0A55 => "Razer BlackShark V3 Pro for Xbox",
        0x0576 | 0x0577 => "Razer BlackShark V3 Pro",
        _ => "Razer BlackShark V3",
    };
    let mut s = headset(pid, name);
    s.percent = Some(battery[13]);
    s.charging = battery[14] != 0 || cable.is_some_and(|c| c.len() >= 14 && c[13] == 1);
    Some(s)
}

/// Requête V3 X : rapport de fonctionnalité 07.
pub fn v3x_request(transaction: u8, command: u8) -> [u8; 64] {
    let mut r = [0u8; 64];
    r[0] = 0x07;
    r[2] = transaction;
    r[6] = 0x02;
    r[7] = 0x07;
    r[8] = command;
    r[62] = xor(&r[3..=60]);
    r
}

/// `None` : pas notre réponse. `Some(None)` : échec. `Some(Some(v))` : valeur.
pub fn parse_v3x(r: &[u8], transaction: u8, command: u8) -> Option<Option<u8>> {
    if r.len() < 63 || r[2] != transaction || r[7] != 0x07 || r[8] != command || r[6] < 2 {
        return None;
    }
    Some((r[1] == 0x02 || r[1] == 0x01).then_some(r[10]))
}

/// BlackShark V2 HyperSpeed.
pub fn hs_query(command: u8, sequence: u8) -> [u8; 64] {
    let mut b = [0u8; 64];
    b[0] = 0x02;
    b[2] = sequence;
    b[6] = 0x04;
    b[9] = 0x80;
    b[10] = command;
    b[62] = xor(&b[..62]);
    b
}

pub fn parse_hs(r: &[u8], command: u8, sequence: u8) -> Option<u8> {
    let ok = r.len() >= 64 && r[0] == 0x02 && r[2] == sequence && r[10] == command && r[12] >= 1 && xor(&r[1..62]) == r[62];
    ok.then(|| r[13])
}

/// BlackShark V2 Pro (2020). En charge, le niveau annoncé n'est pas fiable : il reste inconnu.
pub fn parse_v2_2020(r: &[u8], last_read: bool) -> Option<Status> {
    const FRESH: [u8; 9] = [0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05];
    const STALE: [u8; 9] = [0xFF, 0x01, 0x00, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05];
    if r.len() != 64 {
        return None;
    }
    let fresh = r.starts_with(&FRESH);
    if !fresh && !(last_read && r.starts_with(&STALE)) {
        return None;
    }
    let mut s = headset(0x0528, "Razer BlackShark V2 Pro (2020)");
    s.connected = fresh && (r[12] != 0 || r[13] != 0);
    if s.connected {
        s.charging = r[11] == 0x09;
        s.percent = (!s.charging && r[14] <= 100).then_some(r[14]);
    }
    Some(s)
}

/// Enveloppe « PA » (BlackShark V2 Pro 2023).
pub fn pa_frame(total: u8, inner: u8, kind: u8, command: u8, flag: u8) -> [u8; 64] {
    let mut b = [0u8; 64];
    b[0] = 0x02;
    b[1] = 0x80;
    b[2] = total;
    b[5] = b'P';
    b[6] = b'A';
    b[7] = inner;
    b[9] = kind;
    b[10] = command;
    b[11] = flag;
    b
}

pub fn parse_pa(r: &[u8], command: u8) -> Option<u8> {
    (r.len() >= 16 && r[0] == 0x02 && r[12] == command && r[13] == 0x01 && r[14] >= 1).then(|| r[15])
}

fn poll_hyperspeed(ctx: &mut Context) {
    const PID: u16 = 0x0565;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let mut found = ctx.find(VID, PID, |c| {
        (c.usage_page == 0xFF14 || c.usage_page == 0xFF00) && c.usage == 0x01 && c.output_len >= 64 && c.input_len >= 64
    });
    found.sort_by_key(|&i| ctx.colls[i].usage_page != 0xFF14);
    for i in found {
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        let mut sequence = 0u8;
        let mut ask = |command| {
            let seq = 0x60 | (sequence & 0x1F);
            sequence += 1;
            d.flush();
            let r = d.query(&hs_query(command, seq), 650, |r| parse_hs(r, command, seq).is_some())?;
            parse_hs(&r, command, seq)
        };
        let Some(level) = ask(0x21).filter(|&v| v <= 100) else { continue };
        let mut s = headset(PID, "Razer BlackShark V2 HyperSpeed");
        s.percent = Some(level);
        s.charging = ask(0x2A).is_some_and(|v| v > 0);
        ctx.add(s, VID, PID);
        return;
    }
}

fn poll_v2_2020(ctx: &mut Context) {
    const PID: u16 = 0x0528;
    const REQUEST: [u8; 9] = [0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05];
    if ctx.is_claimed(VID, PID) {
        return;
    }
    for i in ctx.find(VID, PID, |c| c.iface == 3 && c.usage_page == 0xFF00 && c.usage == 0x0001 && c.feature_len == 64) {
        let c = &ctx.colls[i];
        if !hid::feature_write(c, &REQUEST) {
            continue;
        }
        let status = (0..5).find_map(|read| {
            pause(100);
            parse_v2_2020(&hid::feature_read(c, 0xFF, 64)?, read == 4)
        });
        if let Some(s) = status {
            ctx.add(s, VID, PID);
            return;
        }
    }
}

fn pa_ask(d: &Dev, command: u8) -> Option<u8> {
    let remote = |on: bool| {
        let ok = d.write(&pa_frame(0x07, 0x0E, 0x02, 0xE1, u8::from(on)));
        pause(35);
        ok
    };
    d.flush();
    remote(true); // réveille une liaison assoupie
    let mut value = None;
    for _ in 0..3 {
        remote(true);
        if !d.write(&pa_frame(0x08, 0x08, 0x03, command, 0)) {
            break;
        }
        value = d.wait_for(150, |r| parse_pa(r, command).is_some()).and_then(|r| parse_pa(&r, command));
        if value.is_some() {
            break;
        }
    }
    remote(false);
    value
}

fn poll_v2_2023(ctx: &mut Context) {
    const PID: u16 = 0x0555;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    for i in ctx.find(VID, PID, |c| c.usage_page == 0xFF00 && c.output_len >= 64 && c.input_len >= 16) {
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        let Some(level) = pa_ask(&d, 0x21).filter(|&v| v <= 100) else { continue };
        let mut s = headset(PID, "Razer BlackShark V2 Pro (2023)");
        s.percent = Some(level);
        s.charging = pa_ask(&d, 0x2A).is_some_and(|v| v > 0);
        ctx.add(s, VID, PID);
        return;
    }
}

fn poll_v3x(ctx: &mut Context) {
    let mut transaction = 0x40u8;
    for pid in [0x057D, 0x057C] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let Some(&i) = ctx.find(VID, pid, |c| c.iface == 0 && c.feature_len >= 64).first() else { continue };
        if let Some(s) = cached(pid) {
            ctx.add(s, VID, pid);
            continue;
        }
        let c = &ctx.colls[i];
        let mut get = |command| {
            for _ in 0..4 {
                transaction = transaction.wrapping_add(1);
                if !hid::feature_write(c, &v3x_request(transaction, command)) {
                    return None;
                }
                pause(60);
                let Some(r) = hid::feature_read(c, 0x07, c.feature_len) else { continue };
                if let Some(result) = parse_v3x(&r, transaction, command) {
                    return result;
                }
            }
            None
        };
        let Some(level) = get(0x80).filter(|&v| v <= 100) else { continue };
        let mut s = headset(pid, "Razer BlackShark V3 X");
        s.percent = Some(level);
        s.charging = get(0x84) == Some(1);
        remember(pid, &s);
        ctx.add(s, VID, pid);
    }
}

fn poll_v3(ctx: &mut Context) {
    for pid in [0x0576, 0x0577, 0x0579, 0x057A, 0x0A4E, 0x0A55] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let Some(&i) = ctx.find(VID, pid, |c| c.iface == 5 && c.output_len >= 64 && c.input_len >= 15).first() else { continue };
        if let Some(s) = cached(pid) {
            ctx.add(s, VID, pid);
            continue;
        }
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        // poignée de main attendue par le casque avant les lectures
        for (direction, class) in [(0x00, 0x02), (0x00, 0x2A), (0x80, 0x2A)] {
            d.write(&v3_packet(direction, class));
        }
        pause(250);
        d.flush();
        let get = |class| d.query(&v3_packet(0x80, class), 600, |r| is_v3_reply(r, class));
        let Some(battery) = get(0x21) else { continue };
        let Some(s) = parse_v3(pid, &battery, get(0x2A).as_deref()) else { continue };
        remember(pid, &s);
        ctx.add(s, VID, pid);
    }
}

pub fn poll(ctx: &mut Context) {
    for step in [poll_hyperspeed, poll_v2_2020, poll_v2_2023, poll_v3x, poll_v3] {
        if !ctx.out_of_time() {
            step(ctx);
        }
    }
}

pub fn parse_barracuda(r: &[u8], command: u8) -> Option<u8> {
    (r.len() >= 17 && r[13] == command && (r[14] == 0x01 || r[14] == 0x02) && r[15] > 0 && r[15] <= 32).then(|| r[16])
}

/// Barracuda Pro : le « mode distant » est activé le temps de la requête puis désactivé, comme le fait Synapse.
pub fn poll_barracuda(ctx: &mut Context) {
    const PID: u16 = 0x053A;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let Some(&i) = ctx.find(VID, PID, |c| c.usage_page == 0xFF00 && c.output_len >= 64).first() else { return };
    let Some(d) = Dev::open(&ctx.colls[i]) else { return };
    let drain = || {
        for _ in 0..8 {
            if d.read(20).is_none() {
                break;
            }
        }
    };
    let remote = |on: bool| d.write(&[0x01, 0x80, 0x07, 0x50, 0x41, 0x0E, 0x08, 0x02, 0xE1, u8::from(on)]);
    let ask = |command: u8| {
        (0..4).find_map(|_| {
            drain();
            if !d.write(&[0x01, 0x80, 0x08, 0x50, 0x41, 0x08, 0x08, 0x03, command]) {
                return None;
            }
            (0..6).find_map(|_| d.read(150).and_then(|r| parse_barracuda(&r, command)))
        })
    };
    drain();
    if !remote(true) {
        return;
    }
    let started = Instant::now();
    pause(50);
    let level = ask(0x21);
    let charging = level.is_some() && started.elapsed() < Duration::from_secs(5) && ask(0x2A).is_some_and(|v| v > 0);
    drain();
    remote(false); // toujours désactivé, que la lecture ait réussi ou non
    let mut s = Status::new("barracuda:053A", "Razer Barracuda Pro", Kind::Headset);
    s.percent = level.filter(|&v| v <= 100);
    s.connected = s.percent.is_some();
    s.charging = charging;
    ctx.add(s, VID, PID);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn v3_frames() {
        let p = v3_packet(0x80, 0x21);
        assert_eq!(p[62], 0x02 ^ 0x60 ^ 0x04 ^ 0x80 ^ 0x21);
        let mut reply = [0u8; 64];
        reply[13] = 71;
        reply[14] = 1;
        let s = parse_v3(0x0579, &reply, None).unwrap();
        assert_eq!((s.percent, s.charging), (Some(71), true));
        reply[13] = 200;
        assert!(parse_v3(0x0579, &reply, None).is_none());
    }

    #[test]
    fn other_frames() {
        let mut r = v3x_request(0x41, 0x80).to_vec();
        r[1] = 0x02;
        r[10] = 55;
        assert_eq!(parse_v3x(&r, 0x41, 0x80), Some(Some(55)));
        r[1] = 0x05;
        assert_eq!(parse_v3x(&r, 0x41, 0x80), Some(None));
        assert_eq!(parse_v3x(&r, 0x42, 0x80), None);

        let mut hs = [0u8; 64];
        (hs[0], hs[2], hs[10], hs[12], hs[13]) = (0x02, 0x60, 0x21, 1, 90);
        hs[62] = xor(&hs[1..62]);
        assert_eq!(parse_hs(&hs, 0x21, 0x60), Some(90));
        hs[62] ^= 1;
        assert_eq!(parse_hs(&hs, 0x21, 0x60), None);

        let mut old = [0u8; 64];
        old[..9].copy_from_slice(&[0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05]);
        (old[12], old[13], old[14]) = (0x0F, 0xA0, 63);
        assert_eq!(parse_v2_2020(&old, false).unwrap().percent, Some(63));
        old[11] = 0x09;
        assert_eq!(parse_v2_2020(&old, false).unwrap().percent, None);

        let mut pa = [0u8; 16];
        (pa[0], pa[12], pa[13], pa[14], pa[15]) = (2, 0x21, 1, 1, 48);
        assert_eq!(parse_pa(&pa, 0x21), Some(48));

        let mut b = [0u8; 17];
        (b[13], b[14], b[15], b[16]) = (0x21, 1, 1, 82);
        assert_eq!(parse_barracuda(&b, 0x21), Some(82));
    }
}
