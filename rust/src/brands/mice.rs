//! Souris à rapport de fonctionnalité de 64 octets : Glorious, Hitscan, G-Wolves, WLMouse et LAMZU ; Cooler Master.
//! Dérivé de BatteryHub (GPL-3.0), src/drivers/glorious.cpp, lamzu.cpp et coolermaster.cpp (sources : glorious-indicator,
//! glorious-ctl, configurateurs web des fabricants, coolermaster-usb-protocol). Lecture seule.

use super::{pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid;

const FEATURE_LEN: usize = 65;

/// Requête commune : [3]=02 [4]=02 [6]=83.
pub fn battery_request() -> [u8; FEATURE_LEN] {
    let mut w = [0u8; FEATURE_LEN];
    (w[3], w[4], w[6]) = (0x02, 0x02, 0x83);
    w
}

#[derive(Debug, PartialEq, Clone, Copy)]
pub enum Awake {
    Invalid,
    WakingUp,
    Awake(u8, bool),
    Asleep(bool),
}

/// Réponse Glorious : état A1 éveillée, A4 en veille, A0 se réveille ; [7] charge, [8] pourcentage.
pub fn parse_glorious(r: &[u8]) -> Awake {
    if r.len() < 9 || r[6] != 0x83 || r[1] & 0xF0 != 0xA0 {
        return Awake::Invalid;
    }
    let charging = r[7] == 0x01;
    match r[1] {
        0xA0 => Awake::WakingUp,
        0xA1 if r[8] <= 100 => Awake::Awake(r[8].max(1), charging),
        _ => Awake::Asleep(charging),
    }
}

fn glorious_name(pid: u16) -> Option<&'static str> {
    Some(match pid {
        0x2022 | 0x2011 => "Glorious Model O Wireless",
        0x2023 | 0x2012 => "Glorious Model D Wireless",
        0x2024 | 0x2013 => "Glorious Model O- Wireless",
        0x2025 | 0x2014 => "Glorious Model D- Wireless",
        0x2033 => "Glorious Model O 2 Wireless",
        0x2034 | 0x2036 | 0x201A => "Glorious Model D 2 PRO",
        _ => return None,
    })
}

/// Noms G-Wolves d'après la liste de mouse.xyz.
pub fn gwolves_name(pid: u16) -> &'static str {
    match pid {
        0x3808 | 0x3817 => "G-Wolves HTM Plus",
        0x6808 | 0x6817 => "G-Wolves HSK Pro 2.0",
        0x5608 | 0x5617..=0x5619 => "G-Wolves HTXU",
        0x3608 | 0x3617 | 0x3619 => "G-Wolves Fenrir Pro",
        0x3908 | 0x3917 => "G-Wolves VUK",
        0x7904 | 0x7913 => "G-Wolves HT-S2",
        0x3708 | 0x3717 => "G-Wolves Fenrir Max",
        0x5308 | 0x5317 => "G-Wolves HTS Ultra",
        0x7704 | 0x7713 => "G-Wolves HTR",
        0x3508 | 0x3517 => "G-Wolves Fenrir",
        0x7908 | 0x7917 => "G-Wolves HT-S2 Pro",
        0x5808 | 0x5817 => "G-Wolves HSK Pro",
        0x2708 | 0x2717 | 0x2719 => "G-Wolves HTX Mini",
        0x5408 | 0x5417..=0x5419 => "G-Wolves HTS Plus",
        0x5708 | 0x5717 => "G-Wolves HTX",
        0x5908 | 0x5917 => "G-Wolves HSK Plus",
        0x7204 | 0x7203 => "G-Wolves HSK Lite",
        0x7708 | 0x7717 => "G-Wolves HTR Pro",
        0x5804 | 0x5803 => "G-Wolves HSK Pro ACE",
        0x5404 | 0x5403 => "G-Wolves HTS Plus ACE",
        0x5704 | 0x5703 => "G-Wolves HTX ACE",
        0x5904 | 0x5903 => "G-Wolves HSK Plus ACE",
        0x4718 | 0x4719 => "G-Wolves Lycan",
        0x4219 => "G-Wolves WARG",
        0x5219 => "G-Wolves HTS Plus Pro",
        0x3519 => "G-Wolves Fenrir Asym",
        0x2619 => "G-Wolves HTX Mini Asym",
        0x1619 => "G-Wolves Fenrir Max Asym",
        0x2119 => "G-Wolves VUK Mini",
        _ => "G-Wolves wireless mouse",
    }
}


const GWOLVES_CABLE: [u16; 36] = [
    0x1619, 0x2119, 0x2619, 0x2708, 0x2719, 0x3508, 0x3519, 0x3608, 0x3619, 0x3708, 0x3808, 0x3908, 0x4219, 0x4718, 0x4719, 0x5219,
    0x5308, 0x5404, 0x5408, 0x5418, 0x5419, 0x5608, 0x5618, 0x5619, 0x5704, 0x5708, 0x5804, 0x5808, 0x5904, 0x5908, 0x6808, 0x7204,
    0x7704, 0x7708, 0x7904, 0x7908,
];

/// Pour un produit inconnu de l'identifiant partagé 258A : clavier ou souris d'après le nom puis les collections.
fn shared_chip_kind(ctx: &Context, vid: u16, pid: u16, product: &str) -> Kind {
    let guessed = crate::generic::guess_kind(product);
    if guessed != Kind::Other {
        return guessed;
    }
    let count = |usage| ctx.colls.iter().filter(|c| c.vid == vid && c.pid == pid && c.usage_page == 1 && c.usage == usage).count();
    if count(0x06) >= 2 {
        Kind::Keyboard
    } else if count(0x02) > 0 {
        Kind::Mouse
    } else if count(0x05) > 0 {
        Kind::Gamepad
    } else if count(0x06) >= 1 {
        Kind::Keyboard
    } else {
        Kind::Mouse
    }
}

pub fn poll_glorious(ctx: &mut Context) {
    for (vid, prefix, delay) in [(0x258Au16, "glorious", 50), (0x3770, "hitscan", 200), (0x33E4, "gwolves", 100), (0x36A7, "wlmouse", 100)] {
        for pid in ctx.products(vid) {
            // le récepteur WLMouse est partagé : seul le récepteur YING 8K et sa collection batterie sont reconnus
            let found = ctx.find(vid, pid, |c| {
                c.feature_len == FEATURE_LEN && (vid != 0x36A7 || (c.pid == 0xA874 && c.usage_page == 0xFFFF && c.usage == 0))
            });
            for i in found {
                if ctx.out_of_time() {
                    return;
                }
                let c = &ctx.colls[i];
                let mut answer = None;
                for attempt in 0..2 {
                    if !hid::feature_write(c, &battery_request()) {
                        break;
                    }
                    pause(delay);
                    let Some(r) = hid::feature_read(c, 0, c.feature_len) else { break };
                    match parse_glorious(&r) {
                        Awake::Invalid => break,
                        Awake::WakingUp if attempt == 0 => pause(400),
                        state => {
                            answer = Some(state);
                            break;
                        }
                    }
                }
                let Some(state) = answer else { continue };
                let known = match vid {
                    0x258A => glorious_name(pid),
                    0x3770 => Some(match pid {
                        0x0136 => "Hitscan Hyperlight X",
                        0x0194 => "Hitscan Hyperlight N",
                        _ => "Hitscan Hyperlight",
                    }),
                    0x33E4 => Some(gwolves_name(pid)),
                    _ => Some("WLMouse YING"),
                };
                let cable = match vid {
                    0x258A => matches!(pid, 0x2011..=0x2014 | 0x201A),
                    0x3770 => matches!(pid, 0x0136 | 0x0194),
                    0x33E4 => GWOLVES_CABLE.contains(&pid),
                    _ => false,
                };
                let product = c.product.clone();
                let name = known.map_or_else(|| if product.trim().is_empty() { "Wireless device".into() } else { product.clone() }, String::from);
                let kind = if known.is_some() { Kind::Mouse } else { shared_chip_kind(ctx, vid, pid, &product) };
                let mut s = Status::new(format!("{prefix}:{pid:04X}"), name, kind);
                match state {
                    Awake::Awake(percent, charging) => {
                        s.percent = Some(percent);
                        s.charging = cable || charging;
                    }
                    _ => s.connected = false,
                }
                ctx.add(s, vid, pid);
                break;
            }
        }
    }
}

/// Réponse LAMZU, avec ou sans l'octet d'identifiant : (charge, pourcentage).
pub fn parse_lamzu(r: &[u8]) -> Option<(u8, u8)> {
    if r.len() < 10 {
        return None;
    }
    let (charge, percent) = if r[1] == 0xA1 && r[4] == 0x02 && r[6] == 0x83 {
        (r[7], r[8])
    } else if r[0] == 0xA1 && r[3] == 0x02 && r[5] == 0x83 {
        (r[6], r[7])
    } else {
        return None;
    };
    (percent <= 100).then_some((charge, percent))
}

fn lamzu_name(vid: u16, pid: u16) -> &'static str {
    match (vid, pid) {
        (0x373E, 0x001C..=0x001E) => "LAMZU Maya X",
        (0x373E, 0x0016) | (0x37B0, 0x001F) => "LAMZU Maya X V2",
        (0x37B0, 0x0005 | 0x000B | 0x000C) => "LAMZU Tachi",
        (0x37B0, 0x001C | 0x001E) => "LAMZU Tachi Lite",
        (0x37B0, 0x0009 | 0x000F | 0x0010) => "LAMZU Inca",
        (0x37B0, 0x0011 | 0x0013 | 0x0015 | 0x003C | 0x0050) => "LAMZU Maya",
        (0x37B0, 0x0007 | 0x000D | 0x000E) => "LAMZU Paro",
        (0x37B0, 0x0017 | 0x0019 | 0x001B) => "LAMZU Thorn",
        (0x37B0, 0x0021 | 0x0003 | 0x0023 | 0x0030 | 0x0032 | 0x0040) => "LAMZU Thorn V2",
        (0x37B0, 0x0025 | 0x0027) => "LAMZU Atlantis OG Champion",
        (0x37B0, 0x0028 | 0x002A | 0x002B | 0x003E | 0x003F) => "LAMZU Atlantis Mini",
        (0x37B0, 0x002C | 0x0052) => "LAMZU Maya M",
        (0x37B0, 0x003A | 0x003B | 0x0044) => "LAMZU Maya X",
        (0x37B0, 0x0042 | 0x0056) => "LAMZU Orcus",
        (0x37B0, 0x0046 | 0x0054) => "LAMZU Mini",
        _ => "LAMZU wireless mouse",
    }
}

fn lamzu_cable(vid: u16, pid: u16) -> bool {
    const WIRED: [u16; 24] = [
        0x05, 0x07, 0x09, 0x11, 0x17, 0x1C, 0x1F, 0x21, 0x25, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3A, 0x3C, 0x3E, 0x40, 0x42, 0x44, 0x46,
        0x50, 0x52, 0x56,
    ];
    if vid == 0x373E { pid == 0x001C } else { WIRED.contains(&pid) }
}

pub fn poll_lamzu(ctx: &mut Context) {
    for vid in [0x373E, 0x37B0] {
        for pid in ctx.products(vid) {
            for i in ctx.find(vid, pid, |c| c.feature_len == FEATURE_LEN) {
                let c = &ctx.colls[i];
                if !hid::feature_write(c, &battery_request()) {
                    continue;
                }
                pause(100);
                let Some((charge, percent)) = hid::feature_read(c, 0, c.feature_len).and_then(|r| parse_lamzu(&r)) else { continue };
                let mut s = Status::new(format!("lamzu:{vid:04X}:{pid:04X}"), lamzu_name(vid, pid), Kind::Mouse);
                // 0 depuis un récepteur : souris éteinte ou hors de portée
                s.connected = percent > 0 || lamzu_cable(vid, pid);
                s.percent = (percent > 0).then_some(percent);
                s.charging = charge == 1;
                ctx.add(s, vid, pid);
                break;
            }
        }
    }
}

/// Rapport d'état Cooler Master [47][81]… : [10] %, [11] en charge.
pub fn parse_cooler_master(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 13 || r[0] != 0x47 || r[1] != 0x81 {
        return None;
    }
    let name = match (pid, r[2]) {
        (0x0169, _) | (0x01B7, 0x50) => "Cooler Master MM712",
        (0x0167, _) | (0x01B7, 0x70) => "Cooler Master MM731",
        _ => "Cooler Master wireless mouse",
    };
    let mut s = Status::new(format!("coolermaster:{pid:04X}"), name, Kind::Mouse);
    s.percent = Some(r[10]).filter(|p| (1..=100).contains(p));
    s.charging = r[11] != 0;
    Some(s)
}

pub fn poll_cooler_master(ctx: &mut Context) {
    const VID: u16 = 0x2516;
    for pid in ctx.products(VID) {
        if ![0x01B7, 0x0169, 0x0167].contains(&pid) {
            continue;
        }
        for i in ctx.find(VID, pid, |c| c.iface == 3 && c.usage_page == 0xFF0A) {
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            d.flush();
            let Some(r) = d.query(&[0x00, 0x46, 0x81, 0x00], 500, |r| r.len() >= 13 && r[0] == 0x47 && r[1] == 0x81) else { continue };
            let Some(s) = parse_cooler_master(pid, &r) else { continue };
            // câble et récepteur branchés ensemble : une seule souris
            if let Some(other) = ctx.out.iter_mut().find(|o| o.name == s.name) {
                other.charging |= s.charging;
                ctx.claim(VID, pid);
            } else {
                ctx.add(s, VID, pid);
            }
            break;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn glorious_states() {
        assert_eq!(parse_glorious(&[0, 0xA1, 0, 0, 2, 0, 0x83, 0, 64]), Awake::Awake(64, false));
        assert_eq!(parse_glorious(&[0, 0xA1, 0, 0, 2, 0, 0x83, 1, 0]), Awake::Awake(1, true));
        assert_eq!(parse_glorious(&[0, 0xA0, 0, 0, 2, 0, 0x83, 0, 0]), Awake::WakingUp);
        assert_eq!(parse_glorious(&[0, 0xA4, 0, 0, 2, 0, 0x83, 0, 50]), Awake::Asleep(false));
        assert_eq!(parse_glorious(&[0, 0x11, 0, 0, 2, 0, 0x83, 0, 50]), Awake::Invalid);
    }

    #[test]
    fn lamzu_and_cooler_master() {
        assert_eq!(parse_lamzu(&[0, 0xA1, 0, 0, 2, 0, 0x83, 1, 70, 0]), Some((1, 70)));
        assert_eq!(parse_lamzu(&[0xA1, 0, 0, 2, 0, 0x83, 0, 55, 0, 0]), Some((0, 55)));
        assert_eq!(parse_lamzu(&[0xA1, 0, 0, 2, 0, 0x83, 0, 200, 0, 0]), None);
        let mut r = [0u8; 13];
        (r[0], r[1], r[2], r[10], r[11]) = (0x47, 0x81, 0x50, 88, 0);
        let s = parse_cooler_master(0x01B7, &r).unwrap();
        assert_eq!((s.name.as_str(), s.percent), ("Cooler Master MM712", Some(88)));
    }
}
