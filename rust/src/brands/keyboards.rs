//! AULA, MCHOSE, Rongyuan, SONiX et Royal Kludge. Dérivé de BatteryHub (GPL-3.0), src/drivers/aula*.cpp, mchose*.cpp,
//! rongyuan.cpp, sonix.cpp, royal_kludge.cpp. Lecture seule.

use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};
use std::cell::{Cell, RefCell};
use std::time::{Duration, Instant};

fn status(key: String, name: impl Into<String>, kind: Kind, battery: Option<(u8, bool)>) -> Status {
    let mut s = Status::new(key, name, kind);
    match battery {
        Some((p, c)) => (s.percent, s.charging) = (Some(p), c),
        None => s.connected = false,
    }
    s
}

fn product_or(product: &str, fallback: &str) -> String {
    if product.is_empty() { fallback.into() } else { product.into() }
}

fn without_24g(product: &str) -> &str {
    product.strip_suffix(" 2.4G").filter(|p| !p.is_empty()).unwrap_or(product)
}

fn sum(b: &[u8]) -> u8 {
    b.iter().fold(0u8, |s, &v| s.wrapping_add(v))
}

// ---------------------------------------------------------------- AULA (claviers « BY »)

const BY_RECEIVERS: [(u16, u16); 4] = [(0x3554, 0xFA09), (0x3554, 0xFB40), (0x41E4, 0x2001), (0x05AC, 0x024F)];

pub fn by_request(command: u8, packets: u8) -> [u8; 20] {
    let mut r = [0u8; 20];
    (r[0], r[1], r[2]) = (0x13, command, packets);
    r[19] = sum(&r[..19]);
    r
}

pub fn by_is_reply(r: &[u8], command: u8) -> bool {
    if r.len() < 20 || r[0] != 0x13 || r[1] & 0x7F != command || r[4..19].iter().all(|&b| b == 0) {
        return false;
    }
    // MCHOSE laisse la somme à 0
    r[19] == sum(&r[..19]) || (r[19] == 0 && r[2] == 1 && r[3] == 0)
}

pub fn by_model(r: &[u8]) -> Option<&'static str> {
    const MODELS: [([u8; 6], &str); 20] = [
        ([3, 0, 0, 0, 2, 0x1C], "MCHOSE G87"), ([3, 0, 0, 0, 0, 0x77], "MCHOSE K99"), ([3, 0, 0, 0, 1, 0xDF], "MCHOSE K99 V2"),
        ([3, 0, 0, 0, 1, 0x32], "MCHOSE G98"), ([3, 0, 0, 0, 0, 0xD6], "MCHOSE X75"), ([0x0A, 0, 0, 0, 0, 3], "MCHOSE G98 Pro"),
        ([3, 0, 0, 0, 1, 0xC6], "MCHOSE X75 V2"), ([3, 0, 0, 0, 1, 0x15], "MCHOSE G75 Pro"), ([0x0A, 0, 0, 0, 0, 0x18], "MCHOSE G98 V2"),
        ([0x0A, 0, 0, 0, 0, 0x2B], "MCHOSE UT98"), ([0x0A, 0, 0, 0, 0, 1], "MCHOSE Z75"), ([0x0A, 0, 0, 0, 0, 9], "MCHOSE Z75S"),
        ([3, 0, 0, 0, 1, 0xC7], "MCHOSE KX75"), ([3, 0, 0, 0, 1, 0x74], "MCHOSE GX87"), ([3, 0, 0, 0, 1, 0xED], "MCHOSE GX87 V2"),
        ([3, 0, 0, 0, 1, 0x35], "MCHOSE K87"), ([3, 0, 0, 0, 3, 0x35], "MCHOSE G75 V2"), ([0x0A, 0, 0, 0, 0, 0x34], "MCHOSE G87 V2"),
        ([3, 0, 0, 0, 0, 0x6A], "Redragon Horus Pro"), ([3, 0, 0, 0, 3, 0xDA], "Redragon Horus Pro V2"),
    ];
    [5usize, 4].iter().find_map(|&at| {
        let code = r.get(at..at + 6)?;
        MODELS.iter().find(|m| m.0 == code).map(|m| m.1)
    })
}

/// La requête est renvoyée toutes les 1,3 s : le clavier peut dormir.
fn by_exchange(d: &Dev, command: u8, total_ms: u64) -> Option<Vec<u8>> {
    d.flush();
    let end = Instant::now() + Duration::from_millis(total_ms);
    let mut packet = 0u8;
    while Instant::now() < end {
        if !d.write(&by_request(command, packet & 1)) {
            return None;
        }
        packet = packet.wrapping_add(1);
        let wait = end.saturating_duration_since(Instant::now()).min(Duration::from_millis(1300)).as_millis() as u64;
        if let Some(r) = d.wait_for(wait.max(1), |r| by_is_reply(r, command)) {
            return Some(r);
        }
    }
    None
}

thread_local! {
    /// Par récepteur : (a déjà répondu, nombre d'échecs, premier échec, prochaine tentative).
    static BACKOFF: RefCell<[(bool, u32, Option<Instant>, Option<Instant>); 4]> = const { RefCell::new([(false, 0, None, None); 4]) };
}

fn poll_by(ctx: &mut Context) {
    for (n, &(vid, pid)) in BY_RECEIVERS.iter().enumerate() {
        if ctx.find(vid, pid, |_| true).is_empty() {
            BACKOFF.with_borrow_mut(|b| b[n] = (false, 0, None, None));
            continue;
        }
        let state = BACKOFF.with_borrow(|b| b[n]);
        if ctx.is_claimed(vid, pid) || (!state.0 && state.3.is_some_and(|next| Instant::now() < next)) {
            continue;
        }
        let apple = vid == 0x05AC && pid == 0x024F;
        // sous l'identifiant Apple : seulement la disposition MCHOSE, sinon c'est un récepteur SONiX
        if apple && ctx.find(vid, pid, |c| c.iface == 1 && c.usage_page == 0xFF04 && c.usage == 2 && c.feature_len > 0 && c.input_len == 0).is_empty() {
            continue;
        }
        let found = ctx.find(vid, pid, |c| {
            if apple {
                c.iface == 1 && c.usage_page == 0xFF02 && c.usage == 2 && c.input_len == 20 && c.output_len == 20
            } else {
                (c.usage_page == 0xFF02 || c.usage_page == 0xFF04) && c.usage == 2 && c.output_len >= 20 && c.input_len >= 20
            }
        });
        let (mut tried, mut answered) = (false, false);
        for i in found {
            if ctx.out_of_time() {
                return;
            }
            let product = ctx.colls[i].product.clone();
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let generic = product.contains("2.4G Wireless Receiver") || product.contains("MCHOSE 2.4G Wireless");
            let model = if generic || apple { by_exchange(&d, 0x05, 1300).and_then(|r| by_model(&r)) } else { None };
            let mchose = vid == 0x41E4 || apple || model.is_some();
            tried = true;
            let Some(r) = by_exchange(&d, 0x4A, 4000) else { continue };
            if r.len() < 7 || !(1..=100).contains(&r[5]) {
                continue;
            }
            answered = true;
            let name = match model {
                Some(m) => m.into(),
                None if apple && !generic && !product.is_empty() => product,
                None if mchose => "MCHOSE wireless keyboard".into(),
                None => "AULA wireless keyboard".into(),
            };
            ctx.add(status(format!("aula:{vid:04X}:{pid:04X}"), name, Kind::Keyboard, Some((r[5], r[6] >> 4 == 1))), vid, pid);
            break;
        }
        BACKOFF.with_borrow_mut(|b| {
            let s = &mut b[n];
            if answered {
                s.0 = true;
            } else if tried && !s.0 {
                let now = Instant::now();
                let first = *s.2.get_or_insert(now);
                s.1 += 1;
                if s.1 >= 3 && now.duration_since(first) >= Duration::from_secs(300) {
                    s.3 = Some(now + Duration::from_secs(300 << (s.1 - 3).min(4)).min(Duration::from_secs(3600)));
                }
            }
        });
    }
}

// ---------------------------------------------------------------- AULA HERO 68 Air

pub fn hero_header(counter: u8, index: u8, kind: u8) -> [u8; 20] {
    let mut f = [0u8; 20];
    (f[0], f[1]) = (0x09, 0x66);
    f[2] = 0x05 | if counter & 4 != 0 { 0x80 } else { 0 };
    f[3] = index | if counter & 2 != 0 { 0x80 } else { 0 };
    f[4] = kind | if counter & 1 != 0 { 0x80 } else { 0 };
    f
}

fn hero_ack(r: &[u8]) -> [u8; 20] {
    let mut f = [0u8; 20];
    (f[0], f[1], f[2], f[3], f[4]) = (0x09, 0x66, r[2], r[3], r[4] & 0x80);
    f[5] = sum(&f[1..5]);
    f
}

fn hero_is_ack(r: &[u8], sent: &[u8]) -> bool {
    r[4] & 0x7F == 0 && r[2] == sent[2] && r[3] == sent[3] && r[4] & 0x80 == sent[4] & 0x80 && r[5] == sum(&r[1..5])
}

#[derive(Default)]
struct Hero {
    level: Option<(u8, bool)>,
    in_reply: bool,
    done: bool,
}

impl Hero {
    /// Lit une trame utile, accuse réception des données et retient la batterie.
    fn read(&mut self, d: &Dev, wait_ms: u64) -> Option<Vec<u8>> {
        let r = d.wait_for(wait_ms, |r| r.len() >= 20 && r[0] == 0x09 && r[1] == 0x66)?;
        match r[4] & 0x7F {
            0x0E if r[19] == sum(&r[1..19]) => {
                d.write(&hero_ack(&r));
                if r[3] & 0x7F == 1 {
                    self.in_reply = false;
                    if r[5] == 0x87 && (1..=100).contains(&r[11]) {
                        self.level = Some((r[11], r[12] & 0x10 != 0));
                        self.in_reply = true;
                    } else if r[5] == 0xFE && r[6] == 0x05 && (1..=100).contains(&r[7]) && self.level.is_none() {
                        self.level = Some((r[7], r[8] & 0x10 != 0));
                    }
                }
            }
            0x07 => {
                d.write(&hero_ack(&r));
                self.done |= self.in_reply;
                self.in_reply = false;
            }
            _ => {}
        }
        Some(r)
    }
}

fn hero_battery(d: &Dev) -> Option<(u8, bool)> {
    let mut message = [0u8; 56];
    (message[0], message[1], message[4]) = (0x09, 0x87, 0x01);
    let mut frames: Vec<[u8; 20]> = (1..=4u8)
        .map(|i| {
            let mut f = hero_header(i, i, 0x0E);
            f[5..19].copy_from_slice(&message[usize::from(i - 1) * 14..usize::from(i) * 14]);
            f[19] = sum(&f[1..19]);
            f
        })
        .collect();
    let mut trailer = hero_header(5, 5, 0x08);
    trailer[12] = 0xFF - sum(&message);
    trailer[13] = sum(&trailer[1..13]);
    frames.push(trailer);

    d.flush();
    let mut link = Hero::default();
    let end = Instant::now() + Duration::from_secs(1);
    let left = || end.saturating_duration_since(Instant::now()).as_millis() as u64;
    for frame in &frames {
        let mut acknowledged = false;
        while !acknowledged && left() > 0 {
            if !d.write(frame) {
                return None;
            }
            let resend = Instant::now() + Duration::from_millis(150);
            while !acknowledged && Instant::now() < resend {
                let wait = resend.saturating_duration_since(Instant::now()).as_millis() as u64;
                let Some(r) = link.read(d, wait.min(left()).max(1)) else { break };
                acknowledged = hero_is_ack(&r, frame);
            }
        }
        if !acknowledged {
            return None;
        }
    }
    while !(link.level.is_some() && link.done) && left() > 0 {
        if link.read(d, left()).is_none() {
            break;
        }
    }
    link.level
}

fn poll_hero(ctx: &mut Context) {
    const VID: u16 = 0x372E;
    const PID: u16 = 0x106C;
    for i in ctx.find(VID, PID, |c| c.usage_page == 0xFF60 && c.usage == 0x61 && c.output_len >= 20 && c.input_len >= 20) {
        let Some(battery) = Dev::open(&ctx.colls[i]).and_then(|d| hero_battery(&d)) else { continue };
        ctx.add(status(format!("aula:{VID:04X}:{PID:04X}"), "AULA wireless keyboard", Kind::Keyboard, Some(battery)), VID, PID);
        break;
    }
}

// ---------------------------------------------------------------- AULA SC580 et souris X9 / V9

/// Réponse « info » de la SC580 : octets 4..31.
pub fn sc_info(r: &[u8]) -> Option<&[u8]> {
    (r.len() >= 32 && r[0] == 0x30 && r[1] == 0 && r[31] == sum(&r[4..31])).then(|| &r[4..31])
}

fn poll_sc580(ctx: &mut Context) {
    const KEY: &str = "aula:sc580";
    for (vid, pid) in [(0x088D, 0x062E), (0x089D, 0x062F)] {
        if ctx.is_claimed(vid, pid) {
            continue;
        }
        if ctx.out.iter().any(|o| o.key == KEY) {
            ctx.claim(vid, pid);
            continue;
        }
        for i in ctx.find(vid, pid, |c| c.usage_page == 0xFF02 && c.usage == 0 && c.output_len >= 33 && c.input_len >= 33) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let mut request = [0u8; 33];
            (request[1], request[3]) = (0x30, 0x01);
            d.flush();
            let end = Instant::now() + Duration::from_secs(1);
            let mut answer = None;
            while answer.is_none() && Instant::now() < end {
                let reply = d.query(&request, 250, |r| r.len() >= 32 && r[0] == 0x30 && (r[1] == 0 || r[1] == 2));
                // Some(None) : le récepteur annonce la souris hors ligne
                answer = reply.and_then(|r| if r[1] == 2 { Some(None) } else { sc_info(&r).map(|d| Some(d.to_vec())) });
            }
            let Some(answer) = answer else { continue };
            ctx.claim(vid, pid);
            let mut s = Status::new(KEY, "AULA SC580", Kind::Mouse);
            match answer {
                // en charge, la souris annonce un 100 fixe : niveau inconnu
                Some(data) if data[10] != 0 && data[9] <= 100 => {
                    s.charging = data[8] == 1;
                    s.percent = (!s.charging).then_some(data[9]);
                }
                Some(data) if data[10] != 0 => break,
                _ => s.connected = false,
            }
            ctx.out.push(s);
            break;
        }
    }
}

pub fn aula_x9_battery(r: &[u8]) -> Option<(u8, bool)> {
    let reply = r.len() >= 10 && r[0] == 4 && r[1..3] == [0, 0] && r[3] == 0x1A && r[4..8] == [0; 4];
    (reply && (1..=100).contains(&r[8]) && r[9] <= 2).then(|| (r[8], r[9] != 0))
}

fn poll_x9(ctx: &mut Context) {
    const VID: u16 = 0x320F;
    const PID: u16 = 0x22A0;
    let fits = |c: &Info| c.iface == 1 && c.usage_page == 0xFF1C && c.usage == 0x92 && c.input_len == 64 && c.output_len == 64;
    for i in ctx.find(VID, PID, fits) {
        if ctx.colls[i].product != "AULA-8K Dongle" {
            continue;
        }
        let link_down = Cell::new(false);
        let battery = Dev::open(&ctx.colls[i]).and_then(|d| {
            let command = |code: u8| {
                let mut r = [0u8; 64];
                (r[0], r[3]) = (4, code);
                r
            };
            // le battement de cœur (04 AA 00 AA … 00/FF) dit si la souris est liée
            let wait = |code: u8, ms: u64| {
                d.wait_for(ms, |r| {
                    if r.len() >= 9 && r[0] == 4 && r[1] == 0xAA && r[3] == 0xAA {
                        link_down.set(r[8] == 0);
                        return false;
                    }
                    r.len() >= 8 && r[0] == 4 && r[3] == code && r[1..3] == [0, 0] && r[4..8] == [0; 4]
                })
            };
            d.flush();
            let started = d.write(&command(1)) && wait(1, 450).is_some();
            let battery = (started && d.write(&command(0x1A))).then(|| wait(0x1A, 350)).flatten().and_then(|r| aula_x9_battery(&r));
            if d.write(&command(2)) {
                wait(2, 200);
            }
            battery
        });
        let battery = battery.filter(|_| !link_down.get());
        ctx.add(status("aula_mouse:320F:22A0".into(), "AULA wireless mouse (X9 / V9)", Kind::Mouse, battery), VID, PID);
        return;
    }
}

pub fn poll_aula(ctx: &mut Context) {
    poll_sc580(ctx);
    poll_by(ctx);
    poll_hero(ctx);
    poll_x9(ctx);
}

// ---------------------------------------------------------------- MCHOSE

/// Souris à rapport 12, octets inversés : `Some(None)` endormie ; `Some(Some(niveau, charge, drapeaux))`.
pub fn mchose_status(r: &[u8], field: u16, other: u16) -> Option<Option<(u8, bool, u8)>> {
    if r.len() < 13 || r[0] != 0x12 || r[1] != 0x06 ^ 0xFF {
        return None;
    }
    let p: Vec<u8> = r[2..13].iter().map(|&b| b ^ 0xFF).collect();
    if p.iter().all(|&b| b == 0) {
        return Some(None);
    }
    let found = u16::from_le_bytes([p[0], p[1]]);
    if (found != field && found != other) || p[9] > 100 {
        return None;
    }
    Some(Some((p[9], p[10] != 0, p[8])))
}

fn mchose_product(ctx: &mut Context, vid: u16, pid: u16, field: u16, other: u16) {
    for i in ctx.find(vid, pid, |c| c.usage_page == 0xFF01 && c.usage == 1 && c.feature_len >= 65) {
        let c = &ctx.colls[i];
        let mut request = vec![0xFFu8; c.feature_len.max(65)];
        (request[0], request[1]) = (0x12, 0x06 ^ 0xFF);
        let mut answer = None;
        for attempt in 0..5 {
            if attempt > 0 {
                pause(100);
            }
            if !hid::feature_write(c, &request) {
                break;
            }
            pause(120);
            let Some(r) = hid::feature_read(c, 0x12, c.feature_len) else { continue };
            match mchose_status(&r, field, other) {
                Some(Some(found)) => {
                    answer = Some(Some(found));
                    break;
                }
                Some(None) => answer = answer.or(Some(None)),
                None => {}
            }
        }
        let Some(answer) = answer else { continue };
        let name = product_or(&c.product, "MCHOSE mouse");
        ctx.add(status(format!("mchose:{pid:04X}"), name, Kind::Mouse, answer.map(|a| (a.0, a.1))), vid, pid);
        break;
    }
}

fn poll_mchose_mice(ctx: &mut Context) {
    for pid in ctx.products(0x5253) {
        mchose_product(ctx, 0x5253, pid, 0x5253, 0x5253);
    }
    for pid in [0x100C, 0x100B, 0x100A, 0x1001, 0x1012] {
        if !ctx.is_claimed(0x3837, pid) {
            mchose_product(ctx, 0x3837, pid, 0x41E4, 0x3837);
        }
    }
}

/// A5 Pro : (niveau, en charge), avec ou sans octet d'identifiant.
pub fn a5_battery(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 9 || (r[0] != 0xA1 && r[1] != 0xA1) {
        return None;
    }
    let at = usize::from(r[1] == 0xA1);
    if r.len() <= at + 7 || r[at + 4] != 0 || r[at + 5] != 0x83 || r[at + 7] == 0xFF {
        return None;
    }
    Some((r[at + 7].min(100), r[at + 6] != 0))
}

thread_local! {
    /// Un récepteur muet n'est montré « endormi » qu'après une première réponse.
    static A5_SEEN: RefCell<Vec<u16>> = const { RefCell::new(Vec::new()) };
}

fn poll_a5(ctx: &mut Context) {
    const VID: u16 = 0x2023;
    for (pid, name, cable) in [
        (0xF013, "MCHOSE A5 Pro / Pro Max", false), (0xF015, "MCHOSE A5 Pro / Pro Max", false), (0xF017, "MCHOSE A5 Pro", true),
        (0xF018, "MCHOSE A5 Pro Max", false), (0xF019, "MCHOSE A5 Pro Max", true),
    ] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let mut found = ctx.find(VID, pid, |c| c.usage_page == 0xFFFF && c.feature_len >= 65);
        found.sort_by_key(|&i| ctx.colls[i].iface != 2);
        let (mut heard, mut level) = (false, None);
        for i in found {
            let c = &ctx.colls[i];
            let mut request = vec![0u8; c.feature_len.max(9)];
            (request[3], request[4], request[6]) = (0x02, 2, 0x83);
            if !hid::feature_write(c, &request) {
                continue;
            }
            heard = true;
            pause(34);
            level = (0..8).find_map(|attempt| {
                if attempt == 3 && hid::feature_write(c, &request) {
                    pause(34);
                }
                let found = hid::feature_read(c, 0, c.feature_len).and_then(|r| a5_battery(&r));
                if found.is_none() {
                    pause(30);
                }
                found
            });
            if level.is_some() {
                break;
            }
        }
        if !heard {
            continue;
        }
        match level {
            Some(_) => A5_SEEN.with_borrow_mut(|s| s.push(pid)),
            None if cable || !A5_SEEN.with_borrow(|s| s.contains(&pid)) => continue,
            None => {}
        }
        ctx.add(status(format!("mchose-a5:{pid:04X}"), name, Kind::Mouse, level), VID, pid);
    }
}

fn poll_g3(ctx: &mut Context) {
    const VID: u16 = 0x3837;
    let mut done = false; // le câble l'emporte sur le 2,4 GHz
    for pid in [0x418C, 0x4245] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF01 && c.usage == 0x10 && c.output_len >= 65 && c.input_len >= 65) {
            if done {
                ctx.claim(VID, pid);
                break;
            }
            let name = product_or(&ctx.colls[i].product, "MCHOSE G3 V2");
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let request = [0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01, 0x00, 0x00];
            let reply = d.query(&request, 1000, |r| r.len() >= 10 && r[0] == 0xAA && r[1] == 0x30);
            let Some(r) = reply.filter(|r| (1..=100).contains(&r[8])) else { continue };
            ctx.add(status("mchose:g3v2".into(), name, Kind::Mouse, Some((r[8], r[9] == 1))), VID, pid);
            done = true;
            break;
        }
    }
}

/// Casque V9 Pro 2 : `Some(None)` casque éteint.
pub fn v9_battery(r: &[u8]) -> Option<Option<(u8, bool)>> {
    if r.len() < 9 || r[0] != 0x55 {
        return None;
    }
    let f = &r[1..];
    if f[0] != 0xAA || f[1] != 0x01 || f[4] != 0 || !((f[5] == 0x01 && f[6] == 0x0B) || (f[5] == 0x04 && f[6] == 0x22)) {
        return None;
    }
    let len = usize::from(f[3]);
    if len < 3 || 6 + len >= f.len() {
        return None;
    }
    let check = if f[2] == 1 { f[6..6 + len].iter().fold(f[2] ^ f[3] ^ f[4] ^ f[5], |x, &v| x ^ v) } else { 0 };
    if f[6 + len] != check {
        return None;
    }
    let body = &f[7..];
    if body[0] == 0 {
        return Some(None);
    }
    (body[1] & 0x7F <= 100).then(|| Some((body[1] & 0x7F, body[1] & 0x80 != 0)))
}

fn poll_v9(ctx: &mut Context) {
    const VID: u16 = 0x3837;
    const PID: u16 = 0x6011;
    for i in ctx.find(VID, PID, |c| c.usage_page == 0xFF07 && c.usage == 0x222 && c.output_len >= 64 && c.input_len >= 64) {
        let name = product_or(&ctx.colls[i].product, "MCHOSE V9 Pro 2");
        let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
        d.flush();
        let request = [0x55, 0xAA, 0x01, 0x00, 0x01, 0x00, 0x01, 0x0B, 0x00];
        let Some(answer) = d.query(&request, 1000, |r| v9_battery(r).is_some()).and_then(|r| v9_battery(&r)) else { continue };
        ctx.add(status("mchose:v9pro2".into(), name, Kind::Headset, answer), VID, PID);
        break;
    }
}

pub fn mchose_keyboard_battery(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 41 || r[0] != 0xAA || r[1] != 0x05 || r[5] != 0 || r[6] != 0 {
        return None;
    }
    let size = usize::from(r[4]);
    if size < 33 || 8 + size > r.len() {
        return None;
    }
    let total = r[4..8].iter().chain(&r[8..8 + size]).fold(0u8, |s, &v| s.wrapping_add(v));
    (total == r[3] && r[40] & 0x7F <= 100).then(|| (r[40] & 0x7F, r[40] >> 7 != 0))
}

thread_local! {
    /// Clavier interrogé au plus toutes les 10 min : (dernière demande, dernière lecture).
    static KEYBOARD: Cell<(Option<Instant>, Option<(u8, bool)>)> = const { Cell::new((None, None)) };
    /// K87 V3 : (prochaine demande, dernière lecture), au plus toutes les 3 min.
    static K87: Cell<(Option<Instant>, Option<(u8, bool)>)> = const { Cell::new((None, None)) };
}

fn poll_mchose_keyboard(ctx: &mut Context) {
    const VID: u16 = 0x3837;
    const PID: u16 = 0x3033;
    let found = ctx.find(VID, PID, |c| {
        c.input_len == 65 && c.output_len == 65 && ((c.usage_page == 1 && c.usage == 0) || (c.usage_page == 0xFF31 && c.usage == 0x74))
    });
    let Some(&first) = found.first() else { return };
    let (asked, mut last) = KEYBOARD.get();
    let ask = asked.is_none_or(|a| a.elapsed() >= Duration::from_secs(600));
    let mut fresh = None;
    if ask {
        for &i in &found {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let request = [0x00, 0x55, 0x05, 0x00, 56, 56, 0x00, 0x00, 0x00];
            fresh = (0..2).find_map(|_| {
                d.query(&request, 500, |r| mchose_keyboard_battery(r).is_some()).and_then(|r| mchose_keyboard_battery(&r))
            });
            if fresh.is_some() {
                break;
            }
        }
        last = fresh.or(last);
        KEYBOARD.set((Some(Instant::now()), last));
    }
    let shown = if ask { fresh } else { last };
    if shown.is_none() && last.is_none() {
        return;
    }
    let product = product_or(without_24g(&ctx.colls[first].product), "MCHOSE keyboard");
    ctx.add(status("mchose:3837:3033".into(), product, Kind::Keyboard, shown), VID, PID);
}

fn xor_range(b: &[u8]) -> u8 {
    b.iter().fold(0, |x, &v| x ^ v)
}

/// Protocole MCHOSE récent (rapport 4D) : `Some(None)` appareil non lié.
pub fn modern_info(r: &[u8], keyboard: bool) -> Option<Option<(u8, bool)>> {
    if r.len() < 10 || r[0] != 0x4D || u16::from_le_bytes([r[4], r[5]]) != 0x0900 {
        return None;
    }
    let len = usize::from(r[3]);
    if 8 + len >= r.len() {
        return None;
    }
    if len < 14 {
        return if keyboard && !(len == 1 && r[8] == 0xFF) { None } else { Some(None) };
    }
    if r[8 + len] != xor_range(&r[2..8 + len]) || u16::from_le_bytes([r[8], r[9]]) != 0x3837 {
        return None;
    }
    if keyboard && (len < 20 || r[10] != 0x23 || r[11] != 0x20) {
        return None;
    }
    if r[18] == 0 {
        return Some(None);
    }
    (r[20] <= 100).then(|| Some((r[20], r[19] == 1)))
}

fn poll_modern(ctx: &mut Context) {
    const VID: u16 = 0x3837;
    for (pid, keyboard) in [(0x1014, false), (0x1018, false), (0x1016, false), (0x102C, false), (0x102D, false), (0x4028, false), (0x1015, true)] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let found = ctx.find(VID, pid, |c| {
            if keyboard {
                c.usage_page == 0xFF80 && c.usage == 1 && c.output_len == 64 && c.input_len == 64
            } else {
                (c.usage_page == 0xFF01 || c.usage_page == 0xFF0B) && c.output_len >= 64 && c.input_len >= 21
            }
        });
        for i in found {
            let (next, cached) = K87.get();
            let fresh_enough = keyboard && next.is_some_and(|n| Instant::now() < n);
            let answer = if fresh_enough {
                Some(cached)
            } else {
                let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
                let mut request = vec![0u8; ctx.colls[i].output_len.max(9)];
                (request[0], request[1], request[2], request[5]) = (0x4D, 0x01, 0x01, 0x09);
                request[8] = xor_range(&request[2..8]);
                d.flush();
                let answer = (0..3).find_map(|attempt| {
                    if attempt > 0 {
                        pause(150);
                    }
                    d.query(&request, 250, |r| modern_info(r, keyboard).is_some()).and_then(|r| modern_info(&r, keyboard))
                });
                if answer.is_none() && !keyboard {
                    continue;
                }
                if keyboard {
                    K87.set((Some(Instant::now() + Duration::from_secs(180)), answer.flatten()));
                }
                Some(answer.flatten())
            };
            let mut name = product_or(&ctx.colls[i].product, if keyboard { "MCHOSE K87 V3" } else { "MCHOSE mouse" });
            if keyboard {
                name = without_24g(&name).to_string();
            }
            let kind = if keyboard { Kind::Keyboard } else { Kind::Mouse };
            ctx.add(status(format!("mchose:{VID:04X}:{pid:04X}"), name, kind, answer.flatten()), VID, pid);
            break;
        }
    }
}

pub fn poll_mchose(ctx: &mut Context) {
    poll_mchose_mice(ctx);
    poll_a5(ctx);
    poll_g3(ctx);
    poll_v9(ctx);
    poll_mchose_keyboard(ctx);
    poll_modern(ctx);
}

// ---------------------------------------------------------------- Rongyuan

const RONGYUAN: [u32; 81] = [
    0x0DB0197E, 0x0DB08E9A, 0x145F0376, 0x145F038D, 0x14A5C045, 0x25AA2008, 0x25AA4008, 0x25AA4018, 0x31514011, 0x31514014,
    0x31514017, 0x31514021, 0x31514027, 0x3151402D, 0x31514038, 0x31515006, 0x31515007, 0x31515008, 0x31515020, 0x31515026,
    0x3151502B, 0x31515031, 0x31515037, 0x31515038, 0x3151503D, 0x3151503F, 0x31515040, 0x31515042, 0x31515045, 0x3151504B,
    0x3151504E, 0x31515050, 0x31515052, 0x31515055, 0x3151505A, 0x342DE3E0, 0x342DE3E1, 0x342DE3E2, 0x342DE3E6, 0x342DE3E7,
    0x342DE3E8, 0x342DE3F5, 0x342DE3F6, 0x342DE3F7, 0x342DE3FB, 0x342DE3FC, 0x342DE3FD, 0x347AA302, 0x347AA305, 0x374AA205,
    0x374AA217, 0x374AA220, 0x374AA223, 0x374AA226, 0x374AA229, 0x374AA234, 0x374AA237, 0x374AA251, 0x374AA263, 0x374AA268,
    0x374AA311, 0x374AA314, 0x374AA319, 0x374AA322, 0x374AA329, 0x374AA355, 0x379A1804, 0x379A1807, 0x38A90002, 0x38EE0000,
    0x38EE0002, 0x38EE0039, 0x391D1A04, 0x391D1A08, 0x391D1A0E, 0x391D1A13, 0x391D1A25, 0x391D1A2B, 0x391D1A34, 0x391D2017,
    0x391D2A05,
];

pub fn slot_name(vid: u16, pid: u16, keyboard: bool, product: &str) -> String {
    const LINK: [&str; 10] = ["2.4G", "2.4GHZ", "8K", "4K", "2K", "1K", "RECEIVER", "WIRELESS", "DONGLE", "USB"];
    let generic = product.is_empty() || product.contains("Receiver") || product.contains("2.4G");
    let brand: Vec<&str> = product.split_whitespace().filter(|w| !LINK.contains(&w.to_uppercase().as_str())).collect();
    let brand = brand.join(" ");
    let other = !brand.is_empty() && !brand.to_uppercase().contains("ATTACK SHARK");
    if vid == 0x3151 && pid == 0x402D && !keyboard && !other {
        "Attack Shark R3".into()
    } else if vid == 0x3151 && pid == 0x5038 && keyboard {
        "MonsGeek keyboard".into()
    } else if generic && !brand.is_empty() {
        format!("{brand} {}", if keyboard { "wireless keyboard" } else { "wireless mouse" })
    } else if vid == 0x3151 && pid == 0x5006 && keyboard && product.contains("Akko") {
        "Akko Air 01".into()
    } else if generic {
        if keyboard { "Wireless keyboard".into() } else { "Wireless mouse".into() }
    } else {
        product.into()
    }
}

pub fn poll_rongyuan(ctx: &mut Context) {
    let mut products: Vec<(u16, u16)> = ctx
        .colls
        .iter()
        .filter(|c| RONGYUAN.contains(&(u32::from(c.vid) << 16 | u32::from(c.pid))))
        .map(|c| (c.vid, c.pid))
        .collect();
    products.sort_unstable();
    products.dedup();
    for (vid, pid) in products {
        if ctx.is_claimed(vid, pid) || ctx.out_of_time() {
            continue;
        }
        for i in ctx.find(vid, pid, |c| c.iface == 2 && c.usage_page == 0xFFFF && c.usage == 2 && c.feature_len >= 7) {
            let c = &ctx.colls[i];
            let mut query = vec![0u8; c.feature_len.max(7)];
            query[1] = 0xF7;
            let reply = (0..3).find_map(|attempt| {
                if attempt > 0 {
                    pause(100);
                }
                if !hid::feature_write(c, &query) {
                    return None;
                }
                pause(20);
                hid::feature_read(c, 0, c.feature_len).filter(|r| r.len() >= 7 && r[2..7].iter().any(|&b| b != 0))
            });
            let Some(r) = reply else { continue };
            let product = c.product.clone();
            // emplacement 0 : clavier, emplacement 1 : souris
            for (keyboard, level_at, link_at) in [(true, 2, 4), (false, 3, 5)] {
                if r[link_at] != 0 {
                    continue;
                }
                let suffix = if keyboard { "kb" } else { "ms" };
                let kind = if keyboard { Kind::Keyboard } else { Kind::Mouse };
                let mut s = Status::new(format!("rongyuan:{vid:04X}:{pid:04X}:{suffix}"), slot_name(vid, pid, keyboard, &product), kind);
                s.percent = Some(r[level_at]).filter(|v| (1..=100).contains(v));
                ctx.out.push(s);
            }
            ctx.claim(vid, pid);
            break;
        }
    }
}

// ---------------------------------------------------------------- SONiX et Royal Kludge

pub fn sonix_request() -> [u8; 33] {
    let mut r = [0u8; 33];
    (r[1], r[2]) = (0x20, 0x01);
    r[32] = sum(&r[1..32]);
    r
}

const SONIX: [(u16, u16, i32); 20] = [
    (0x05AC, 0x024F, 3), (0x0C45, 0xFEFE, 3), (0x0C45, 0xFDFD, 3), (0x342D, 0xE479, -1), (0x342D, 0xE485, -1),
    (0x342D, 0xE4BC, -1), (0x342D, 0xE4C2, -1), (0x342D, 0xE407, -1), (0x388D, 0x0003, -1), (0x0DB0, 0x0CA0, -1),
    (0x36B0, 0x30BD, -1), (0x342D, 0xE51A, -1), (0x342D, 0xE487, -1), (0x342D, 0xE537, -1), (0x388D, 0x0002, -1),
    (0x19F5, 0x3245, -1), (0x362D, 0xD028, -1), (0x362D, 0xD030, -1), (0x362D, 0xD101, -1), (0x342D, 0xE49F, -1),
];

pub fn poll_sonix(ctx: &mut Context) {
    for (vid, pid, iface) in SONIX {
        if ctx.is_claimed(vid, pid) || ctx.out_of_time() {
            continue;
        }
        let found = ctx.find(vid, pid, |c| {
            (iface < 0 || c.iface == iface) && c.usage_page == 0xFF60 && c.usage == 0x61 && c.output_len == 33 && c.input_len == 33
        });
        for i in found {
            let name = product_or(without_24g(&ctx.colls[i].product), "Wireless keyboard");
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            // la première requête peut se perdre pendant le réveil de la liaison
            let level = (0..2).find_map(|_| {
                d.flush();
                d.query(&sonix_request(), 500, |r| r.len() >= 4 && r[0] == 0x20 && r[1] == 1 && r[2] == 0).map(|r| r[3])
            });
            let Some(level) = level else { continue };
            let battery = (1..=100).contains(&level).then_some((level, false));
            ctx.add(status(format!("sonix:{vid:04X}:{pid:04X}"), name, Kind::Keyboard, battery), vid, pid);
            break;
        }
    }
}

/// Royal Kludge MG6 : `Some(None)` souris hors ligne ; `Some(Some(%, câble))`.
pub fn rk_reply(r: &[u8]) -> Option<Option<(u8, bool)>> {
    if r.len() < 11 || r[0] & 0x1F != 0x01 || r[1] & 0x0F != 0x01 || r[2] != 0x02 {
        return None;
    }
    if (r[3] >> 4) & 1 == 0 {
        return Some(None);
    }
    let percent = u16::from_le_bytes([r[6], r[7]]);
    (percent <= 100).then(|| Some((percent as u8, r[10] != 0)))
}

pub fn poll_royal_kludge(ctx: &mut Context) {
    const VID: u16 = 0x2E3C;
    for pid in ctx.products(VID) {
        if ![0x00A5, 0x00A6, 0x00E5, 0x00E6].contains(&pid) {
            continue;
        }
        let cable = pid == 0x00A5 || pid == 0x00E5;
        let page = if cable { 0xFFF2 } else { 0xFF00 };
        for i in ctx.find(VID, pid, |c| c.usage_page == page && c.usage == 1 && c.output_len == 65 && c.input_len == 65) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let mut answered = false;
            let mut online = None;
            for attempt in 0..3 {
                if attempt > 0 {
                    pause(100);
                }
                d.flush();
                let reply = d.query(&[0x00, 0x01, 0x01, 0x02, 0x01], 600, |r| rk_reply(r).is_some()).and_then(|r| rk_reply(&r));
                if let Some(reply) = reply {
                    answered = true;
                    online = reply;
                }
                if online.is_some() {
                    break;
                }
            }
            if !answered {
                continue;
            }
            let battery = online.map(|(p, plugged)| (p, plugged || cable));
            ctx.add(status(format!("rk:{VID:04X}:{pid:04X}"), "RK MG6 Pro", Kind::Mouse, battery), VID, pid);
            break;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn by_and_hero() {
        let mut r = [0u8; 20];
        (r[0], r[1], r[5]) = (0x13, 0x4A, 80);
        r[19] = sum(&r[..19]);
        assert!(by_is_reply(&r, 0x4A));
        assert!(!by_is_reply(&by_request(0x4A, 0), 0x4A), "un écho n'est pas une réponse");
        assert_eq!(by_model(&[0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0x77]), Some("MCHOSE K99"));
        assert_eq!(hero_header(5, 2, 0x0E)[2..5], [0x85, 2, 0x8E]);
        let mut info = [0u8; 32];
        (info[0], info[13], info[14]) = (0x30, 70, 1);
        info[31] = sum(&info[4..31]);
        assert_eq!(sc_info(&info).unwrap()[9], 70);
        assert_eq!(aula_x9_battery(&[4, 0, 0, 0x1A, 0, 0, 0, 0, 55, 1]), Some((55, true)));
    }

    #[test]
    fn mchose_frames() {
        let mut r = [0xFFu8; 13];
        (r[0], r[1]) = (0x12, 0xF9);
        assert_eq!(mchose_status(&r, 0x5253, 0x5253), Some(None));
        (r[2], r[3], r[11]) = (0x53 ^ 0xFF, 0x52 ^ 0xFF, 80 ^ 0xFF);
        assert_eq!(mchose_status(&r, 0x5253, 0x5253), Some(Some((80, false, 0))));
        assert_eq!(a5_battery(&[0xA1, 0, 0, 0, 0, 0x83, 0, 60, 0]), Some((60, false)));
        assert_eq!(rk_reply(&[1, 1, 2, 0x10, 0, 0, 66, 0, 0, 0, 1]), Some(Some((66, true))));
        assert_eq!(rk_reply(&[1, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0]), Some(None));
        assert_eq!(sonix_request()[32], 0x21);
        assert_eq!(slot_name(0x3151, 0x4017, true, "ATK 2.4G Wireless Receiver"), "ATK wireless keyboard");
    }
}
