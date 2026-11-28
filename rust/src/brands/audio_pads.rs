//! Poly / Plantronics, Turtle Beach, Sony INZONE, Steam Controller, Switch Pro. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/ (un fichier d'origine par marque). Lecture seule.

use super::{pause, playstation::path_hash, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid;
use std::cell::Cell;
use std::time::{Duration, Instant};

fn level(value: u8) -> Option<u8> {
    Some(value).filter(|&v| v <= 100)
}

// ---------------------------------------------------------------- Poly / Plantronics

pub fn plantronics_frame(op: u8, group: u8, item: u8, address: u8) -> [u8; 64] {
    let mut f = [0u8; 64];
    let head = [0x07, 0x01, 0x01, 0x10, if op == 0x02 { 0x06 } else { 0x07 }, address, 0, 0, op, group, item];
    f[..head.len()].copy_from_slice(&head);
    f
}

fn plantronics_is_reply(r: &[u8], op: u8, group: u8, item: u8, address: u8) -> bool {
    r.len() > 10 && r[..4] == [0x07, 0x01, 0x01, 0x10] && r[5] == address && r[9] == group && r[10] == item && r[8] == op
}

pub fn plantronics_battery(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 14 {
        return None;
    }
    let (step, count) = (u32::from(r[11]), u32::from(r[12]));
    (count >= 2 && step < count).then(|| ((step * 100 / (count - 1)) as u8, r[13] != 0))
}

fn plantronics_read(d: &Dev) -> Option<(u8, bool)> {
    let wait = |op, group, item, address| {
        for _ in 0..16 {
            let r = d.read(1000)?;
            if plantronics_is_reply(&r, op, group, item, address) {
                return Some(r);
            }
        }
        None
    };
    d.flush();
    d.write(&[0x06, 0x01]); // attache de l'adaptateur ; sans effet s'il l'est déjà
    d.write(&plantronics_frame(0x01, 0x01, 0x02, 0x00));
    wait(0x08, 0x01, 0x02, 0x00);
    if !d.write(&plantronics_frame(0x01, 0x01, 0x02, 0x20)) {
        return None;
    }
    wait(0x08, 0x01, 0x02, 0x02)?; // casque éteint, hors de portée ou non appairé
    if !d.write(&plantronics_frame(0x02, 0x0A, 0x1A, 0x20)) {
        return None;
    }
    plantronics_battery(&wait(0x03, 0x0A, 0x1A, 0x02)?)
}

fn poll_plantronics(ctx: &mut Context) {
    const VID: u16 = 0x047F;
    for pid in [0x02EE, 0x02F7] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFFA2 && c.usage == 3) {
            let Some((percent, charging)) = Dev::open(&ctx.colls[i]).and_then(|d| plantronics_read(&d)) else { continue };
            let mut s = Status::new(format!("plantronics:{pid:04X}"), "Poly / Plantronics headset", Kind::Headset);
            (s.percent, s.charging) = (Some(percent), charging);
            ctx.add(s, VID, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- Turtle Beach Stealth 700 Gen 3

const ENABLE_COAP: &[u8] = &[0x05, 0x5A, 0x02, 0x00, 0x42, 0x99];
const GET_GSI: &[u8] = &[0x05, 0x5A, 0x0A, 0x00, 0x02, 0x99, 0x40, 0x01, 0x00, 0x01, 0xB3, b'G', b'S', b'I'];

fn turtle_send(race: &[u8]) -> [u8; 62] {
    let mut b = [0u8; 62];
    (b[0], b[1]) = (0x06, race.len() as u8);
    b[3..3 + race.len()].copy_from_slice(race);
    b
}

/// Dernier niveau « "240":"NN" » d'un message GSI.
pub fn turtle_level(text: &str) -> Option<u8> {
    if !text.contains("\"GSI\"") {
        return None;
    }
    text.split("\"240\":\"")
        .skip(1)
        .filter_map(|rest| {
            let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
            let closed = (1..=4).contains(&digits) && rest.as_bytes().get(digits) == Some(&b'"');
            closed.then(|| rest[..digits].parse::<u16>().ok()).flatten().filter(|&v| v <= 100)
        })
        .last()
        .map(|v| v as u8)
}

fn turtle_payload(r: &[u8]) -> String {
    if r.len() < 3 || r[0] != 0x07 {
        return String::new();
    }
    let n = usize::from(r[1]).min(r.len() - 3);
    r[3..3 + n].iter().map(|&b| b as char).collect()
}

fn turtle_read(info: &hid::Info) -> Option<u8> {
    let d = Dev::open(info)?;
    let drain = || (0..5).for_each(|_| drop(hid::input_report(info, 0x07, info.input_len)));
    drain();
    if !d.write(&turtle_send(ENABLE_COAP)) {
        return None;
    }
    pause(300); // la puce a besoin d'un instant une fois le client activé
    drain();
    if !d.write(&turtle_send(GET_GSI)) {
        return None;
    }
    let mut text = String::new();
    for _ in 0..30 {
        pause(30);
        let Some(r) = hid::input_report(info, 0x07, info.input_len) else { continue };
        text.push_str(&turtle_payload(&r));
        if let Some(found) = turtle_level(&text) {
            return Some(found);
        }
    }
    None
}

fn turtle_name(product: &str) -> String {
    if product.is_empty() {
        "Turtle Beach Stealth 700 Gen 3".into()
    } else if product.to_ascii_lowercase().starts_with("turtle beach") {
        product.into()
    } else {
        format!("Turtle Beach {product}")
    }
}

fn poll_turtle_beach(ctx: &mut Context) {
    const VID: u16 = 0x10F5;
    const PIDS: [u16; 3] = [0x2251, 0x2262, 0x2253];
    let (mut chosen, mut value) = (None, None);
    for pid in PIDS {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let found = ctx.find(VID, pid, |c| c.usage_page == 0xFF13 && c.usage == 1 && c.input_len > 0 && c.output_len > 0);
        let Some(&i) = found.first() else { continue };
        chosen.get_or_insert((i, pid));
        value = turtle_read(&ctx.colls[i]);
        if value.is_some() {
            chosen = Some((i, pid));
            break;
        }
    }
    let Some((i, pid)) = chosen else { return };
    // émetteur et câble : un seul casque
    let mut s = Status::new("turtlebeach:stealth700", turtle_name(&ctx.colls[i].product), Kind::Headset);
    (s.percent, s.connected) = (value, value.is_some());
    ctx.add(s, VID, pid);
    for other in PIDS {
        ctx.claim(VID, other);
    }
}

// ---------------------------------------------------------------- Sony INZONE

pub struct Event {
    pub id: u8,
    pub kind: u8,
    pub tid: u16,
    pub payload: Vec<u8>,
}

fn sum(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |s, &v| s.wrapping_add(v))
}

pub fn inzone_event(b: &[u8]) -> Option<Event> {
    if b.len() < 15 || b[0] != 0x02 {
        return None;
    }
    let len = usize::from(b[1]);
    if !(12..=62).contains(&len) || b.len() < len + 2 {
        return None;
    }
    if b[2] != 0x04 || b[3] != 0xFF || b[5..8] != [0x00, 0x96, 0xC3] || b[8] >> 4 != 1 || sum(&b[5..=len]) != b[len + 1] {
        return None;
    }
    Some(Event { id: b[9], kind: b[10], tid: u16::from_le_bytes([b[11], b[12]]), payload: b[13..=len].to_vec() })
}

pub fn inzone_request(tid: u16) -> [u8; 64] {
    let mut r = [0u8; 64];
    let t = tid.to_le_bytes();
    r[..13].copy_from_slice(&[0x02, 12, 0x01, 0x00, 0xFC, 8, 0x96, 0xC3, 0x41, 0x04, 0x01, t[0], t[1]]);
    r[13] = sum(&r[6..13]);
    r
}

thread_local!(static TID: Cell<u16> = const { Cell::new(1) });

/// `Ok(None)` : pas de réponse ; `Err` : écriture refusée.
fn inzone_battery(d: &Dev, min_payload: usize) -> Result<Option<Vec<u8>>, ()> {
    let tid = TID.get().wrapping_add(1).max(2); // le tid 1 sert aux notifications du récepteur
    TID.set(tid);
    d.flush();
    if !d.write(&inzone_request(tid)) {
        return Err(());
    }
    let end = Instant::now() + Duration::from_millis(1500);
    loop {
        let left = end.saturating_duration_since(Instant::now()).as_millis() as u64;
        if left == 0 {
            return Ok(None);
        }
        let Some(r) = d.read(left).filter(|r| r.len() >= 15) else { return Ok(None) };
        let Some(e) = inzone_event(&r) else { continue };
        if e.id == 0x04 && e.tid == tid && e.payload.len() >= min_payload && (e.kind == 0x10 || e.kind == 0xA0) {
            return Ok(Some(e.payload));
        }
    }
}

/// Écouteurs : le plus faible des deux, ou le seul connu.
pub fn inzone_buds(p: &[u8]) -> Option<u8> {
    let (left, right) = (level(p[1]), level(p[3]));
    match (left, right) {
        (Some(l), Some(r)) => Some(l.min(r)),
        _ => left.or(right),
    }
}

fn poll_inzone(ctx: &mut Context) {
    const VID: u16 = 0x054C;
    for (pid, name, buds, min_payload) in
        [(0x0FA8, "Sony INZONE H9 II", false, 2), (0x0EBF, "Sony INZONE H5", false, 2), (0x0EC2, "Sony INZONE Buds", true, 6)]
    {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let new_status = || Status::new(format!("inzone:{pid:04X}"), name, Kind::Headset);
        let (mut added, mut silent) = (false, false);
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF04 && c.output_len >= 64) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let p = match inzone_battery(&d, min_payload) {
                Ok(Some(p)) => p,
                Ok(None) => {
                    silent = true;
                    continue;
                }
                Err(()) => continue,
            };
            let mut s = new_status();
            if buds {
                s.percent = inzone_buds(&p);
                s.connected = s.percent.is_some();
            } else if p[1] == 0xFF {
                s.connected = false;
            } else {
                (s.percent, s.charging) = (level(p[1]), p[0] != 0);
            }
            ctx.add(s, VID, pid);
            added = true;
            break;
        }
        if buds && silent && !added {
            let mut s = new_status();
            s.connected = false;
            ctx.add(s, VID, pid);
        }
    }
}

// ---------------------------------------------------------------- Valve Steam Controller, Nintendo Switch Pro

pub fn steam(r: &[u8], legacy: bool) -> Option<(u8, bool)> {
    if r.len() < 15 {
        return None;
    }
    if legacy {
        return (r[..4] == [0x01, 0x00, 0x04, 11] && r[14] <= 100).then(|| (r[14], false));
    }
    (r[0] == 0x43 && r[2] <= 100).then(|| (r[2], r[1] == 2))
}

fn steam_read(d: &Dev, legacy: bool, puck: bool) -> Option<(u8, bool)> {
    let mut received = false;
    let end = Instant::now() + Duration::from_secs(5);
    while Instant::now() < end {
        let Some(r) = d.read(if puck && !received { 250 } else { 100 }) else {
            if puck && !received {
                return None;
            }
            continue;
        };
        received = true;
        if let Some(found) = steam(&r, legacy) {
            return Some(found);
        }
    }
    None
}

fn poll_steam(ctx: &mut Context) {
    const VID: u16 = 0x28DE;
    for i in 0..ctx.colls.len() {
        let c = &ctx.colls[i];
        let pid = c.pid;
        let known = [0x1142, 0x1302, 0x1303, 0x1304, 0x1305].contains(&pid);
        // sur le socle (1304), les autres collections sont les contacts de charge
        if c.vid != VID || !known || c.usage_page != 0xFF00 || c.input_len < 15 || (pid == 0x1304 && c.usage != 1) {
            continue;
        }
        if ctx.out_of_time() {
            return;
        }
        let (legacy, puck) = (pid == 0x1142, pid == 0x1304);
        let name = if legacy || puck || pid == 0x1305 || c.product.is_empty() { "Steam Controller".into() } else { c.product.clone() };
        let key = format!("steam:{pid:04X}:{}", path_hash(&c.path_text));
        let Some((percent, charging)) = Dev::open(c).and_then(|d| steam_read(&d, legacy, puck)) else { continue };
        let mut s = Status::new(key, name, Kind::Gamepad);
        (s.percent, s.charging) = (Some(percent), charging);
        ctx.add(s, VID, pid);
    }
}

pub fn switch_pro(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 13 || r[0] != 0x30 {
        return None;
    }
    let step = (r[2] & 0xE0) >> 5;
    (step <= 4).then(|| (25 * step, r[2] & 0x10 != 0 && step < 4))
}

fn poll_switch_pro(ctx: &mut Context) {
    const VID: u16 = 0x057E;
    const PID: u16 = 0x2009;
    for i in ctx.find(VID, PID, |c| c.usage_page == 1 && (c.usage == 4 || c.usage == 5) && c.input_len >= 13) {
        let c = &ctx.colls[i];
        let key = format!("switchpro:{}", path_hash(&c.path_text));
        let name = if c.product.is_empty() { "Nintendo Switch Pro Controller".into() } else { c.product.clone() };
        let Some(d) = Dev::open(c) else { continue };
        let end = Instant::now() + Duration::from_millis(350);
        while Instant::now() < end {
            let Some(r) = d.read(end.saturating_duration_since(Instant::now()).as_millis() as u64) else { break };
            let Some((percent, charging)) = switch_pro(&r) else { continue };
            let mut s = Status::new(key.clone(), name.clone(), Kind::Gamepad);
            (s.percent, s.charging) = (Some(percent), charging);
            ctx.add(s, VID, PID);
            break;
        }
    }
}

pub fn poll(ctx: &mut Context) {
    for step in [poll_plantronics, poll_turtle_beach, poll_inzone, poll_steam, poll_switch_pro] {
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
        assert_eq!(plantronics_battery(&[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 5, 0]), Some((50, false)));
        assert_eq!(turtle_level(r#"{"GSI":{"240":"85","241":"1"}}"#), Some(85));
        assert_eq!(turtle_level(r#"{"240":"85"}"#), None);
        let mut r = [0u8; 64];
        r[..15].copy_from_slice(&[0x02, 14, 0x04, 0xFF, 0, 0, 0x96, 0xC3, 0x10, 0x04, 0x10, 7, 0, 1, 66]);
        r[15] = sum(&r[5..=14]);
        let e = inzone_event(&r).unwrap();
        assert_eq!((e.tid, e.payload), (7, vec![1, 66]));
        assert_eq!(inzone_buds(&[0, 80, 0, 0xFF, 0, 0]), Some(80));
        assert_eq!(switch_pro(&[0x30, 0, 0x70, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), Some((75, true)));
        let mut s = [0u8; 15];
        (s[0], s[1], s[2]) = (0x43, 2, 90);
        assert_eq!(steam(&s, false), Some((90, true)));
    }
}
