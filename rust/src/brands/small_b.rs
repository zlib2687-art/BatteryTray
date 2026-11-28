//! Flydigi, Fractal, Machenike, MAONO, MOONDROP, MSI, Niceboy, Ninjutso, NuPhy, NZXT, Press Play, Raigor, Redragon
//! Evision, Scyrox, Semico, Sinowealth, Sprime, VAXEE, VGN, Zenith, Zowie. Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/ (un fichier d'origine par marque). Lecture seule.

use super::{interpolate, pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::{self, Info};
use std::cell::{Cell, RefCell};
use std::time::{Duration, Instant};

fn put(mut s: Status, percent: Option<u8>, charging: bool) -> Status {
    (s.percent, s.charging) = (percent, charging);
    s
}

fn off(mut s: Status) -> Status {
    s.connected = false;
    s
}

fn level(value: u8) -> Option<u8> {
    Some(value).filter(|&v| v <= 100)
}

fn sum(b: &[u8]) -> u8 {
    b.iter().fold(0u8, |s, &v| s.wrapping_add(v))
}

fn mouse(key: impl Into<String>, name: impl Into<String>) -> Status {
    Status::new(key, name, Kind::Mouse)
}

/// Applique l'échange à la première collection convenable qui répond.
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

/// Envoie la requête (vidée d'abord) et rend le premier rapport que `parse` comprend.
fn ask<T>(d: &Dev, request: &[u8], timeout: u64, parse: impl Fn(&[u8]) -> Option<T>) -> Option<T> {
    d.flush();
    d.query(request, timeout, |r| parse(r).is_some()).and_then(|r| parse(&r))
}

// ---------------------------------------------------------------- Flydigi, Fractal

pub fn flydigi_reply(r: &[u8], command: u8) -> Option<&[u8]> {
    let r = if r.len() >= 4 && r[..3] == [0x04, 0x5A, 0xA5] { &r[1..] } else { r };
    (r.len() >= 5 && r[0] == 0x5A && r[1] == 0xA5 && r[2] == command).then_some(r)
}

/// (pourcentage, en charge, via le récepteur).
pub fn flydigi_info(ids: &[u8], r: &[u8], modern: bool) -> Option<(u8, bool, bool)> {
    if r.len() < 12 || !ids.contains(&r[5]) {
        return None;
    }
    let (value, state, link) = (r[11] & 0x0F, r[11] >> 4, r[6]);
    let link_ok = if modern { link <= 1 } else { link == 1 || link == 2 };
    if state > 2 || value > 5 || !link_ok {
        return None;
    }
    Some((if state == 2 { 100 } else { value * 20 }, state == 1, link == if modern { 1 } else { 2 }))
}

fn poll_flydigi_fractal(ctx: &mut Context) {
    for (pid, report, ids, name) in [
        (0x2401, 0x00, &[130u8][..], "Flydigi Vader 5 Pro"),
        (0x2501, 0x03, &[128, 129, 133, 134, 135][..], "Flydigi Apex 5"),
    ] {
        first(ctx, 0x37D7, pid, |c| c.usage_page == 0xFFA0 && c.usage == 1 && c.input_len >= 12 && c.output_len >= 6, |_, d| {
            d.flush();
            let command = |c: u8, p: u8| d.write(&[report, 0x5A, 0xA5, c, p, 0]);
            let reply = |c: u8| d.wait_for(800, |r| flydigi_reply(r, c).is_some()).and_then(|r| flydigi_reply(&r, c).map(<[u8]>::to_vec));
            if !command(0x07, 0) {
                return None;
            }
            let modern = reply(0x07).is_some_and(|a| a.len() > 4 && a[3] == 1 && a[4] == 0);
            if !command(0x01, 2) {
                return None;
            }
            let (percent, charging, _) = flydigi_info(ids, &reply(0x01)?, modern)?;
            Some(put(Status::new(format!("flydigi:{pid:04X}"), name, Kind::Gamepad), Some(percent), charging))
        });
    }

    for pid in [0x0001, 0x2001] {
        let cable = pid == 0x2001;
        first(ctx, 0x36BC, pid, |c| c.usage_page == 0xFF00 && c.usage == 1 && c.output_len >= 4 && c.input_len >= 24, |_, d| {
            let state = |kind: u8| ask(d, &[0x02, kind, 0x21], 1000, |r| (r.len() >= 24 && r[..3] == [0x02, kind, 0x21]).then(|| r.to_vec()));
            let s = Status::new("fractal:scape", "Fractal Scape", Kind::Headset);
            if !cable && state(0x11)?[4] != 1 {
                return Some(off(s));
            }
            let headset = state(0xF1).filter(|r| r[15] <= 100)?;
            Some(put(s, Some(headset[15]), headset[23] == 1))
        });
    }
}

// ---------------------------------------------------------------- Machenike, MAONO, MOONDROP

/// (connectée, pourcentage, en charge).
pub fn machenike(r: &[u8], cable: bool) -> Option<(bool, u8, bool)> {
    let header = r.len() == 21 && r[..2] == [0x51, 0x06] && r[4..8] == [0x8A, 0x24, 0x4E, 0xFF];
    if !header || r[10] & 7 != u8::from(!cable) {
        return None;
    }
    let connected = cable || r[10] & 8 != 0;
    if connected && (r[11] > 100 || r[12] > 1) {
        return None;
    }
    Some((connected, r[11], r[12] == 1))
}

fn maono_checksum(d: &[u8]) -> u16 {
    0u16.wrapping_sub(d.iter().fold(0u16, |s, &v| s.wrapping_add(u16::from(v))))
}

pub fn maono_request() -> [u8; 64] {
    let mut r = [0u8; 64];
    (r[0], r[1], r[4], r[5]) = (0xC4, 9, 0x04, 0x42);
    let c = maono_checksum(&r[..7]).to_le_bytes();
    (r[7], r[8]) = (c[0], c[1]);
    r
}

/// Registre 0x42 (batterie) d'une réponse à somme vérifiée.
pub fn maono_battery(r: &[u8]) -> Option<u8> {
    if r.len() < 11 || r[0] != 0xC4 {
        return None;
    }
    let len = usize::from(r[1]);
    if len < 11 || len > r.len() || r[2..5] != [0, 0, 0x04] {
        return None;
    }
    let end = len - 2;
    let word = |at: usize| u16::from_le_bytes([r[at], r[at + 1]]);
    if (end - 5) % 4 != 0 || word(end) != maono_checksum(&r[..end]) {
        return None;
    }
    (5..end).step_by(4).find(|&at| word(at) == 0x0042).map(|at| word(at + 2)).filter(|&v| v <= 100).map(|v| v as u8)
}

fn poll_machenike_audio(ctx: &mut Context) {
    let mut have_cable = false;
    for pid in [0xFF4E, 0xFF4C] {
        const VID: u16 = 0x248A;
        let cable = pid == 0xFF4E;
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let found = ctx.find(VID, pid, |c| c.iface == if cable { 2 } else { 1 } && on(0x008C, 1, 33, 33)(c) && c.feature_len == 1001);
        for i in found {
            if ctx.colls[i].product != "L7 Mix" {
                continue;
            }
            ctx.claim(VID, pid);
            if have_cable {
                break;
            }
            let c = &ctx.colls[i];
            let mut request = [0u8; 21];
            (request[0], request[1]) = (0x51, 0x06);
            if !hid::feature_write(c, &request) {
                break;
            }
            let end = Instant::now() + Duration::from_millis(200);
            let mut found = None;
            while found.is_none() && Instant::now() < end {
                pause(10);
                found = hid::feature_read(c, 0x51, 21).and_then(|r| machenike(&r, cable));
            }
            let Some((connected, percent, charging)) = found else { break };
            let mut s = put(mouse("machenike:l7", "Machenike L7 Arctic / L7 Mix"), connected.then_some(percent), connected && charging);
            s.connected = connected;
            have_cable = cable;
            ctx.add(s, VID, pid);
            break;
        }
    }

    let maono = |c: &Info| c.iface == 3 && on(0x000C, 1, 64, 64)(c) && c.feature_len == 0;
    first(ctx, 0x352F, 0x040E, maono, |_, d| {
        let percent = ask(d, &maono_request(), 1000, maono_battery)?;
        Some(put(Status::new("maono:pd200w", "MAONO PD200W", Kind::Microphone), Some(percent), false))
    });

    const MOONDROP: u16 = 0x35D8;
    const GM01: u16 = 0x0133;
    let found = ctx.find(MOONDROP, GM01, |c| c.iface == 3 && c.usage_page == 0x000C);
    if found.is_empty() || ctx.is_claimed(MOONDROP, GM01) {
        return;
    }
    let headset = || Status::new("moondrop:0133", "MOONDROP GM01 Pro", Kind::Headset);
    let mut unlinked = false;
    for &i in &found {
        let c = &ctx.colls[i];
        if c.output_len < 4 || c.input_len < 10 {
            continue;
        }
        let Some(d) = Dev::open(c) else { continue };
        let read = |command: u8| {
            ask(&d, &[0x4B, 0x80, command, 0x00], 400, |r| (r.len() >= 5 && r[..3] == [0x4B, 0x80, command]).then(|| r.to_vec()))
        };
        if let Some(r) = read(0x27).filter(|r| r.len() >= 10 && r[3] >= 6 && r[8] != 0 && r[9] <= 100) {
            ctx.add(put(headset(), Some(r[9]), false), MOONDROP, GM01);
            return;
        }
        unlinked |= read(0x31).is_some_and(|r| r[3] >= 1 && r[4] == 0);
    }
    let on_cable = ctx.find(MOONDROP, GM01, |c| c.iface == 3 && !c.path_text.contains("&col")).len() > 0;
    if on_cable {
        ctx.add(put(headset(), None, true), MOONDROP, GM01);
    } else if unlinked {
        ctx.add(off(headset()), MOONDROP, GM01);
    } else {
        ctx.claim(MOONDROP, GM01);
    }
}

// ---------------------------------------------------------------- MSI, Niceboy, Ninjutso, Sprime

pub fn msi_status(r: &[u8]) -> Option<(u8, bool)> {
    (r.len() >= 15 && r[0] == 0x0F && r[1] == 0x20 && r[14] & 0x7F <= 100).then(|| (r[14] & 0x7F, r[14] & 0x80 != 0))
}

/// GM41 : valeur brute convertie (approximative).
pub fn gm41_percent(raw: u8) -> u8 {
    ((u32::from(raw) * 51 + 1760 + 50) / 100).min(100) as u8
}

pub fn niceboy_percent(mv: u32) -> u8 {
    [(4200, 100), (4100, 90), (4000, 80), (3900, 70), (3800, 60), (3700, 50), (3500, 40), (3400, 30), (3200, 20)]
        .into_iter()
        .find(|&(limit, _)| mv >= limit)
        .map_or(10, |p| p.1)
}

/// Sora V2 et Sprime PM1 : (connectée, pourcentage, en charge).
pub fn sora(r: &[u8], cable: bool, sprime: bool) -> Option<(bool, u8, bool)> {
    if r.len() < 13 || r[0] != 0x05 || r[1] != 0x15 || r[9] > 100 {
        return None;
    }
    let connected = cable || r[12] != 0;
    let full = sprime && r[11] != 0;
    let percent = if full { 100 } else if !sprime && r[9] == 0 { 100 } else { r[9] };
    Some((connected, percent, connected && r[10] != 0 && !full))
}

fn poll_sora_like(ctx: &mut Context, vid: u16, pid: u16, key: String, name: &str, cable: bool, sprime: bool) {
    if ctx.is_claimed(vid, pid) {
        return;
    }
    for i in ctx.find(vid, pid, |c| c.usage_page == 0xFFA0 && c.usage == 1 && c.feature_len >= 32) {
        let c = &ctx.colls[i];
        let mut query = [0u8; 32];
        (query[0], query[1], query[4]) = (0x05, 0x15, 1);
        let mut found = None;
        for attempt in 0..4 {
            if attempt > 0 {
                pause(90);
            }
            if !hid::feature_write(c, &query) {
                break;
            }
            pause(15);
            let Some(answer) = hid::feature_read(c, 0x05, c.feature_len).and_then(|r| sora(&r, cable, sprime)) else { continue };
            found = Some(answer);
            if answer.0 {
                break;
            }
        }
        let Some((connected, percent, charging)) = found else { continue };
        let mut s = put(mouse(key, name), connected.then_some(percent), charging);
        s.connected = connected;
        ctx.add(s, vid, pid);
        break;
    }
}

fn poll_msi_ninjutso(ctx: &mut Context) {
    const MSI: u16 = 0x0DB0;
    if !ctx.is_claimed(MSI, 0x9E22) {
        for (pid, passive, page, usage, name) in [
            (0x9E22, false, 0xFF0D, 0xB0, "MSI VERSA 300 ELITE WIRELESS"), (0x0D3C, false, 0xFF00, 1, "MSI Clutch GM31 Lightweight Wireless"),
            (0x0D3B, false, 0xFF00, 1, "MSI Clutch GM31 Lightweight Wireless"), (0x0D4C, true, 0xFF10, 6, "MSI Clutch GM41 Lightweight Wireless"),
            (0x0D4B, true, 0xFF10, 6, "MSI Clutch GM41 Lightweight Wireless"),
        ] {
            let fits = |c: &Info| {
                c.iface == 1 && c.usage_page == page && c.usage == usage
                    && if passive { c.input_len >= 7 } else { c.output_len >= 65 && c.input_len >= 65 }
            };
            first(ctx, MSI, pid, fits, |_, d| {
                let s = mouse(format!("msi:{pid:04X}"), name);
                if passive {
                    let r = d.wait_for(2000, |r| r.len() >= 7 && r[0] == 0xA0 && r[2] == 0x07)?;
                    return Some(put(s, Some(gm41_percent(r[1])), false));
                }
                let (percent, charging) = ask(d, &[0x00, 0x0F, 0x20], 1000, msi_status)?;
                Some(if percent == 0 && !charging { off(s) } else { put(s, Some(percent), charging) })
            });
        }
    }

    let niceboy = |c: &Info| c.iface == 0 && on(0x0001, 0, 33, 33)(c);
    first(ctx, 0x248A, 0xFA02, niceboy, |_, d| {
        let r = ask(d, &[0x05, 0x24, 0xFA, 0x48], 500, |r| (r.len() >= 7 && r[0] == 0x05 && r[2] == 0xFA && r[3] == 0x48).then(|| r.to_vec()))?;
        let s = mouse("niceboy:FA02", "Niceboy MW400");
        if r[4] | r[5] | r[6] == 0 {
            return Some(off(s));
        }
        let mv = u32::from(u16::from_le_bytes([r[4], r[5]])) * 990 / 240;
        Some(put(s, Some(niceboy_percent(mv)), r[6] != 0))
    });

    for pid in [0xAE1C, 0xAE11] {
        poll_sora_like(ctx, 0x1915, pid, format!("ninjutso:1915:{pid:04X}"), "Ninjutso Sora V2", pid == 0xAE11, false);
    }
    for pid in [0xEB01, 0xE020] {
        if ctx.is_claimed(0x093A, pid) {
            continue;
        }
        for i in ctx.find(0x093A, pid, |c| c.usage_page == 0xFF01 && c.usage == 1 && c.feature_len == 16) {
            let c = &ctx.colls[i];
            let query = |command: u8| {
                let mut q = [0u8; 16];
                (q[0], q[1], q[4]) = (0x06, command, 1);
                if !hid::feature_write(c, &q) {
                    return None;
                }
                (0..3).find_map(|_| {
                    pause(30);
                    hid::feature_read(c, 0x06, 16).filter(|r| r.len() == 16 && r[1] == command).map(|r| r[8])
                })
            };
            if query(0x13) != Some(1) {
                continue;
            }
            let (Some(percent), Some(charging)) = (query(0x12).and_then(level), query(0x11)) else { continue };
            let name = if c.product.is_empty() { "Ninjutso Ten / Ten Air".to_string() } else { c.product.clone() };
            ctx.add(put(mouse(format!("ninjutso-ten:{pid:04X}"), name), Some(percent), charging != 0), 0x093A, pid);
            break;
        }
    }
    for pid in [0xAC11, 0xAC12, 0xAC13, 0xAC1C, 0xAD8C] {
        let cable = (0xAC11..=0xAC13).contains(&pid);
        poll_sora_like(ctx, 0x1915, pid, format!("sprime:1915:{pid:04X}"), "Sprime PM1", cable, true);
    }
}

// ---------------------------------------------------------------- NuPhy, NZXT, Press Play, Raigor, Redragon, Scyrox

pub fn nuphy(r: &[u8]) -> Option<(u8, bool)> {
    let header = r.len() == 64 && r[..3] == [0xAA, 0x05, 0] && r[4..8] == [1, 0x20, 0, 0];
    (header && sum(&r[4..]) == r[3] && r[8] & 0x7F <= 100).then(|| (r[8] & 0x7F, r[8] & 0x80 != 0))
}

/// NZXT Lift Elite : (pourcentage, en charge).
pub fn nzxt(r: &[u8]) -> Option<(u8, bool)> {
    if r.len() < 27 || r[0] != 0x4E || r[1] != 0x02 || r[2] != 0x97 {
        return None;
    }
    let value = u16::from_le_bytes([r[17], r[18]]);
    (value <= 100).then(|| (value as u8, r[22] & 1 != 0))
}

pub fn raigor(r: &[u8]) -> Option<u8> {
    if r.len() != 8 || r[0] != 0xB5 {
        return None;
    }
    if r[1] == 0x05 && r[2] == 0x94 && r[3] == 0x04 && r[6] <= 100 {
        return Some(r[6]);
    }
    (r[1] == 0x67 && r[7] <= 100).then(|| r[7])
}

/// Scyrox : `Some(None)` fin sans niveau, `None` pas le profil actif.
pub fn scyrox(r: &[u8]) -> Option<Option<u8>> {
    if r.len() != 64 || r[0] != 0x08 || r[1] != 0x01 {
        return None;
    }
    if r[3] == 0 {
        return Some(None);
    }
    if r[62] == 1 || r[4] > 3 || r[4] != r[5] || !(1..=5).contains(&r[7]) {
        return None;
    }
    Some(level(r[20]))
}

thread_local!(static REDRAGON_SEQUENCE: Cell<u16> = const { Cell::new(0) });

fn poll_misc_vendors(ctx: &mut Context) {
    first(ctx, 0x19F5, 0xA011, |c| c.iface == 1 && on(1, 0, 65, 65)(c), |_, d| {
        let (percent, charging) = ask(d, &[0, 0x55, 0x05, 0, 0x21, 1, 0x20, 0, 0], 400, nuphy)?;
        Some(put(Status::new("nuphy:WH80", "NuPhy WH80", Kind::Keyboard), Some(percent), charging))
    });

    // NZXT : câble et récepteur forment une seule souris
    let (mut present, mut reading) = (false, None);
    for pid in [0x2129, 0x2101] {
        let found = ctx.find(0x1E71, pid, |c| c.usage_page == 0xFFCA && c.usage == 1);
        if found.is_empty() || ctx.is_claimed(0x1E71, pid) {
            continue;
        }
        present = true;
        ctx.claim(0x1E71, pid);
        for i in found {
            if reading.is_some() {
                break;
            }
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let mut request = [0u8; 65];
            (request[1], request[2], request[3], request[5]) = (0x4E, 0x02, 0x81, 0xB0);
            reading = ask(&d, &request, 1500, nzxt);
        }
    }
    if present {
        let s = mouse("nzxt:2101", "NZXT Lift Elite");
        ctx.out.push(match reading {
            Some((percent, charging)) => put(s, Some(percent), charging),
            None => off(s),
        });
    }

    first(ctx, 0x7768, 0x6434, on(0xFFB2, 1, 64, 64), |_, d| {
        d.flush();
        let r = d.query(&[0x06, 0x05], 500, |_| true)?;
        let ok = r.len() >= 14 && r[..3] == [0x06, 0x05, 0x26] && r[13] <= 100;
        ok.then(|| put(mouse("pressplay:icarus-v2", "Press Play ICARUS V2 Nearlink"), Some(r[13]), false))
    });

    for pid in [0x0066, 0x0064] {
        first(ctx, 0x1EA7, pid, on(0xFFB5, 1, 8, 8), |c, d| {
            let percent = ask(d, &[0xB5, 0x05, 0, 0, 0, 0, 0, 0], 1200, raigor)?;
            let name = if c.product.is_empty() { "2.4 GHz mouse".to_string() } else { c.product.clone() };
            Some(put(mouse(format!("raigor:1EA7:{pid:04X}"), name), Some(percent), false))
        });
    }

    first(ctx, 0x320F, 0x226F, |c| c.usage_page == 0xFF1C && c.usage == 0x92, |_, d| {
        let sequence = REDRAGON_SEQUENCE.get() % 0xFFFF + 1;
        REDRAGON_SEQUENCE.set(sequence);
        let s = sequence.to_le_bytes();
        let mut request = [0u8; 32];
        (request[0], request[1], request[2], request[3], request[4]) = (0x04, s[0], s[1], 0x1A, 0x02);
        let reply = ask(d, &request, 1000, |r| {
            (r.len() >= 10 && r[0] == 0x04 && r[1..3] == s && r[3] == 0x1A && r[5] == 0 && r[6] == 0).then(|| (r[8], r[9]))
        });
        let status = mouse("redragon_evision:226F", "Redragon wireless mouse");
        Some(match reply {
            Some((percent, state)) if percent <= 100 && state <= 2 => put(status, Some(percent), state > 0),
            _ => off(status),
        })
    });

    first(ctx, 0x395A, 0x0006, on(0xFF00, 2, 64, 64), |_, d| {
        let mut request = [0u8; 64];
        (request[0], request[1], request[63]) = (0x08, 0x01, 0x09);
        let percent = ask(d, &request, 1500, scyrox).flatten();
        d.flush(); // on jette le reste du flot de profils
        percent.map(|p| put(mouse("scyrox:0006", "Scyrox V12M"), Some(p), false))
    });
}

// ---------------------------------------------------------------- Semico, Sinowealth

/// Clavier Semico : `Some(None)` hors ligne.
pub fn semico(r: &[u8]) -> Option<Option<u8>> {
    if r.len() < 7 || r[..5] != [0xAA, 0x55, 0xCC, 0x33, 0x0E] {
        return None;
    }
    if r[6] == 0 {
        return Some(None);
    }
    (r[6] <= 3 && r[5] <= 100).then(|| Some(r[5]))
}

fn poll_semico(ctx: &mut Context) {
    const VID: u16 = 0x1A2C;
    let name_of = |pid| match pid {
        0xA034 => Some("Attack Shark X98"),
        0xA036 => Some("AJAZZ AK820 Max Plus"),
        _ => None,
    };
    let mut pids = ctx.products(VID);
    let cable_pid = pids.iter().copied().filter(|&p| p == 0xA034 || p == 0xA036).last().unwrap_or(0);
    pids.sort_by_key(|&p| p != 0x8FFF);
    let (mut online, mut offline) = (false, Vec::new());
    for &pid in &pids {
        if online || ![0x8FFF, 0xA034, 0xA036].contains(&pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.iface == 0 && on(0xFF02, 2, 65, 65)(c)) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let mut request = [0u8; 65];
            request[1..6].copy_from_slice(&[0xAA, 0x55, 0xCC, 0x33, 0x0E]);
            match ask(&d, &request, 600, semico) {
                Some(Some(percent)) => {
                    let product = ctx.colls[i].product.clone();
                    let generic = product.is_empty() || product == "2.4 Wireless Device" || product == "USB Gaming Keyboard";
                    let name = name_of(pid).or_else(|| name_of(cable_pid)).map(String::from);
                    let name = name.unwrap_or_else(|| if generic { "Wireless keyboard".into() } else { product });
                    ctx.add(put(Status::new("semico:1A2C", name, Kind::Keyboard), Some(percent), false), VID, pid);
                    online = true;
                }
                Some(None) => offline.push(pid),
                None => continue,
            }
            break;
        }
    }
    if online {
        for &pid in pids.iter().filter(|&&p| [0x8FFF, 0xA034, 0xA036].contains(&p)) {
            ctx.claim(VID, pid);
        }
    } else if !offline.is_empty() {
        let name = name_of(cable_pid).unwrap_or("Wireless keyboard");
        ctx.out.push(off(Status::new("semico:1A2C", name, Kind::Keyboard)));
        for pid in offline {
            ctx.claim(VID, pid);
        }
    }
}

fn poll_sinowealth(ctx: &mut Context) {
    const VID: u16 = 0x258A;
    const PID: u16 = 0x002F;
    let fits = |c: &Info| c.iface == 1 && c.usage_page == 0xFF00 && c.usage == 1 && c.feature_len == 8 && c.input_len == 0 && c.output_len == 0;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    for i in ctx.find(VID, PID, fits) {
        let c = &ctx.colls[i];
        let query = |command: u8| {
            if !hid::feature_write(c, &[5, command, 0, 0, 0, 0, 0, 0]) {
                return None;
            }
            pause(100);
            hid::feature_read(c, 5, 8).filter(|r| r.len() == 8 && r[1] == command)
        };
        if !query(0x80).is_some_and(|r| r[2] == 1) {
            continue;
        }
        let Some(r) = query(0x90).filter(|r| r[2] == 0x11 && r[3] <= 100) else { continue };
        ctx.add(put(mouse("sinowealth:258A:002F", "Sinowealth wireless mouse"), Some(r[3]), false), VID, PID);
        break;
    }
}

// ---------------------------------------------------------------- VAXEE, VGN, Zenith, Zowie

thread_local!(static VAXEE_LAST: RefCell<Vec<(String, Status)>> = const { RefCell::new(Vec::new()) });

pub fn vaxee_reply(r: &[u8], command: u8) -> Option<u8> {
    (r.len() >= 6 && r[..3] == [0x0E, 0xA5, command] && r[4] == 1).then(|| r[5])
}

fn poll_vaxee(ctx: &mut Context) {
    const VID: u16 = 0x3057;
    // une souris débranchée reste signalée une fois comme hors ligne
    VAXEE_LAST.with_borrow_mut(|last| {
        last.retain(|(path, s)| {
            let present = ctx.colls.iter().any(|c| &c.path_text == path);
            if !present {
                ctx.out.push(off(put(s.clone(), None, false)));
            }
            present
        });
    });
    for pid in ctx.products(VID) {
        if ![0x1002, 0x2002, 0x1011].contains(&pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF05 && c.usage == 1 && c.feature_len == 64) {
            let c = &ctx.colls[i];
            let query = |command: u8| {
                let mut r = [0u8; 64];
                (r[0], r[1], r[2], r[3], r[4]) = (0x0E, 0xA5, command, 0x01, 0x01);
                if !hid::feature_write(c, &r) {
                    return None;
                }
                pause(100);
                hid::feature_read(c, 0x0E, 64).and_then(|r| vaxee_reply(&r, command))
            };
            let Some(battery) = (0..2).find_map(|_| query(0x0B).filter(|&b| b <= 20)) else { continue };
            let name = match pid {
                0x1011 => "ZYGEN NP-01S V2 Wireless",
                0x2002 => "VAXEE 8K Wireless",
                _ => "VAXEE wireless mouse",
            };
            let s = put(mouse(format!("vaxee:{pid:04X}"), name), Some(battery * 5), query(0x10).is_some_and(|v| v != 0));
            ctx.out.retain(|o| o.key != s.key || o.connected);
            let path = c.path_text.clone();
            VAXEE_LAST.with_borrow_mut(|last| {
                last.retain(|l| l.0 != path);
                last.push((path, s.clone()));
            });
            ctx.add(s, VID, pid);
            break;
        }
    }
}

pub fn vgn(r: &[u8]) -> Option<u8> {
    if r.len() >= 3 && r[0] == 0x55 && r[1] == 0x65 {
        level(r[2])
    } else if r.len() >= 2 && r[0] == 0x65 {
        level(r[1])
    } else {
        None
    }
}

pub fn zenith(r: &[u8]) -> Option<(u8, bool)> {
    let ok = r.len() == 17 && r[0] == 0x08 && r[1] == 0x04 && r[6] <= 100 && sum(r) == 0x55;
    ok.then(|| (r[6], r[7] != 0))
}

pub fn zowie_percent(mv: i64) -> u8 {
    interpolate(mv, &[4200, 4000, 3890, 3760, 3680, 3500], &[100, 75, 50, 25, 10, 0])
}

/// Charge déduite de l'évolution de la tension.
#[derive(Clone, Copy, Default)]
pub struct Trend {
    seen: bool,
    charging: bool,
    peak: i64,
    trough: i64,
}

impl Trend {
    pub fn update(&mut self, mv: i64) -> bool {
        if !self.seen {
            // une cellule Li-ion au repos ne dépasse jamais 4,2 V
            *self = Trend { seen: true, charging: mv >= 4250, peak: mv, trough: mv };
        } else if self.charging {
            self.peak = self.peak.max(mv);
            if mv <= self.peak - 20 {
                self.charging = false;
                self.trough = mv;
            }
        } else {
            self.trough = self.trough.min(mv);
            if mv >= self.trough + 50 {
                self.charging = true;
                self.peak = mv;
            }
        }
        self.charging
    }
}

thread_local!(static ZOWIE: RefCell<Vec<(u16, Trend)>> = const { RefCell::new(Vec::new()) });

fn poll_vgn_zenith_zowie(ctx: &mut Context) {
    first(ctx, 0x372E, 0x10BB, |c| c.usage_page == 0x000C && c.usage == 1 && c.output_len >= 63 && c.input_len >= 3, |c, d| {
        let percent = ask(d, &[0x55, 0x65, 0x01], 800, vgn)?;
        let name = if c.product.is_empty() { "VGN OMNISOUND (ZHOUTING)".to_string() } else { c.product.clone() };
        Some(put(Status::new("vgn:zhou", name, Kind::Headset), Some(percent), false))
    });

    for pid in [0x1114, 0x1143] {
        const VID: u16 = 0x260D;
        let fits = |c: &Info| c.iface == 1 && on(0xFF02, 2, 17, 17)(c);
        if ctx.find(VID, pid, fits).len() != 1 {
            continue;
        }
        first(ctx, VID, pid, fits, |_, d| {
            let mut request = [0u8; 17];
            (request[0], request[1], request[16]) = (0x08, 0x04, 0x49);
            let (percent, charging) = ask(d, &request, 1000, zenith)?;
            Some(put(mouse(format!("zenith:{VID:04X}:{pid:04X}"), "Zenith Pro"), Some(percent), charging))
        });
    }

    const ZOWIE_VID: u16 = 0x04A5;
    for pid in ctx.products(ZOWIE_VID) {
        if pid == 0x8005 || pid == 0x8006 {
            let fits = |c: &Info| (c.usage_page >= 0xFF00 || c.usage_page == 0x00FF) && c.output_len == 65;
            first(ctx, ZOWIE_VID, pid, fits, |_, d| {
                d.flush();
                let r = d.query(&[0x00, 0xA5, 0x03], 1000, |r| r.len() >= 3)?;
                let mv = i64::from(u16::from_le_bytes([r[0], r[1]]));
                if mv != 0 && !(3000..=4500).contains(&mv) {
                    return None;
                }
                let mut s = mouse(format!("zowie:{pid:04X}"), "ZOWIE EC-CW");
                s.charging = pid == 0x8005;
                if mv == 0 {
                    return Some(off(s));
                }
                s.percent = Some(if (1..=100).contains(&r[2]) { r[2] } else { zowie_percent(mv) });
                Some(s)
            });
            continue;
        }
        // série DW / U2 : commande et réponse sur deux collections
        let commands = ctx.find(ZOWIE_VID, pid, |c| c.usage_page == 0xFF03 && c.output_len == 16);
        let replies = ctx.find(ZOWIE_VID, pid, |c| c.usage_page == 0xFF04 && c.input_len == 16);
        let (Some(&command), Some(&reply)) = (commands.first(), replies.first()) else { continue };
        let (Some(input), Some(output)) = (Dev::open(&ctx.colls[reply]), Dev::open(&ctx.colls[command])) else { continue };
        input.flush();
        if !output.write(&[0x08, 0xB4, 0x15]) {
            continue;
        }
        let found = input.wait_for(1500, |r| r.len() >= 5 && r[0] == 0x09);
        let Some(r) = found.filter(|r| r[1] != 0xE6 && r[4] == 0xC8) else { continue };
        let mv = (i64::from(r[1]) * 256 + i64::from(r[2])) * 10;
        let cable = pid != 0x800A && pid != 0x8008;
        let product = ctx.colls[command].product.clone();
        let name = if pid == 0x8008 {
            "ZOWIE U2".into()
        } else if product.is_empty() || product.contains("Receiver") {
            "ZOWIE DW mouse".into()
        } else {
            product
        };
        let mut s = mouse(format!("zowie:{pid:04X}"), name);
        if mv == 0 {
            s.connected = false;
        } else {
            s.percent = Some(zowie_percent(mv));
            let rising = ZOWIE.with_borrow_mut(|all| {
                let index = all.iter().position(|e| e.0 == pid).unwrap_or_else(|| {
                    all.push((pid, Trend::default()));
                    all.len() - 1
                });
                all[index].1.update(mv)
            });
            s.charging = cable || rising;
        }
        ctx.add(s, ZOWIE_VID, pid);
    }
}

pub fn poll(ctx: &mut Context) {
    for step in [
        poll_flydigi_fractal, poll_machenike_audio, poll_msi_ninjutso, poll_misc_vendors, poll_semico, poll_sinowealth, poll_vaxee,
        poll_vgn_zenith_zowie,
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
        assert_eq!(flydigi_reply(&[4, 0x5A, 0xA5, 7, 0, 0], 7).unwrap()[0], 0x5A);
        assert_eq!(flydigi_info(&[130], &[0x5A, 0xA5, 1, 0, 0, 130, 1, 0, 0, 0, 0, 0x03], false), Some((60, false, false)));
        let q = maono_request();
        assert_eq!(u16::from_le_bytes([q[7], q[8]]), maono_checksum(&q[..7]));
        let mut r = vec![0xC4, 11, 0, 0, 4, 0x42, 0, 77, 0];
        let c = maono_checksum(&r).to_le_bytes();
        r.extend_from_slice(&c);
        assert_eq!(maono_battery(&r), Some(77));
        assert_eq!(gm41_percent(100), 69);
        assert_eq!(niceboy_percent(4150), 90);
        assert_eq!(sora(&[5, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], false, false), Some((true, 100, false)));
        assert_eq!(raigor(&[0xB5, 0x67, 0, 0, 0, 0, 0, 55]), Some(55));
        assert_eq!(semico(&[0xAA, 0x55, 0xCC, 0x33, 0x0E, 66, 1]), Some(Some(66)));
        assert_eq!(vaxee_reply(&[0x0E, 0xA5, 0x0B, 0, 1, 15], 0x0B), Some(15));
        assert_eq!(vgn(&[0x65, 40]), Some(40));
    }

    #[test]
    fn voltage_trend() {
        let mut t = Trend::default();
        assert!(!t.update(3800));
        assert!(t.update(3860));
        assert!(!t.update(3800));
        assert_eq!(zowie_percent(3890), 50);
    }
}
