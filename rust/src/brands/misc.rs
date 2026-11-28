//! Souris diverses (Motospeed, Xenta, Delux, Ajazz, Incott, Wraith), Rapoo, DAREU / Sharkoon, RAWM / SADES.
//! Dérivé de BatteryHub (GPL-3.0), src/drivers/misc_mice.cpp, rapoo.cpp, dareu.cpp, rawm.cpp. Lecture seule.

use super::{clamp100, pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};
use std::cell::RefCell;
use std::time::{Duration, Instant};

fn mouse(key: String, name: impl Into<String>) -> Status {
    Status::new(key, name, Kind::Mouse)
}

fn with(mut s: Status, percent: Option<u8>, charging: bool) -> Status {
    (s.percent, s.charging) = (percent, charging);
    s
}

fn name_or(product: &str, fallback: &str) -> String {
    if product.is_empty() { fallback.into() } else { product.into() }
}

fn bit7(value: u8) -> Option<(u8, bool)> {
    (value & 0x7F <= 100).then(|| (value & 0x7F, value & 0x80 != 0))
}

// ---------------------------------------------------------------- Motospeed X6

fn poll_motospeed(ctx: &mut Context) {
    for pid in [0xFFE0, 0xFFF2, 0xFFF1] {
        if ctx.is_claimed(0x0BDA, pid) {
            continue;
        }
        for i in ctx.find(0x0BDA, pid, |c| c.iface == 4 && c.output_len >= 21) {
            let mut s = mouse(format!("motospeed:{pid:04X}"), "Motospeed X6");
            if pid == 0xFFF1 {
                s.charging = true; // sur câble : en charge, pas de lecture
            } else {
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                d.flush();
                let Some(r) = d.query(&[0xB5, 0x06], 1500, |r| r.len() > 20 && r[0] == 0xB4) else { continue };
                let raw = r[20];
                s = with(s, Some(clamp100(i64::from(raw & 0x7F))), raw >= 128);
            }
            ctx.add(s, 0x0BDA, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- Xenta (trames poussées)

/// (modèle, état, niveau) ; trames 03 <modèle> 40|41 <état> <niveau>.
pub fn xenta_frame(r: &[u8]) -> Option<(u8, u8, u8)> {
    (r.len() >= 5 && r[0] == 0x03 && (r[2] == 0x40 || r[2] == 0x41)).then(|| (r[1], r[3], r[4]))
}

/// ARDOR Fury V2 : paliers de 25 %.
pub fn ardor_percent(r: &[u8]) -> Option<u8> {
    if r.len() < 5 || r[..3] != [0x03, 0x00, 0x40] || r[3] & 0x0F != 0x03 {
        return None;
    }
    match r[3] >> 4 {
        0x1 => Some(25),
        0x3 => Some(50),
        0x7 => Some(75),
        0xF => Some(100),
        _ => None,
    }
}

fn xenta_model(code: u8) -> Option<(&'static str, bool)> {
    Some(match code {
        0x73 => ("Inphic IN10R MAX", false),
        0x74 => ("Inphic IN10 MAX", false),
        0x3D | 0x30 | 0x50 => ("Delux M900 Pro", false),
        0x55 => ("Attack Shark X11", false),
        0x4E => ("Attack Shark X3 Max", false),
        0xE5 => ("Attack Shark V3 Pro", false),
        0x24 => ("Attack Shark V6", false),
        0x10 => ("Attack Shark X6 / R1", true),
        _ => return None,
    })
}

/// `agreed` : deux rapports identiques de suite.
pub fn xenta_status(pid: u16, frame: (u8, u8, u8), agreed: bool, ardor: Option<Option<u8>>) -> Status {
    let (code, state, level) = frame;
    let cable = pid == 0xFA55 || pid == 0x2155;
    let model = xenta_model(code);
    let mut s = mouse(format!("xenta:{pid:04X}"), model.map_or("Wireless mouse", |m| m.0));
    let valid = (1..=100).contains(&level);
    if pid == 0xFA65 && (code == 0x30 || code == 0x50) {
        if state == 0x02 {
            s.percent = Some(100);
        } else if state == 0x03 {
            s.charging = true;
            s.percent = Some(level).filter(|&l| l <= 100);
        } else if agreed && state == 0x01 && valid {
            s.percent = Some(level);
        }
    } else if let Some(confirmed) = ardor {
        s.name = "ARDOR GAMING Fury V2".into();
        s.percent = confirmed;
    } else if cable || state == 0x02 || state == 0x03 {
        s.charging = true; // niveau figé sur alimentation externe
        s.percent = (state == 0x02 && !cable).then_some(100);
    } else if agreed {
        s.percent = match model {
            Some((_, true)) => (1..=10).contains(&level).then(|| level * 10),
            _ => (state == 0x01 && valid).then_some(level),
        };
    }
    s.connected = s.percent.is_some() || s.charging;
    s
}

struct Listener {
    path: String,
    dev: Dev,
    last: Vec<u8>,
    previous: Vec<u8>,
    at: Instant,
    /// ARDOR : (candidat, répétitions, valeur confirmée, instant).
    candidate: (Option<u8>, u32),
    confirmed: Option<(u8, Instant)>,
}

thread_local!(static XENTA: RefCell<Vec<Listener>> = const { RefCell::new(Vec::new()) });

fn poll_xenta(ctx: &mut Context) {
    let mut present = Vec::new();
    XENTA.with_borrow_mut(|listeners| {
        for pid in [0xFA60, 0xFA61, 0xFA65, 0xFA55, 0x2155] {
            if ctx.is_claimed(0x1D57, pid) {
                continue;
            }
            for i in ctx.find(0x1D57, pid, |c| c.usage_page == 0x000A && c.input_len >= 5) {
                let path = ctx.colls[i].path_text.clone();
                present.push(path.clone());
                let (index, fresh) = match listeners.iter().position(|l| l.path == path) {
                    Some(index) => (index, false),
                    None => {
                        let Some(dev) = Dev::open(&ctx.colls[i]) else { continue };
                        let listener = Listener {
                            path, dev, last: Vec::new(), previous: Vec::new(), at: Instant::now(), candidate: (None, 0), confirmed: None,
                        };
                        listeners.push(listener);
                        (listeners.len() - 1, true)
                    }
                };
                let l = &mut listeners[index];
                // premier passage : on attend deux rapports (5 s au plus) ; ensuite on vide la file
                let until = Instant::now() + Duration::from_secs(if fresh { 5 } else { 0 });
                let mut got = 0;
                loop {
                    let left = until.saturating_duration_since(Instant::now()).as_millis() as u64;
                    if fresh && got < 2 && left == 0 {
                        break;
                    }
                    let wait = if fresh && got < 2 { left.max(1) } else { 30 };
                    let Some(r) = l.dev.read(wait).filter(|r| r.len() >= 5) else { break };
                    if xenta_frame(&r).is_none() {
                        continue;
                    }
                    l.previous = std::mem::replace(&mut l.last, r[..5].to_vec());
                    l.at = Instant::now();
                    if pid == 0xFA60 && r[1] == 0 {
                        if let Some(percent) = ardor_percent(&r) {
                            l.candidate = if l.candidate.0 == Some(percent) { (Some(percent), (l.candidate.1 + 1).min(2)) } else { (Some(percent), 1) };
                            if l.candidate.1 >= 2 {
                                l.confirmed = Some((percent, l.at));
                            }
                        }
                    }
                    got += 1;
                }
                let active = l.confirmed.filter(|c| pid == 0xFA60 && c.1.elapsed() <= Duration::from_secs(45));
                if l.last.is_empty() || (active.is_none() && l.at.elapsed() > Duration::from_secs(15)) {
                    continue;
                }
                let Some(frame) = xenta_frame(&l.last) else { continue };
                let cable = pid == 0xFA55 || pid == 0x2155;
                let ardor = (pid == 0xFA60 && !cable && ardor_percent(&l.last).is_some()).then(|| active.map(|c| c.0));
                ctx.add(xenta_status(pid, frame, l.previous == l.last, ardor), 0x1D57, pid);
                break;
            }
        }
        listeners.retain(|l| present.contains(&l.path));
    });
}

// ---------------------------------------------------------------- Delux M800

/// (pourcentage, état, souris liée).
pub fn delux(r: &[u8]) -> Option<(u8, u8, bool)> {
    (r.len() >= 20 && r[0] == 0x0C && r[2] == 0x20 && r[18] <= 100).then(|| (r[18], r[19], r[6] != 0))
}

fn poll_delux(ctx: &mut Context) {
    let mut sequence = 0u8;
    for pid in [0x5B2E, 0x5B2F] {
        if ctx.is_claimed(0x248A, pid) {
            continue;
        }
        for i in ctx.find(0x248A, pid, |c| c.usage_page == 0xFF00 && c.usage == 1 && c.feature_len >= 33) {
            let c = &ctx.colls[i];
            let mut ask = || {
                sequence = sequence.wrapping_add(1);
                let mut request = vec![0u8; c.feature_len.max(33)];
                (request[0], request[1], request[2], request[4]) = (0x0C, 0x01, 0x20, sequence);
                if !hid::feature_write(c, &request) {
                    return None;
                }
                pause(20);
                hid::feature_read(c, 0x0C, c.feature_len).and_then(|r| delux(&r))
            };
            let Some(mut answer) = ask() else { continue };
            let receiver = pid == 0x5B2F;
            if receiver && !answer.2 {
                answer = ask().unwrap_or(answer);
            }
            let (percent, state, linked) = answer;
            let mut s = mouse("delux:m800".into(), "Delux M800 Pro");
            s.connected = linked || percent > 0;
            let level = (s.connected && percent > 0).then_some(percent);
            s = match state {
                2 => with(s, Some(100), true),
                1 => with(s, level, true),
                _ => with(s, level, false),
            };
            if !receiver && s.connected {
                ctx.claim(0x248A, 0x5B2F);
            }
            ctx.add(s, 0x248A, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- Ajazz

/// AJ179 : (nom, pourcentage, en charge, souris connectée).
pub fn aj179(r: &[u8]) -> Option<(String, u8, bool, bool)> {
    if r.len() < 32 || r[0] != 0x10 || r[1] != 0 || r[4..31].iter().fold(0u8, |s, &v| s.wrapping_add(v)) != r[31] || r[13] > 100 {
        return None;
    }
    let model: String = r[4..8].iter().take_while(|&&b| (0x20..0x7F).contains(&b)).map(|&b| b as char).collect();
    let name = match model.strip_prefix('M').filter(|_| model.len() == 4) {
        Some(digits) => format!("Ajazz AJ{digits}"),
        None => "Ajazz mouse".into(),
    };
    Some((name, r[13], r[12] != 0, r[14] != 0))
}

fn poll_ajazz(ctx: &mut Context) {
    for vendor in [0x249A, 0x248A] {
        for pid in ctx.products(vendor) {
            for i in ctx.find(vendor, pid, |c| c.iface == 2 && c.output_len == 33 && c.input_len == 33) {
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                d.flush();
                let Some(a) = d.query(&[0x00, 0x10], 1500, |r| aj179(r).is_some()).and_then(|r| aj179(&r)) else { continue };
                let mut s = mouse(format!("ajazz:{vendor:04X}:{pid:04X}"), a.0);
                s.connected = vendor != 0x249A || a.3;
                s = with(s, Some(a.1), a.2);
                ctx.add(s, vendor, pid);
                break;
            }
        }
    }
    // série AJ (récepteurs Rongyuan à rapport 05)
    for pid in ctx.products(0x3151) {
        for i in ctx.find(0x3151, pid, |c| c.usage_page == 0xFFFF && c.usage == 2 && c.feature_len >= 65) {
            let c = &ctx.colls[i];
            let mut request = vec![0u8; c.feature_len];
            request[1] = 0xF7;
            if !hid::feature_write(c, &request) {
                continue;
            }
            pause(30);
            let level = hid::feature_read(c, 0x05, c.feature_len).filter(|r| r.len() >= 4 && r[..3] == [0x05, 0, 0] && r[3] <= 100);
            let Some(r) = level else { continue };
            let mut s = mouse(format!("ajazzaj:{:04X}:{pid:04X}", 0x3151), name_or(&c.product, "AJAZZ mouse"));
            s.connected = r[3] > 0;
            s.percent = Some(r[3]).filter(|&v| v > 0);
            ctx.add(s, 0x3151, pid);
            break;
        }
    }
    // AJ139
    for vendor in [0xA8A5, 0xA8A4] {
        const PID: u16 = 0x2255;
        for i in ctx.find(vendor, PID, |c| c.usage_page == 0xFF01 && c.usage == 0x10 && c.output_len >= 65 && c.input_len >= 65) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let ok = |r: &[u8]| r.len() > 9 && r[0] == 0xAA && r[1] == 0x30;
            let reply = d
                .query(&[0x00, 0x55, 0x30, 0xA5, 0x2E, 0x2E], 700, ok)
                .or_else(|| d.query(&[0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01], 800, ok));
            let Some(r) = reply.filter(|r| r[8] <= 100) else { continue };
            let cable = vendor == 0xA8A4;
            let mut s = mouse(format!("ajazz139:{vendor:04X}:{PID:04X}"), "AJAZZ wireless mouse");
            s.connected = cable || r[8] > 0;
            ctx.add(with(s, Some(r[8]), r[9] != 0), vendor, PID);
            break;
        }
    }
}

// ---------------------------------------------------------------- Wraith W1 et Incott (mêmes identifiants PixArt)

/// Code modèle de la lecture d'infos : `Some(None)` répond mais n'est pas un W1.
pub fn w1_info(r: &[u8]) -> Option<Option<u8>> {
    (r.len() >= 4 && r[0] == 0x09 && r[1] == 0x8F).then(|| Some(r[3]).filter(|&m| m == 18 || m == 20))
}

thread_local!(static W1_MODELS: RefCell<[Option<u8>; 2]> = const { RefCell::new([None, None]) });

fn pixart(ctx: &Context, pid: u16) -> Vec<usize> {
    let mut found = ctx.find(0x093A, pid, |c| c.usage_page == 0xFF05 && c.feature_len >= 9);
    found.reverse(); // sur câble, la première collection refuse SET_FEATURE
    found
}

/// Lit la batterie d'un W1 : on demande l'état des boutons et on écoute les rapports d'état.
fn w1_battery(ctx: &Context, c: &Info) -> Option<(u8, bool)> {
    let inputs: Vec<Dev> = ctx
        .find(0x093A, c.pid, |x| x.usage_page == 0xFF05 && x.input_len >= 2)
        .iter()
        .filter_map(|&i| Dev::open(&ctx.colls[i]))
        .collect();
    inputs.iter().for_each(Dev::flush);
    if inputs.is_empty() {
        return None;
    }
    let button = |b: u8| {
        if !hid::feature_write(c, &[0x09, 0x86, b]) {
            return false;
        }
        for wait in [150, 20] {
            pause(wait);
            match hid::feature_read(c, 0x09, c.feature_len) {
                Some(f) if f.len() >= 4 && f[1] == 0x86 => break,
                Some(_) => {}
                None => break,
            }
        }
        true
    };
    let listen = |ms: u64| {
        let end = Instant::now() + Duration::from_millis(ms);
        while Instant::now() < end {
            for d in &inputs {
                if let Some(found) = d.read(50).filter(|r| r.len() >= 2 && r[0] == 0x09).and_then(|r| bit7(r[1])) {
                    return Some(found);
                }
            }
        }
        None
    };
    pause(200);
    let sent = [0u8, 1, 2, 4, 3].into_iter().fold(false, |any, b| button(b) || any);
    if !sent {
        return None;
    }
    let mut found = listen(400);
    for _ in 0..4 {
        if found.is_some() || !button(0) {
            break;
        }
        found = listen(550);
    }
    found
}

fn poll_wraith(ctx: &mut Context) {
    for (n, pid) in [0x522C, 0x622C].into_iter().enumerate() {
        if ctx.is_claimed(0x093A, pid) {
            continue;
        }
        let found = pixart(ctx, pid);
        if found.is_empty() {
            continue;
        }
        let mut reply = None;
        let mut used = None;
        for i in found {
            let c = &ctx.colls[i];
            if !hid::feature_write(c, &[0x09, 0x8F]) {
                continue;
            }
            reply = (0..10).find_map(|_| {
                pause(50);
                hid::feature_read(c, 0x09, c.feature_len).and_then(|r| w1_info(&r))
            });
            if reply.is_some() {
                used = Some(i);
                break;
            }
        }
        if reply == Some(None) {
            W1_MODELS.with_borrow_mut(|m| m[n] = None);
            continue; // autre souris PixArt : laissée à Incott
        }
        if let Some(Some(model)) = reply {
            W1_MODELS.with_borrow_mut(|m| m[n] = Some(model));
        }
        let Some(model) = W1_MODELS.with_borrow(|m| m[n]) else { continue };
        let name = if model == 20 { "Wraith W1 Freestyle" } else { "Wraith W1" };
        let mut s = mouse(format!("wraithw1:{pid:04X}"), name);
        let battery = used.and_then(|i| w1_battery(ctx, &ctx.colls[i]));
        let cable = pid == 0x622C;
        match battery {
            Some((percent, charging)) if cable || percent > 0 || charging => s = with(s, Some(percent), charging),
            _ => s.connected = false,
        }
        ctx.add(s, 0x093A, pid); // réservé même endormi, pour qu'Incott ne l'interroge jamais
    }
}

fn poll_incott(ctx: &mut Context) {
    for pid in [0x522C, 0x622C] {
        if ctx.is_claimed(0x093A, pid) {
            continue;
        }
        for i in pixart(ctx, pid) {
            let c = &ctx.colls[i];
            if !hid::feature_write(c, &[0x09, 0x89]) {
                continue;
            }
            let found = (0..10).find_map(|_| {
                pause(50);
                let r = hid::feature_read(c, 0x09, c.feature_len)?;
                (r.len() >= 4 && r[0] == 0x09 && r[1] == 0x89 && (r[2] | r[3]) != 0).then(|| bit7(r[2])).flatten()
            });
            let Some((percent, charging)) = found else { continue };
            let mut s = mouse(format!("incott:{pid:04X}"), "Incott mouse");
            s.connected = pid == 0x622C || percent > 0;
            ctx.add(with(s, Some(percent), charging), 0x093A, pid);
            break;
        }
    }
}

pub fn poll(ctx: &mut Context) {
    poll_motospeed(ctx);
    poll_xenta(ctx);
    poll_delux(ctx);
    poll_ajazz(ctx);
    poll_wraith(ctx); // avant Incott : mêmes identifiants
    poll_incott(ctx);
}

// ---------------------------------------------------------------- Rapoo (rapports passifs)

#[derive(Debug, PartialEq)]
pub struct Rapoo {
    pub percent: Option<u8>,
    pub charging: bool,
    pub cable: bool,
    pub mouse: u16,
}

/// `legacy` : disposition des anciens VT3 Pro (BB B0).
pub fn rapoo(d: &[u8], legacy: bool) -> Option<Rapoo> {
    let (state, percent, cable, mouse) = if !legacy && d.first() == Some(&0x07) {
        if d.len() < 9 || d[1] & 0x0F > 2 {
            return None;
        }
        let mouse = if d.len() >= 15 { u16::from_le_bytes([d[13], d[14]]) } else { 0 };
        (d[7], d[8], d[1] & 0x0F == 2, mouse)
    } else if legacy && d.first() == Some(&0xBB) {
        if d.len() < 7 || d[1] != 0xB0 {
            return None;
        }
        (d[5], d[6], false, 0)
    } else {
        return None;
    };
    if (state != 1 && state != 2) || (percent > 100 && !(legacy && state == 2 && percent == 0x7F)) {
        return None;
    }
    Some(Rapoo { percent: Some(percent).filter(|&p| p <= 100), charging: state == 2, cable, mouse })
}

const RAPOO: [(u16, u16, &str); 17] = [
    (0x4610, 0x1410, "Rapoo VT0"), (0x4620, 0x1420, "Rapoo VT0 Max"), (0x4615, 0x1415, "Rapoo VT1"), (0x4644, 0x1444, "Rapoo VT1 Max"),
    (0x4622, 0x1422, "Rapoo VT2"), (0x4623, 0x1423, "Rapoo VT2 Max"), (0x4612, 0x1412, "Rapoo VT3"), (0x4617, 0x1417, "Rapoo VT3 Max (Gen 2)"),
    (0x4B06, 0x1B06, "Rapoo VT3 Max V2"), (0x4611, 0x1411, "Rapoo VT3S"), (0x4621, 0x1421, "Rapoo VT3S Max"), (0x4613, 0x1413, "Rapoo VT7"),
    (0x4616, 0x1416, "Rapoo VT7 Max"), (0x4614, 0x1414, "Rapoo VT9 (Gen 2)"), (0x4618, 0x1418, "Rapoo VT9 Max"), (0x4415, 0x1215, "Rapoo VT3 Pro"),
    (0x4433, 0x1233, "Rapoo VT1 Pro Max"),
];

thread_local!(static RAPOO_OPEN: RefCell<Vec<(String, Dev)>> = const { RefCell::new(Vec::new()) });

pub fn poll_rapoo(ctx: &mut Context) {
    let mut present = Vec::new();
    RAPOO_OPEN.with_borrow_mut(|open| {
        for pid in ctx.products(0x24AE) {
            let legacy_pid = [0x1215, 0x4415, 0x1233, 0x4433, 0x1244].contains(&pid);
            let found = ctx.find(0x24AE, pid, |c| {
                c.usage_page == 0xFF00
                    && c.usage == 2
                    && (c.input_len == 16 || c.input_len == 19 || (c.input_len == 13 && pid == 0x1417) || (c.input_len == 7 && legacy_pid))
            });
            for i in found {
                let (path, legacy) = (ctx.colls[i].path_text.clone(), ctx.colls[i].input_len == 7);
                present.push(path.clone());
                let (index, fresh) = match open.iter().position(|o| o.0 == path) {
                    Some(index) => (index, false),
                    None => {
                        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                        open.push((path, d));
                        (open.len() - 1, true)
                    }
                };
                let d = &open[index].1;
                let queued = (!fresh).then(|| {
                    (0..64).map_while(|_| d.read(30)).filter_map(|r| rapoo(&r, legacy)).last()
                });
                let reading = queued.flatten().or_else(|| {
                    // la souris envoie son état de temps en temps : jusqu'à 5 s d'attente
                    d.wait_for(5000, |r| rapoo(r, legacy).is_some()).and_then(|r| rapoo(&r, legacy))
                });
                let Some(reading) = reading else { continue };
                let wanted = if reading.mouse != 0 { reading.mouse } else { pid };
                let model = RAPOO.iter().find(|m| m.0 == wanted || m.1 == wanted);
                let identity = model.map_or(wanted, |m| m.0);
                let mut name = model.map_or_else(|| ctx.colls[i].product.clone(), |m| m.2.to_string());
                if pid == 0x1244 {
                    name = "Rapoo VT3 Pro Max".into();
                }
                if name.is_empty() || name == "Rapoo Gaming Device" {
                    name = "Rapoo wireless mouse".into();
                }
                let s = with(mouse(format!("rapoo:{identity:04X}"), name), reading.percent, reading.charging);
                match ctx.out.iter_mut().find(|o| o.key == s.key) {
                    Some(existing) => {
                        existing.charging |= reading.cable && s.charging;
                        existing.percent = existing.percent.or(s.percent);
                        ctx.claim(0x24AE, pid);
                    }
                    None => ctx.add(s, 0x24AE, pid),
                }
                break;
            }
        }
        open.retain(|o| present.contains(&o.0));
    });
}

// ---------------------------------------------------------------- DAREU / Sharkoon

pub fn togran_request(slot: u8, len: u8, class: u8, command: u8) -> [u8; 65] {
    let mut r = [0u8; 65];
    (r[0], r[1], r[2], r[3], r[4]) = (0x0A, slot << 4, len, class, command);
    r
}

pub fn togran_battery(b: &[u8]) -> Option<(u8, bool)> {
    if b.len() < 10 {
        return None;
    }
    let max = if b[9] != 0 { b[9] } else { 100 };
    (b[8] <= max).then(|| ((u32::from(b[8]) * 100 / u32::from(max)) as u8, b[7] == 1))
}

/// (emplacement, produit de la souris).
pub fn togran_list(r: &[u8]) -> Vec<(u8, u16)> {
    let count = r.get(7).copied().unwrap_or(0).min(5);
    (1..=count)
        .filter_map(|slot| {
            let e = 8 + usize::from(slot - 1) * 3;
            (e + 2 < r.len() && r[e] != 0).then(|| (slot, u16::from_be_bytes([r[e + 1], r[e + 2]])))
        })
        .collect()
}

const DAREU: u16 = 0x260D;

fn togran(c: &Info, slot: u8, len: u8, class: u8, command: u8) -> Option<Vec<u8>> {
    if !hid::feature_write(c, &togran_request(slot, len, class, command)) {
        return None;
    }
    for _ in 0..15 {
        pause(30);
        let r = hid::feature_read(c, 0x0A, c.feature_len).filter(|r| r.len() >= 11)?;
        if r[1] & 0x0F == 2 && r[3] == class && r[4] == command {
            return Some(r);
        }
    }
    None
}

pub fn poll_dareu(ctx: &mut Context) {
    for pid in [0x1131, 0x1148] {
        let found = ctx.find(DAREU, pid, |c| c.usage_page == 0x000C && c.usage == 1 && c.feature_len == 65);
        if ctx.is_claimed(DAREU, pid) || found.len() != 1 {
            continue;
        }
        let slots = if pid == 0x1131 {
            togran(&ctx.colls[found[0]], 0, 6, 0x00, 0xA0).map_or_else(Vec::new, |r| togran_list(&r))
        } else {
            vec![(0, pid)]
        };
        for (slot, model) in slots {
            let Some((percent, charging)) = togran(&ctx.colls[found[0]], slot, 4, 0x07, 0x80).and_then(|r| togran_battery(&r)) else {
                continue;
            };
            let suffix = if pid == model { String::new() } else { format!(":{model:04X}") };
            let name = if model == 0x1148 { "DAREU A980 Pro".into() } else { format!("DAREU {model:04X}") };
            ctx.add(with(mouse(format!("dareu:260D:{pid:04X}{suffix}"), name), Some(percent), charging), DAREU, pid);
        }
    }

    // clavier A84
    const A84: u16 = 0x0022;
    let found = ctx.find(DAREU, A84, |c| c.iface == 0 && c.usage_page == 1 && c.usage == 6 && c.feature_len == 65);
    if !ctx.is_claimed(DAREU, A84) && found.len() == 1 {
        let c = &ctx.colls[found[0]];
        let mut request = [0u8; 65];
        (request[1], request[2]) = (0x04, 0xF6);
        if hid::feature_write(c, &request) {
            pause(10);
            for _ in 0..3 {
                hid::feature_read(c, 0, c.feature_len); // le pilote officiel écarte ces lectures
                pause(10);
            }
            let level = (0..3)
                .find_map(|_| {
                    pause(10);
                    hid::feature_read(c, 0, c.feature_len).filter(|r| r.len() >= 16 && r[15] != 0)
                })
                .map(|r| r[5]);
            if let Some(level) = level.filter(|&l| l <= 100) {
                let s = Status::new(format!("dareu:{DAREU:04X}:{A84:04X}"), "DAREU A84", Kind::Keyboard);
                ctx.add(with(s, Some(level), false), DAREU, A84);
            }
        }
    }

    // EM901X et Sharkoon SGM50W : requête par fonctionnalité, réponse par rapport d'entrée
    let mut em901x_read = false;
    for pid in [0x1090, 0x1074, 0x1125] {
        if ctx.is_claimed(DAREU, pid) || (em901x_read && pid == 0x1074) {
            continue;
        }
        let inputs = ctx.find(DAREU, pid, |c| c.iface == 1 && c.usage_page == 0xFF01 && c.usage == 0 && c.input_len == 17);
        let features = ctx.find(DAREU, pid, |c| c.iface == 1 && c.usage_page == 0xFF02 && c.usage == 2 && c.feature_len == 17);
        if inputs.len() != 1 || features.len() != 1 {
            continue;
        }
        let Some(input) = Dev::open(&ctx.colls[inputs[0]]) else { continue };
        input.flush();
        let mut request = [0u8; 17];
        (request[0], request[1], request[16]) = (0x08, 0x04, 0x49);
        if !hid::feature_write(&ctx.colls[features[0]], &request) {
            continue;
        }
        let ok = |r: &[u8]| r.len() == 17 && r[0] == 0x09 && r[1] == 0x04 && r[2] == 0 && r[6] <= 100;
        let Some(r) = input.wait_for(1000, ok) else { continue };
        let name = if pid == 0x1125 { "Sharkoon Skiller SGM50W" } else { "DAREU EM901X" };
        ctx.add(with(mouse(format!("dareu:{DAREU:04X}:{pid:04X}"), name), Some(r[6]), r[7] != 0), DAREU, pid);
        em901x_read = pid != 0x1125;
    }
}

// ---------------------------------------------------------------- RAWM / SADES

fn rawm_packet(event: &[u8], channel: Option<u8>) -> Vec<u8> {
    let mut p = vec![0x00];
    if let Some(c) = channel {
        p.push(0xC0 | (c & 0x3F));
    }
    p.push(0x80 | (event.len() as u8 & 0x3F));
    p.extend_from_slice(event);
    p
}

pub fn json_int(json: &str, key: &str) -> Option<i64> {
    let rest = &json[json.find(&format!("\"{key}\":"))? + key.len() + 3..];
    let end = rest.char_indices().find(|(i, c)| !(c.is_ascii_digit() || (*i == 0 && (*c == '-' || *c == '+')))).map_or(rest.len(), |p| p.0);
    rest[..end].parse().ok()
}

pub fn json_text(json: &str, key: &str) -> String {
    let marker = format!("\"{key}\":\"");
    json.find(&marker).and_then(|at| json[at + marker.len()..].split_once('"')).map(|p| p.0.to_string()).unwrap_or_default()
}

/// Flux d'un côté de la liaison : trames FF FF FF FF, informations en JSON et notifications de batterie.
#[derive(Default)]
pub struct Rawm {
    buffer: Vec<u8>,
    pub info: Option<String>,
    pub percent: Option<u8>,
    pub charging: bool,
}

impl Rawm {
    pub fn append(&mut self, data: &[u8]) {
        self.buffer.extend_from_slice(data);
        if self.buffer.len() > 8192 {
            self.buffer.drain(..self.buffer.len() - 4096);
        }
        let mut o = 0;
        while o + 6 <= self.buffer.len() {
            if self.buffer[o..o + 4] != [0xFF; 4] {
                o += 1;
                continue;
            }
            let h = o + 4;
            let command = self.buffer[h] & 0x0F;
            let len = ((usize::from(self.buffer[h]) << 4) & 0xF00) | usize::from(self.buffer[h + 1]);
            if len < 2 {
                o = h;
                continue;
            }
            if h + len > self.buffer.len() {
                break;
            }
            let body = self.buffer[h + 2..h + len].to_vec();
            if command == 0x02 {
                let text: String = body.iter().take_while(|&&b| b != 0).map(|&b| b as char).collect();
                if text.starts_with('{') {
                    if let Some(level) = json_int(&text, "battery").filter(|v| (0..=100).contains(v)) {
                        self.percent = Some(level as u8);
                    }
                    if let Some(c) = json_int(&text, "chr") {
                        self.charging = c == 1;
                    }
                    self.info = Some(text);
                }
            } else if command == 0x0B && body.len() >= 3 && body[0] == 0x17 && body[1] <= 100 {
                self.percent = Some(body[1]);
                self.charging = body[2] == 1;
            }
            o = h + len;
        }
        self.buffer.drain(..o);
    }
}

/// Rapports reçus : `[0]` = l'appareil USB, `[1]` = la souris derrière un récepteur (canal 0).
fn rawm_feed(r: &[u8], sides: &mut [Rawm; 2]) {
    let Some(&first) = r.first() else { return };
    let (side, offset) = if first & 0xC0 == 0xC0 {
        if first & 0x3F != 0 || r.len() <= 1 {
            return;
        }
        (1, 1)
    } else {
        (0, 0)
    };
    if r[offset] & 0xC0 != 0x80 {
        return;
    }
    let n = usize::from(r[offset] & 0x3F).min(r.len() - offset - 1);
    sides[side].append(&r[offset + 1..offset + 1 + n]);
}

fn rawm_query(d: &Dev, sides: &mut [Rawm; 2], channel: Option<u8>, deadline: Instant) -> bool {
    let side = usize::from(channel.is_some());
    let seconds = crate::state::now();
    let mut event = vec![0x01, 0, 0x03, 0, 0];
    event.extend_from_slice(&seconds.to_le_bytes());
    event[1] = event.len() as u8;
    if !d.write(&rawm_packet(&event, channel)) {
        return false;
    }
    while Instant::now() < deadline {
        if !d.write(&rawm_packet(&[0x0E, 4, 0, 0], channel)) {
            return false;
        }
        let end = deadline.min(Instant::now() + Duration::from_millis(80));
        while Instant::now() < end {
            let Some(r) = d.read(end.saturating_duration_since(Instant::now()).as_millis() as u64) else { break };
            rawm_feed(&r, sides);
        }
        if sides[side].info.is_some() {
            return true;
        }
    }
    false
}

pub fn poll_rawm(ctx: &mut Context) {
    const VID: u16 = 0x1915;
    const RECEIVERS: [u16; 9] = [0x232A, 0x232B, 0x2335, 0x2336, 0x233B, 0x233C, 0x2346, 0x2347, 0x234B];
    const MICE: [u16; 25] = [
        0x2328, 0x2329, 0x232C, 0x232D, 0x232E, 0x232F, 0x2330, 0x2331, 0x2332, 0x2333, 0x2334, 0x2337, 0x2338, 0x2339, 0x233A, 0x233E,
        0x233F, 0x2340, 0x2341, 0x2343, 0x2344, 0x2348, 0x2349, 0x234A, 0x2352,
    ];
    for pid in ctx.products(VID) {
        let receiver = RECEIVERS.contains(&pid);
        if !receiver && !MICE.contains(&pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF00 && c.usage == 1 && c.input_len == 65 && c.output_len == 65) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let deadline = Instant::now() + Duration::from_millis(1200);
            let mut sides = <[Rawm; 2]>::default();
            if !rawm_query(&d, &mut sides, None, deadline) {
                continue;
            }
            if receiver && !rawm_query(&d, &mut sides, Some(0), deadline) {
                ctx.claim(VID, pid); // récepteur trouvé, souris éteinte
                break;
            }
            let side = &sides[usize::from(receiver)];
            let Some(percent) = side.percent else { continue };
            let info = side.info.clone().unwrap_or_default();
            let product = json_int(&info, "pi").filter(|v| (1..=0xFFFF).contains(v)).map_or(pid, |v| v as u16);
            let mut name = json_text(&info, "dn");
            if name.is_empty() {
                name = "wireless mouse".into();
            }
            if !name.contains("RAWM") && !name.contains("SADES") {
                name = format!("RAWM {name}");
            }
            let s = with(mouse(format!("rawm:{product:04X}"), name), Some(percent), side.charging);
            match ctx.out.iter().position(|o| o.key == s.key) {
                Some(index) => {
                    if !receiver {
                        ctx.out[index] = s;
                    }
                    ctx.claim(VID, pid);
                }
                None => ctx.add(s, VID, pid),
            }
            break;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xenta_and_ardor() {
        assert_eq!(xenta_frame(&[3, 0x55, 0x40, 1, 77]), Some((0x55, 1, 77)));
        assert_eq!(ardor_percent(&[3, 0, 0x40, 0x73, 0]), Some(75));
        let s = xenta_status(0xFA60, (0x10, 1, 7), true, None);
        assert_eq!((s.percent, s.name.as_str()), (Some(70), "Attack Shark X6 / R1"));
        assert!(!xenta_status(0xFA60, (0x55, 1, 77), false, None).connected);
        assert!(xenta_status(0xFA55, (0x55, 1, 77), true, None).charging);
    }

    #[test]
    fn other_frames() {
        let mut a = [0u8; 32];
        (a[0], a[4], a[5], a[6], a[7], a[13], a[14]) = (0x10, b'M', b'1', b'7', b'9', 64, 1);
        a[31] = a[4..31].iter().fold(0u8, |s, &v| s.wrapping_add(v));
        assert_eq!(aj179(&a), Some(("Ajazz AJ179".into(), 64, false, true)));
        assert_eq!(w1_info(&[9, 0x8F, 0, 20]), Some(Some(20)));
        assert_eq!(w1_info(&[9, 0x8F, 0, 3]), Some(None));
        assert_eq!(rapoo(&[7, 0, 0, 0, 0, 0, 0, 1, 66], false).unwrap().percent, Some(66));
        assert_eq!(rapoo(&[0xBB, 0xB0, 0, 0, 0, 2, 0x7F], true).unwrap().percent, None);
        assert_eq!(togran_battery(&[0, 0, 0, 0, 0, 0, 0, 1, 100, 200]), Some((50, true)));
        assert_eq!(togran_list(&[0, 0, 0, 0, 0, 0, 0, 1, 1, 0x11, 0x48]), vec![(1, 0x1148)]);
    }

    #[test]
    fn rawm_stream() {
        let json = br#"{"battery":81,"chr":1,"dn":"Leviathan V4","pi":9000}"#;
        let len = json.len() + 2;
        let mut frame = vec![0xFF; 4];
        frame.extend_from_slice(&[0x02 | ((len >> 4) & 0xF0) as u8, len as u8]);
        frame.extend_from_slice(json);
        let mut side = Rawm::default();
        side.append(&frame[..10]);
        assert!(side.info.is_none());
        side.append(&frame[10..]);
        assert_eq!((side.percent, side.charging), (Some(81), true));
        assert_eq!(json_text(side.info.as_ref().unwrap(), "dn"), "Leviathan V4");
        assert_eq!(json_int(side.info.as_ref().unwrap(), "pi"), Some(9000));
    }
}
