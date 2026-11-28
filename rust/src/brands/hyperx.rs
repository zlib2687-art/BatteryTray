//! HyperX : casques sans fil et souris Pulsefire Haste 2 (VID 03F0 et 0951). Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/hyperx.cpp (sources : HyperXWM, HyperHeadset, HyperBatteryMonitor, HeadsetControl). Lecture seule.

use super::{clamp100, interpolate, pause, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::Info;
use std::cell::Cell;
use std::time::{Duration, Instant};

fn base(pid: u16, name: &str) -> Status {
    Status::new(format!("hyperx:{pid:04X}"), name, Kind::Headset)
}

fn byte(r: Option<&[u8]>, at: usize, min_len: usize) -> Option<u8> {
    r.filter(|r| r.len() >= min_len).map(|r| r[at])
}

/// Cloud III : 82 → [2] connecté, 8A → [2] en charge, 89 → [4] pourcentage.
pub fn cloud3(pid: u16, link: &[u8], charge: Option<&[u8]>, battery: Option<&[u8]>) -> Option<Status> {
    if link.len() < 5 {
        return None;
    }
    let core = pid == 0x069F || pid == 0x0995;
    let mut s = base(pid, if core { "HyperX Cloud II Core Wireless" } else { "HyperX Cloud III Wireless" });
    s.connected = link[2] != 0;
    if s.connected {
        s.charging = byte(charge, 2, 5) == Some(1);
        s.percent = byte(battery, 4, 5).filter(|&p| p <= 100);
    }
    Some(s)
}

/// Cloud II Wireless : 02 → [7] pourcentage, 03 → [4] en charge.
pub fn cloud2(pid: u16, battery: &[u8], charge: Option<&[u8]>, kingston: bool) -> Option<Status> {
    let percent = byte(Some(battery), 7, 8).filter(|&p| p <= 100)?;
    let flight_s = kingston && (pid == 0x16EA || pid == 0x16EB);
    let mut s = base(pid, if flight_s { "HyperX Cloud Flight S" } else { "HyperX Cloud II Wireless" });
    s.percent = Some(percent);
    s.charging = byte(charge, 4, 5) == Some(1);
    Some(s)
}

/// Cloud Alpha : 03 → [3]==1 hors ligne, 0C → [3]==1 en charge, 0B → [3] pourcentage.
pub fn cloud_alpha(pid: u16, link: &[u8], charge: Option<&[u8]>, battery: Option<&[u8]>) -> Option<Status> {
    if link.len() < 4 {
        return None;
    }
    let mut s = base(pid, "HyperX Cloud Alpha Wireless");
    s.connected = link[3] != 1;
    if s.connected {
        s.charging = byte(charge, 3, 4) == Some(1);
        s.percent = byte(battery, 3, 4).filter(|&p| p <= 100);
    }
    Some(s)
}

/// Cloud Alpha 2 : 52 01 → [2] connecté ; 50 02 → [2] pourcentage, [3] en charge.
pub fn cloud_alpha2(pid: u16, link: &[u8], battery: Option<&[u8]>) -> Option<Status> {
    if link.len() < 8 {
        return None;
    }
    let mut s = base(pid, "HyperX Cloud Alpha 2 Wireless");
    s.connected = link[2] != 0;
    if let (true, Some(b)) = (s.connected, battery.filter(|b| b.len() >= 8 && b[2] <= 100)) {
        s.percent = Some(b[2]);
        s.charging = b[3] == 1;
    }
    Some(s)
}

/// Pulsefire Haste 2 ; `settled` : sous tension depuis assez longtemps (elle annonce 40 % au démarrage).
pub fn haste(pid: u16, link: Option<&[u8]>, battery: Option<&[u8]>, settled: bool) -> Option<Status> {
    let link = link.filter(|l| l.len() >= 8);
    let linked = link.is_none_or(|l| l[2] != 0);
    let battery = battery.filter(|b| linked && b.len() >= 8 && b[2] <= 100);
    if link.is_none() && battery.is_none() {
        return None;
    }
    let name = match pid {
        0x0AB5 => "HyperX Pulsefire Haste 2 Core Wireless",
        0x0DA0 => "HyperX Pulsefire Haste 2 Mini",
        _ => "HyperX Pulsefire Haste 2 Wireless",
    };
    let mut s = base(pid, name);
    s.kind = Kind::Mouse;
    s.connected = linked;
    if let (Some(b), true) = (battery, settled) {
        s.percent = Some(b[2]);
        s.charging = b[3] == 1 || b[3] == 2;
    }
    Some(s)
}

/// Cloud Flight : tension → niveau (au-dessus de 0x100B : en charge).
pub fn cloud_flight(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 5 {
        return None;
    }
    let mv = i64::from(u16::from_be_bytes([r[3], r[4]]));
    let mut s = base(pid, "HyperX Cloud Flight");
    if mv > 0x100B {
        s.charging = true;
    } else if mv <= 3648 {
        s.percent = Some(clamp100((0.00125 * mv as f64) as i64));
    } else if mv > 3975 {
        s.percent = Some(100);
    } else {
        let v = mv as f64;
        let curve = 0.000_000_025_475_05 * v.powi(4) - 0.000_390_029_9 * v.powi(3) + 2.238_321 * v * v - 5706.256 * v + 5_452_299.0;
        s.percent = Some(clamp100(curve.round() as i64));
    }
    Some(s)
}

/// Cloud III S : 02 → [6]==2 connecté, 06 → [6] pourcentage, 48 → [6] 0 décharge, 1 charge, 2 plein.
pub fn cloud3s(pid: u16, link: &[u8], battery: Option<&[u8]>, charge: Option<&[u8]>) -> Option<Status> {
    if link.len() < 7 {
        return None;
    }
    let mut s = base(pid, "HyperX Cloud III S Wireless");
    s.connected = link[6] == 2;
    if s.connected {
        s.percent = byte(battery, 6, 7).filter(|&p| p <= 100);
        s.charging = matches!(byte(charge, 6, 7), Some(1 | 2));
    }
    Some(s)
}

const PERCENTS: [i64; 11] = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0];
const CHARGING_MV: [i64; 11] = [4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400];
const DISCHARGING_MV: [i64; 11] = [4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517];

/// Cloud Stinger Core : connexion [11]==1 ; batterie [9] 03 décharge, 05 charge, 06 plein, [11..12] millivolts.
pub fn stinger(pid: u16, connection: &[u8], battery: Option<&[u8]>) -> Option<Status> {
    if connection.len() < 13 {
        return None;
    }
    let name = match pid {
        0x173F => "HyperX Cloud Core Wireless",
        0x0A8E => "HyperX Cloud Stinger Core Wireless DTS",
        _ => "HyperX Cloud Stinger Core Wireless",
    };
    let mut s = base(pid, name);
    s.connected = connection[11] == 1;
    let Some(b) = battery.filter(|b| s.connected && b.len() >= 13 && matches!(b[9], 3 | 5 | 6)) else { return Some(s) };
    let mv = i64::from(u16::from_le_bytes([b[11], b[12]]));
    s.charging = b[9] == 5;
    let table = if s.charging { &CHARGING_MV } else { &DISCHARGING_MV };
    s.percent = Some(if b[9] == 6 { 100 } else { interpolate(mv, table, &PERCENTS) });
    Some(s)
}

#[derive(Clone, Copy)]
enum Family {
    Cloud3,
    Cloud3S,
    Cloud2,
    Kingston2,
    Alpha,
    Alpha2,
    Haste,
    Flight,
    Stinger,
}

fn family(vid: u16, pid: u16) -> Option<Family> {
    Some(match (vid, pid) {
        (0x03F0, 0x05B7 | 0x0C9D | 0x069F | 0x0995) => Family::Cloud3,
        (0x03F0, 0x06BE | 0x02CC) => Family::Cloud3S,
        (0x03F0, 0x0696 | 0x018B | 0x0D93 | 0x0C8C) => Family::Cloud2,
        (0x0951, 0x1718 | 0x16EA | 0x16EB) => Family::Kingston2,
        (0x03F0, 0x098D) => Family::Alpha,
        (0x03F0, 0x08BE) => Family::Alpha2,
        (0x03F0, 0x0AB5 | 0x0D97 | 0x0F98 | 0x0DA0) => Family::Haste,
        (0x0951, 0x16C4 | 0x1723 | 0x1749) | (0x03F0, 0x0E90) => Family::Flight,
        (0x03F0, 0x0A93 | 0x0A8E) | (0x0951, 0x170B | 0x1713 | 0x173F) => Family::Stinger,
        _ => return None,
    })
}

fn accepts(family: Family, pid: u16, c: &Info) -> bool {
    let vendor_page = c.usage_page >= 0xFF00;
    match family {
        Family::Cloud3 if pid == 0x069F || pid == 0x0995 => vendor_page && c.output_len >= 62 && c.input_len >= 62,
        Family::Cloud3 => c.output_len == 62 && c.input_len == 62,
        Family::Cloud3S => c.output_len == 64 && c.input_len == 64,
        Family::Cloud2 => c.usage_page == 0xFF90 && c.usage == 0x0303,
        Family::Kingston2 if pid == 0x1718 => c.output_len == 62,
        Family::Kingston2 => c.usage_page == 0xFF13 && c.usage == 0x0001 && c.input_len == 62 && c.output_len == 62,
        Family::Alpha => vendor_page && c.output_len >= 3,
        Family::Alpha2 => c.usage_page == 0xFF13 && c.usage == 0xFF00,
        Family::Haste => vendor_page && c.output_len >= 2 && c.input_len >= 8,
        Family::Flight => vendor_page && c.output_len >= 3,
        Family::Stinger => c.usage_page == 0xFF00 && c.usage == 0x0001 && c.feature_len >= 32,
    }
}

const SETTLE: Duration = Duration::from_secs(6);

thread_local!(static LINK_UP: Cell<(Option<Instant>, bool)> = const { Cell::new((None, false)) });

/// Vrai quand la souris est sous tension depuis assez longtemps. Au premier relevé, on la suppose allumée depuis un moment.
fn settled(linked: bool) -> bool {
    let (mut since, polled) = LINK_UP.get();
    let now = Instant::now();
    if !linked {
        since = None;
    } else if since.is_none() {
        since = if polled { Some(now) } else { now.checked_sub(SETTLE).or(Some(now)) };
    }
    LINK_UP.set((since, true));
    since.is_some_and(|s| now.duration_since(s) >= SETTLE)
}

fn stinger_exchange(c: &Info, pid: u16) -> Option<Status> {
    const CONNECTION: [u8; 20] = [0xFF, 0x07, 0, 0xFD, 0x04, 0, 0, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0, 0, 0x03, 0x68, 0x10, 0, 0];
    const BATTERY: [u8; 12] = [0xFF, 0x07, 0, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C];
    // les récepteurs PlayStation et DTS peuvent renvoyer le rapport précédent : on relit
    let slow = matches!(pid, 0x0A93 | 0x1713 | 0x173F | 0x0A8E);
    let mut last: Option<Vec<u8>> = None;
    let mut ask = |command: &[u8], good: fn(&[u8]) -> bool| -> Option<Vec<u8>> {
        if !crate::hid::feature_write(c, command) {
            return None;
        }
        for _ in 0..if slow { 10 } else { 1 } {
            pause(20);
            let r = crate::hid::feature_read(c, 0xFF, c.feature_len).filter(|r| r.len() >= 13)?;
            last = Some(r.clone());
            if !slow || good(&r) {
                return Some(r);
            }
        }
        None
    };
    let connection = ask(&CONNECTION, |r| !(r[1] == 0x01 && r[2] == 0));
    let battery = ask(&BATTERY, |r| r[0] == 0xFF && r[1] == 0x12 && r[2] == 0x05);
    let connection = connection.or_else(|| if slow { last.clone() } else { None })?;
    stinger(pid, &connection, battery.as_deref())
}

fn exchange(family: Family, c: &Info, pid: u16) -> Option<Status> {
    if matches!(family, Family::Stinger) {
        return stinger_exchange(c, pid);
    }
    let d = Dev::open(c)?;
    match family {
        Family::Cloud3 => {
            let ask = |cmd| d.query(&[0x66, cmd], 800, |r| r.len() >= 5 && r[0] == 0x66 && r[1] == cmd);
            let link = ask(0x82)?;
            let (charge, battery) = if link[2] != 0 { (ask(0x8A), ask(0x89)) } else { (None, None) };
            cloud3(pid, &link, charge.as_deref(), battery.as_deref())
        }
        Family::Cloud3S => {
            let ask = |cmd| {
                d.query(&[0x0C, 0x02, 0x03, 0x01, 0x00, cmd], 1000, |r| r.len() >= 7 && r[0] == 0x0C && r[5] == cmd && r[6] != 0xFF)
            };
            let link = ask(0x02)?;
            let (battery, charge) = if link[6] == 2 { (ask(0x06), ask(0x48)) } else { (None, None) };
            cloud3s(pid, &link, battery.as_deref(), charge.as_deref())
        }
        Family::Cloud2 => {
            let ask = |cmd| {
                d.query(&[0x06, 0xFF, 0xBB, cmd], 1000, |r| r.len() >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == cmd)
            };
            let battery = ask(0x02)?;
            cloud2(pid, &battery, ask(0x03).as_deref(), false)
        }
        Family::Kingston2 => {
            let ask = |cmd| {
                let request = [0x06, 0, 0x02, 0, 0x9A, 0, 0, 0x68, 0x4A, 0x8E, 0x0A, 0, 0, 0, 0xBB, cmd, 0];
                d.query(&request, 1000, |r| r.len() >= 8 && r[0] == 0x0B && r[2] == 0xBB && r[3] == cmd)
            };
            let battery = ask(0x02)?;
            cloud2(pid, &battery, ask(0x03).as_deref(), true)
        }
        Family::Alpha => {
            let ask = |cmd| d.query(&[0x21, 0xBB, cmd], 1000, |r| r.len() >= 4 && r[0] == 0x21 && r[2] == cmd);
            let link = ask(0x03)?;
            let (charge, battery) = if link[3] != 1 { (ask(0x0C), ask(0x0B)) } else { (None, None) };
            cloud_alpha(pid, &link, charge.as_deref(), battery.as_deref())
        }
        Family::Alpha2 | Family::Haste => {
            let ask = |cmd: u8, sub: u8| d.query(&[cmd, sub], 1000, |r| r.len() >= 8 && r[0] == cmd + 1 && r[1] == sub);
            let link = ask(0x52, 0x01);
            let linked = link.as_ref().is_none_or(|l| l[2] != 0);
            let battery = if linked { ask(0x50, 0x02) } else { None };
            if matches!(family, Family::Alpha2) {
                cloud_alpha2(pid, &link?, battery.as_deref())
            } else {
                haste(pid, link.as_deref(), battery.as_deref(), settled(linked))
            }
        }
        Family::Flight => cloud_flight(pid, &d.query(&[0x21, 0xFF, 0x05], 1000, |r| r.len() >= 5)?),
        Family::Stinger => None,
    }
}

pub fn poll(ctx: &mut Context) {
    for vid in [0x03F0, 0x0951] {
        for pid in ctx.products(vid) {
            let Some(family) = family(vid, pid) else { continue };
            for i in ctx.find(vid, pid, |c| accepts(family, pid, c)) {
                if ctx.out_of_time() {
                    return;
                }
                if let Some(status) = exchange(family, &ctx.colls[i], pid) {
                    ctx.add(status, vid, pid);
                    break;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn simple_frames() {
        let link = [0x66, 0x82, 1, 0, 0];
        let s = cloud3(0x0C9D, &link, Some(&[0x66, 0x8A, 1, 0, 0]), Some(&[0x66, 0x89, 0, 0, 64])).unwrap();
        assert_eq!((s.percent, s.charging), (Some(64), true));
        assert_eq!(cloud2(0x0696, &[6, 0xFF, 0xBB, 2, 0, 0, 0, 88], None, false).unwrap().percent, Some(88));
        assert_eq!(cloud_alpha(0x098D, &[0x21, 0xBB, 3, 0], None, Some(&[0x21, 0xBB, 0x0B, 45])).unwrap().percent, Some(45));
        assert!(!cloud_alpha(0x098D, &[0x21, 0xBB, 3, 1], None, None).unwrap().connected);
        assert!(cloud3(0x0C9D, &[0x66], None, None).is_none());
    }

    #[test]
    fn haste_startup_reading_is_ignored() {
        let link = [0x53, 1, 1, 0, 0, 0, 0, 0];
        let battery = [0x51, 2, 40, 0, 0, 0, 0, 0];
        assert_eq!(haste(0x0F98, Some(&link), Some(&battery), false).unwrap().percent, None);
        assert_eq!(haste(0x0F98, Some(&link), Some(&battery), true).unwrap().percent, Some(40));
        assert!(haste(0x0F98, None, None, true).is_none());
    }

    #[test]
    fn voltage_based_headsets() {
        assert_eq!(cloud_flight(0x16C4, &[0, 0, 0, 0x0F, 0xA0]).unwrap().percent, Some(100));
        assert!(cloud_flight(0x16C4, &[0, 0, 0, 0x10, 0x20]).unwrap().charging);
        let mut connection = [0u8; 13];
        connection[11] = 1;
        let mut battery = [0u8; 13];
        battery[9] = 3;
        battery[11..13].copy_from_slice(&3838u16.to_le_bytes());
        assert_eq!(stinger(0x0A93, &connection, Some(&battery)).unwrap().percent, Some(60));
    }
}
