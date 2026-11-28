//! SteelSeries : casques et souris sans fil (VID 1038). Dérivé de BatteryHub (GPL-3.0), src/drivers/steelseries*.cpp.
//! Protocoles : HeadsetControl, rivalcfg, steel-mouse, HaloBattery. Seule une requête de niveau est envoyée.

use super::{clamp100, map100, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::Info;
use std::time::{Duration, Instant};

const VID: u16 = 0x1038;

fn headset(pid: u16, name: &str) -> Status {
    Status::new(format!("steelseries:{pid:04X}"), name, Kind::Headset)
}

fn mouse(pid: u16, name: &str) -> Status {
    Status::new(format!("steelseries:{pid:04X}"), name, Kind::Mouse)
}

/// Arctis 1 / 7X : 06 12 → [2]==01 hors ligne, [3] pourcentage.
pub fn arctis1(pid: u16, r: &[u8]) -> Option<Status> {
    let mut s = headset(pid, "SteelSeries Arctis 1 Wireless");
    s.connected = *r.get(2)? != 0x01;
    if s.connected {
        s.percent = Some(clamp100(i64::from(*r.get(3)?)));
    }
    Some(s)
}

/// Arctis 7 / Pro : 06 18 → [2] pourcentage.
pub fn arctis7(pid: u16, r: &[u8]) -> Option<Status> {
    let mut s = headset(pid, if pid == 0x1252 { "SteelSeries Arctis Pro" } else { "SteelSeries Arctis 7" });
    let level = *r.get(2)?;
    s.percent = Some(clamp100(i64::from(level)));
    s.connected = level != 0;
    Some(s)
}

/// Arctis 9 : 00 20 → [3] brut (0x64..0x9A), [4]==01 en charge.
pub fn arctis9(pid: u16, r: &[u8]) -> Option<Status> {
    let mut s = headset(pid, "SteelSeries Arctis 9");
    s.charging = *r.get(4)? == 0x01;
    s.percent = Some(map100(i64::from(r[3]), 0x64, 0x9A));
    Some(s)
}

/// Arctis Pro Wireless : 41 AA → [0]==02 hors ligne ; 40 AA → [0] niveau 0..4.
pub fn arctis_pro_wireless(pid: u16, status: &[u8], level: Option<&[u8]>) -> Option<Status> {
    let mut s = headset(pid, "SteelSeries Arctis Pro Wireless");
    s.connected = *status.first()? != 0x02;
    if s.connected {
        s.percent = level.and_then(|l| l.first()).filter(|&&l| l <= 4).map(|&l| map100(i64::from(l), 0, 4));
    }
    Some(s)
}

/// Arctis Nova Pro Wireless : 06 B0 → [15] état (01 hors ligne, 02 en charge), [6] niveau 0..8.
pub fn nova_pro(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 16 {
        return None;
    }
    let mut s = headset(pid, "SteelSeries Arctis Nova Pro Wireless");
    s.connected = r[15] != 0x01;
    s.charging = r[15] == 0x02;
    if s.connected {
        s.percent = Some(map100(i64::from(r[6]), 0, 8));
    }
    Some(s)
}

/// Famille Nova : 00 B0 → bloc d'état dont la disposition dépend du modèle.
pub fn nova_family(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 7 {
        return None;
    }
    let level = |v: u8| Some(clamp100(i64::from(v)));
    let mut s;
    if [0x220E, 0x2212, 0x2216, 0x2236].contains(&pid) {
        s = headset(pid, "SteelSeries Arctis 7+");
        s.connected = r[1] != 0x01;
        s.percent = Some(map100(i64::from(r[2]), 0, 4));
        s.charging = r[3] == 0x01;
    } else if [0x2269, 0x226D, 0x2232, 0x2253].contains(&pid) {
        let nova5 = pid == 0x2232 || pid == 0x2253;
        s = headset(pid, if nova5 { "SteelSeries Arctis Nova 5" } else { "SteelSeries Arctis Nova 3P Wireless" });
        s.connected = r[1] != 0x02;
        s.percent = level(r[3]);
        s.charging = r[4] == 0x01;
    } else if pid == 0x230A {
        // GameBuds : le plus faible des écouteurs sortis du boîtier
        s = headset(pid, "SteelSeries Arctis GameBuds");
        let buds = [(r[3], r[5]), (r[4], r[6])];
        let lowest = buds.iter().filter(|b| b.0 == 0x03).map(|b| b.1).min();
        s.connected = lowest.is_some();
        s.percent = lowest.map(|v| clamp100(i64::from(v)));
    } else {
        let discrete = [0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4].contains(&pid);
        s = headset(pid, "SteelSeries Arctis Nova 7");
        s.connected = r[3] != 0x00;
        s.charging = r[3] == 0x01 || r[3] == 0x02;
        s.percent = if discrete { Some(map100(i64::from(r[2]), 0, 4)) } else { level(r[2]) };
    }
    if !s.connected {
        s.percent = None;
    }
    Some(s)
}

pub struct Aerox {
    pid: u16,
    receiver: u16,
    name: &'static str,
    wired: bool,
    percent_layout: bool,
}

const fn model(pid: u16, receiver: u16, name: &'static str, wired: bool, percent_layout: bool) -> Aerox {
    Aerox { pid, receiver, name, wired, percent_layout }
}

const AEROX: [Aerox; 13] = [
    model(0x1838, 0x1838, "SteelSeries Aerox 3 Wireless", false, false),
    model(0x183A, 0x1838, "SteelSeries Aerox 3 Wireless", true, false),
    model(0x1878, 0x1878, "SteelSeries Aerox 3 Wireless CS2 Dragon Lore Edition", false, false),
    model(0x187A, 0x1878, "SteelSeries Aerox 3 Wireless CS2 Dragon Lore Edition", true, false),
    model(0x1890, 0x1890, "SteelSeries Aerox 3 Wireless Gen 2", false, true),
    model(0x1892, 0x1890, "SteelSeries Aerox 3 Wireless Gen 2", true, true),
    model(0x1852, 0x1852, "SteelSeries Aerox 5 Wireless", false, false),
    model(0x1854, 0x1852, "SteelSeries Aerox 5 Wireless", true, false),
    model(0x1858, 0x1858, "SteelSeries Aerox 9 Wireless", false, false),
    model(0x185A, 0x1858, "SteelSeries Aerox 9 Wireless", true, false),
    model(0x1840, 0x1840, "SteelSeries Prime Wireless", false, false),
    model(0x1842, 0x1840, "SteelSeries Prime Wireless", true, false),
    model(0x1860, 0x1860, "SteelSeries Aerox 5 Wireless Diablo IV Edition", false, false),
];

/// Aerox / Prime : deux dispositions (paliers de 5 %, ou pourcentage direct). Souris éteinte ou endormie : rien.
pub fn aerox(m: &Aerox, r: &[u8]) -> Option<Status> {
    if r.len() < if m.percent_layout { 3 } else { 2 } || (m.percent_layout && r[1] > 0x02) {
        return None;
    }
    let off = if m.percent_layout { !m.wired && r[1] == 0 && r[2] == 0 } else { r[1] & 0x7F == 0 };
    if off {
        return None;
    }
    let mut s = mouse(m.pid, m.name);
    s.key = format!("steelseries:{:04X}", m.receiver);
    if m.percent_layout {
        s.charging = r[1] != 0;
        s.percent = Some(r[2]).filter(|&p| p <= 100);
    } else if r[1] & 0x7F <= 21 {
        s.percent = Some(((r[1] & 0x7F) - 1) * 5);
        s.charging = r[1] & 0x80 != 0;
    }
    Some(s)
}

/// Rival 3 Wireless et Rival 650 : AA 01 → [0] pourcentage, [2] en charge.
pub fn rival(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 3 {
        return None;
    }
    let rival650 = pid == 0x172B || pid == 0x1726;
    let name = if rival650 {
        "SteelSeries Rival 650 Wireless"
    } else if pid == 0x1872 {
        "SteelSeries Rival 3 Wireless Gen 2"
    } else {
        "SteelSeries Rival 3 Wireless"
    };
    let mut s = mouse(if rival650 { 0x172B } else { pid }, name);
    s.percent = Some(clamp100(i64::from(r[0])));
    s.charging = r[2] != 0;
    Some(s)
}

/// Rival Pro : 92 → 92 00 <pourcentage>.
pub fn rival_pro(pid: u16, r: &[u8]) -> Option<Status> {
    if r.len() < 3 || r[0] != 0x92 || r[1] != 0x00 || r[2] > 100 {
        return None;
    }
    let mut s = mouse(pid, "SteelSeries Rival Pro");
    s.percent = Some(r[2]);
    Some(s)
}

fn write_read(d: &Dev, request: &[u8], timeout_ms: u64) -> Option<Vec<u8>> {
    d.flush();
    d.query(request, timeout_ms, |_| true)
}

/// La commande est renvoyée toutes les 100 ms jusqu'à son écho (le logiciel SteelSeries partage l'interface), 1 s au plus.
fn query_echo(d: &Dev, command: u8, min_len: usize) -> Option<Vec<u8>> {
    d.flush();
    let end = Instant::now() + Duration::from_secs(1);
    while Instant::now() < end {
        if !d.write(&[0x00, command]) {
            return None;
        }
        let resend = Instant::now() + Duration::from_millis(100);
        while Instant::now() < resend.min(end) {
            let Some(r) = d.read(resend.saturating_duration_since(Instant::now()).as_millis() as u64) else { break };
            if r.len() >= min_len && r[0] == command {
                return Some(r);
            }
            if r.len() >= 2 && r[0] == 0x40 && r[1] == 0xFF {
                return None; // le récepteur ne joint pas la souris
            }
        }
    }
    None
}

#[derive(Clone, Copy)]
enum Family {
    Arctis1,
    Arctis7,
    Arctis9,
    ProWireless,
    NovaPro,
    Nova,
    Aerox,
    Rival,
    RivalPro,
}

fn family(pid: u16) -> Option<Family> {
    Some(match pid {
        0x12B3 | 0x12B6 | 0x12D7 => Family::Arctis1,
        0x1260 | 0x12AD | 0x1252 => Family::Arctis7,
        0x12C2 => Family::Arctis9,
        0x1290 => Family::ProWireless,
        0x12E0 | 0x225D => Family::NovaPro,
        0x220E | 0x2212 | 0x2216 | 0x2236 | 0x2269 | 0x226D | 0x2232 | 0x2253 | 0x230A | 0x2202 | 0x22A1 | 0x227E | 0x2206 | 0x2258
        | 0x229E | 0x22AD | 0x223A | 0x22A9 | 0x227A | 0x22A4 | 0x22A5 | 0x220A | 0x22A7 => Family::Nova,
        0x1830 | 0x1872 | 0x172B | 0x1726 => Family::Rival,
        0x1880 => Family::RivalPro,
        _ if AEROX.iter().any(|a| a.pid == pid) => Family::Aerox,
        _ => return None,
    })
}

/// La collection à interroger pour ce produit.
fn accepts(family: Family, pid: u16, c: &Info) -> bool {
    let interface = |n| c.iface == n && c.output_len > 0;
    match family {
        Family::Arctis1 | Family::Arctis7 => c.usage_page == 0xFF43 && c.usage == 0x0202,
        Family::Arctis9 | Family::ProWireless => interface(0),
        Family::NovaPro => interface(4),
        Family::Nova => c.usage_page == 0xFFC0 && c.usage == 0x0001,
        Family::Aerox => interface(3),
        Family::Rival => interface(if pid == 0x172B || pid == 0x1726 { 0 } else { 3 }),
        Family::RivalPro => interface(2),
    }
}

fn exchange(family: Family, d: &Dev, pid: u16) -> Option<Status> {
    match family {
        Family::Arctis1 => arctis1(pid, &write_read(d, &[0x06, 0x12], 1000)?),
        Family::Arctis7 => arctis7(pid, &write_read(d, &[0x06, 0x18], 1000)?),
        Family::Arctis9 => arctis9(pid, &write_read(d, &[0x00, 0x20], 1000)?),
        Family::NovaPro => nova_pro(pid, &write_read(d, &[0x06, 0xB0], 1000)?),
        Family::Nova => nova_family(pid, &write_read(d, &[0x00, 0xB0], 1000)?),
        Family::ProWireless => {
            let ask = |c| write_read(d, &[0x00, c, 0xAA], 1000).or_else(|| write_read(d, &[c, 0xAA], 1000));
            let status = ask(0x41)?;
            let level = (status[0] != 0x02).then(|| ask(0x40)).flatten();
            arctis_pro_wireless(pid, &status, level.as_deref())
        }
        Family::Aerox => {
            let m = AEROX.iter().find(|a| a.pid == pid)?;
            let r = query_echo(d, if m.wired { 0x92 } else { 0xD2 }, if m.percent_layout { 3 } else { 2 })?;
            aerox(m, &r)
        }
        Family::Rival => rival(pid, &write_read(d, &[0x00, 0xAA, 0x01], 300)?),
        Family::RivalPro => {
            d.flush();
            let r = d.query(&[0x00, 0x92], 500, |r| r.len() >= 3 && r[0] == 0x92 && r[1] == 0)?;
            rival_pro(pid, &r)
        }
    }
}

pub fn poll(ctx: &mut Context) {
    for pid in ctx.products(VID) {
        let Some(family) = family(pid) else { continue };
        for i in ctx.find(VID, pid, |c| accepts(family, pid, c)) {
            if ctx.out_of_time() {
                return;
            }
            let Some(d) = Dev::open(&ctx.colls[i]) else { continue };
            let Some(status) = exchange(family, &d, pid) else { continue };
            // câble et récepteur d'une même souris : une seule entrée
            if ctx.out.iter().any(|o| o.key == status.key) {
                ctx.claim(VID, pid);
            } else {
                ctx.add(status, VID, pid);
            }
            break;
        }
    }
}

/// État cumulé de la base Arctis Nova Elite : (niveau, alimentation, en charge).
#[derive(Default)]
pub struct Elite {
    pub level: Option<u8>,
    pub power: Option<u8>,
    pub charging: bool,
}

pub fn absorb(r: &[u8], state: &mut Elite) {
    if r.len() < 5 {
        return;
    }
    if r[0] == 0x01 && r[1] == 0xB0 && r.len() > 15 {
        if r[6] <= 100 {
            state.level = Some(r[6]);
        }
        state.charging = r[15] == 0x02;
        state.power = Some(r[14]);
    } else if r[0] == 0x07 && r[1] == 0xB7 && r[2] <= 100 {
        state.level = Some(r[2]);
        state.charging = r[4] == 0x02;
    } else if r[0] == 0x07 && r[1] == 0xB5 {
        state.power = Some(r[4]);
    }
}

/// Arctis Nova Elite (1038:2244) : deux collections sur l'interface 3.
pub fn poll_elite(ctx: &mut Context) {
    const PID: u16 = 0x2244;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let mut found = ctx.find(VID, PID, |c| c.iface == 3 && (c.input_len > 0 || c.output_len > 0));
    found.sort_by_key(|&i| match ctx.colls[i].usage_page {
        0xFFC0 => 0,
        p if p >= 0xFF00 => 1,
        _ => 2,
    });
    let open: Vec<(u16, usize, Dev)> = found
        .iter()
        .filter_map(|&i| Dev::open(&ctx.colls[i]).map(|d| (ctx.colls[i].usage_page, ctx.colls[i].output_len, d)))
        .collect();
    let mut request = [0u8; 64];
    request[0] = 0x01;
    request[1] = 0xB0;
    open.iter().for_each(|o| o.2.flush());
    if !open.iter().any(|(page, out, d)| *page >= 0xFF00 && *out > 0 && d.write(&request)) {
        return;
    }
    let mut state = Elite::default();
    let end = Instant::now() + Duration::from_secs(1);
    while Instant::now() < end && (state.level.is_none() || state.power.is_none()) && state.power != Some(1) {
        for (_, _, d) in &open {
            if let Some(r) = d.read(30) {
                absorb(&r, &mut state);
            }
        }
    }
    let (Some(level), false) = (state.level, state.power == Some(1)) else { return };
    let mut s = headset(PID, "SteelSeries Arctis Nova Elite");
    s.percent = Some(level);
    s.charging = state.charging || state.power == Some(2);
    ctx.add(s, VID, PID);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn headsets() {
        assert_eq!(arctis1(0x12B3, &[6, 0x12, 3, 77]).unwrap().percent, Some(77));
        assert!(!arctis1(0x12B3, &[6, 0x12, 1, 77]).unwrap().connected);
        assert!(arctis1(0x12B3, &[6]).is_none());
        assert_eq!(arctis7(0x1260, &[6, 0x18, 55]).unwrap().percent, Some(55));
        let nine = arctis9(0x12C2, &[0, 0x20, 0, 0x9A, 1]).unwrap();
        assert_eq!((nine.percent, nine.charging), (Some(100), true));
        let mut r = [0u8; 16];
        r[6] = 4;
        assert_eq!(nova_pro(0x12E0, &r).unwrap().percent, Some(50));
    }

    #[test]
    fn nova_layouts() {
        let seven_plus = nova_family(0x220E, &[0, 0, 2, 1, 0, 0, 0]).unwrap();
        assert_eq!((seven_plus.percent, seven_plus.charging), (Some(50), true));
        assert_eq!(nova_family(0x2232, &[0, 3, 0, 80, 0, 0, 0]).unwrap().percent, Some(80));
        assert_eq!(nova_family(0x230A, &[0, 0, 0, 3, 3, 90, 40]).unwrap().percent, Some(40));
        let off = nova_family(0x22A1, &[0, 0, 60, 0, 0, 0, 0]).unwrap();
        assert_eq!((off.connected, off.percent), (false, None));
    }

    #[test]
    fn mice() {
        let wireless = &AEROX[0];
        let s = aerox(wireless, &[0xD2, 0x80 | 11]).unwrap();
        assert_eq!((s.percent, s.charging, s.key.as_str()), (Some(50), true, "steelseries:1838"));
        assert!(aerox(wireless, &[0xD2, 0]).is_none());
        assert_eq!(aerox(&AEROX[4], &[0xD2, 0, 64]).unwrap().percent, Some(64));
        assert_eq!(aerox(&AEROX[4], &[0xD2, 1, 0xFF]).unwrap().percent, None);
        assert_eq!(rival(0x1830, &[42, 0, 1]).unwrap().percent, Some(42));
        assert_eq!(rival_pro(0x1880, &[0x92, 0, 33]).unwrap().percent, Some(33));
        assert!(rival_pro(0x1880, &[0x92, 1, 33]).is_none());
    }

    #[test]
    fn elite_reports() {
        let mut state = Elite::default();
        absorb(&[0x07, 0xB7, 73, 0, 2], &mut state);
        absorb(&[0x07, 0xB5, 0, 0, 8], &mut state);
        assert_eq!((state.level, state.power, state.charging), (Some(73), Some(8), true));
    }
}
