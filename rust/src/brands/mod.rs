//! Pilotes de marques, portés des pilotes C# de BatteryTray, eux-mêmes dérivés de BatteryHub
//! (krisgfx9, GPL-3.0) : voir NOTICE.md. Lecture seule, identifiants exacts. Non essayés sur du matériel.

mod astro;
mod compx;
mod corsair;
mod gamepads;
mod headsets;
mod mice;
mod bluetooth;
mod external;
mod atk;
mod audio_pads;
mod small_b;
mod small_a;
mod misc;
mod hyperx;
mod adverts;
mod json;
mod keyboards;
mod keychron;
mod logitech;
mod playstation;
mod razer;
mod rog;
mod steelseries;

use crate::devices::{Kind, Reading};
use crate::hid::{self, Info, Stream};
use std::time::{Duration, Instant};

const BUDGET: Duration = Duration::from_secs(15);

/// Résultat d'un pilote pour un appareil.
#[derive(Clone, Debug, PartialEq)]
pub struct Status {
    pub key: String,
    pub name: String,
    pub kind: Kind,
    pub percent: Option<u8>,
    pub charging: bool,
    /// Faux : éteint ou hors de portée, aucune valeur n'est affichée.
    pub connected: bool,
}

impl Status {
    pub fn new(key: impl Into<String>, name: impl Into<String>, kind: Kind) -> Status {
        Status { key: key.into(), name: name.into(), kind, percent: None, charging: false, connected: true }
    }
}

pub struct Context {
    pub colls: Vec<Info>,
    claimed: Vec<(u16, u16)>,
    pub out: Vec<Status>,
    started: Instant,
}

impl Context {
    fn new(colls: Vec<Info>) -> Context {
        Context { colls, claimed: Vec::new(), out: Vec::new(), started: Instant::now() }
    }

    pub fn is_claimed(&self, vid: u16, pid: u16) -> bool {
        self.claimed.contains(&(vid, pid))
    }

    pub fn claim(&mut self, vid: u16, pid: u16) {
        self.claimed.push((vid, pid));
    }

    pub fn add(&mut self, status: Status, vid: u16, pid: u16) {
        self.out.push(status);
        self.claim(vid, pid);
    }

    /// Un récepteur muet ne doit pas bloquer la lecture des autres appareils.
    pub fn out_of_time(&self) -> bool {
        self.started.elapsed() > BUDGET
    }

    /// Produits d'un fabricant pas encore pris en charge.
    pub fn products(&self, vid: u16) -> Vec<u16> {
        let mut pids: Vec<u16> =
            self.colls.iter().filter(|c| c.vid == vid && !self.is_claimed(vid, c.pid)).map(|c| c.pid).collect();
        pids.sort_unstable();
        pids.dedup();
        pids
    }

    /// Index des collections d'un produit qui conviennent.
    pub fn find(&self, vid: u16, pid: u16, accept: impl Fn(&Info) -> bool) -> Vec<usize> {
        (0..self.colls.len()).filter(|&i| self.colls[i].vid == vid && self.colls[i].pid == pid && accept(&self.colls[i])).collect()
    }
}

pub fn clamp100(value: i64) -> u8 {
    value.clamp(0, 100) as u8
}

/// Convertit linéairement low..high en 0..100, borné.
pub fn map100(value: i64, low: i64, high: i64) -> u8 {
    if high == low {
        return 0;
    }
    clamp100(((value - low) as f64 * 100.0 / (high - low) as f64).round() as i64)
}

/// Interpolation dans des tables triées par tension décroissante.
pub fn interpolate(mv: i64, volts: &[i64], percents: &[i64]) -> u8 {
    let n = volts.len().min(percents.len());
    if n == 0 {
        return 0;
    }
    if mv >= volts[0] {
        return clamp100(percents[0]);
    }
    for i in 1..n {
        if mv >= volts[i] {
            let span = volts[i - 1] - volts[i];
            if span <= 0 {
                return clamp100(percents[i]);
            }
            let value = percents[i] as f64 + (percents[i - 1] - percents[i]) as f64 * (mv - volts[i]) as f64 / span as f64;
            return clamp100(value.round() as i64);
        }
    }
    clamp100(percents[n - 1])
}

/// Canal ouvert vers une collection, avec la convention de hidapi : l'identifiant 0 en tête est retiré à la lecture.
pub struct Dev(Stream);

impl Dev {
    pub fn open(info: &Info) -> Option<Dev> {
        Stream::open(info).map(Dev)
    }

    pub fn write(&self, data: &[u8]) -> bool {
        self.0.write(data)
    }

    pub fn read(&self, timeout_ms: u64) -> Option<Vec<u8>> {
        let mut data = self.0.read(timeout_ms.max(1) as u32)?;
        if data.first() == Some(&0) {
            data.remove(0);
        }
        (!data.is_empty()).then_some(data)
    }

    /// Jette les réponses anciennes (d'une autre application par exemple).
    pub fn flush(&self) {
        for _ in 0..64 {
            if self.0.read(1).is_none() {
                break;
            }
        }
    }

    /// Premier rapport accepté dans le délai.
    pub fn wait_for(&self, timeout_ms: u64, accept: impl Fn(&[u8]) -> bool) -> Option<Vec<u8>> {
        let end = Instant::now() + Duration::from_millis(timeout_ms);
        loop {
            let left = end.saturating_duration_since(Instant::now()).as_millis() as u64;
            if left == 0 {
                return None;
            }
            let report = self.read(left)?;
            if accept(&report) {
                return Some(report);
            }
        }
    }

    pub fn query(&self, request: &[u8], timeout_ms: u64, accept: impl Fn(&[u8]) -> bool) -> Option<Vec<u8>> {
        if !self.write(request) {
            return None;
        }
        self.wait_for(timeout_ms, accept)
    }
}

pub fn pause(ms: u64) {
    std::thread::sleep(Duration::from_millis(ms));
}

fn to_readings(statuses: Vec<Status>) -> Vec<Reading> {
    let mut readings: Vec<Reading> = Vec::new();
    for s in statuses {
        let (true, Some(percent)) = (s.connected, s.percent.filter(|&p| p <= 100)) else { continue };
        let name = crate::generic::clean_name(&s.name);
        if name.is_empty() || s.key.is_empty() {
            continue;
        }
        let mut id = format!("hid-{}", s.key);
        // deux appareils identiques : « #2 », « #3 »…
        let base = id.clone();
        let mut n = 2;
        while readings.iter().any(|r| r.id == id) {
            id = format!("{base}#{n}");
            n += 1;
        }
        readings.push(Reading { id, name, kind: s.kind, percent, charging: s.charging, estimated: false, pointer: Default::default() });
    }
    readings
}

/// Casque PRO X 2, lu par le module principal.
const PRO_X2: (u16, u16) = (0x046D, 0x0AF7);

fn protect(ctx: &mut Context, logitech_read: &[u16]) {
    ctx.claim(PRO_X2.0, PRO_X2.1);
    for &pid in logitech_read {
        ctx.claim(0x046D, pid);
    }
}

/// `logitech_read` : produits Logitech déjà lus par le module principal, à ne jamais interroger ici.
pub fn read_all(logitech_read: &[u16]) -> Vec<Reading> {
    // chaque pilote ne parle qu'aux identifiants exacts qu'il connaît
    let colls = hid::enumerate_all();
    if colls.is_empty() {
        return Vec::new();
    }
    let mut ctx = Context::new(colls);
    protect(&mut ctx, logitech_read);
    let drivers: [fn(&mut Context); 39] = [
        mice::poll_glorious,
        steelseries::poll,
        steelseries::poll_elite,
        razer::poll,
        razer::poll_barracuda,
        mice::poll_lamzu,
        playstation::poll,
        corsair::poll,
        hyperx::poll,
        astro::poll,
        headsets::poll_jabra,
        headsets::poll_jbl,
        mice::poll_cooler_master,
        headsets::poll_alienware,
        headsets::poll_audeze,
        rog::poll,
        logitech::poll_g435,
        logitech::poll,
        keychron::poll,
        compx::poll_atk,
        compx::poll_pulsar,
        compx::poll_kysona,
        gamepads::poll_eightbitdo,
        gamepads::poll_gamesir,
        keyboards::poll_aula,
        keyboards::poll_mchose,
        keyboards::poll_rongyuan,
        keyboards::poll_sonix,
        keyboards::poll_royal_kludge,
        misc::poll,
        misc::poll_rapoo,
        misc::poll_dareu,
        misc::poll_rawm,
        small_a::poll,
        small_b::poll,
        audio_pads::poll,
        atk::poll,
        external::poll,
        bluetooth::poll,
    ];
    for poll in drivers {
        if ctx.out_of_time() {
            break;
        }
        poll(&mut ctx);
    }
    to_readings(ctx.out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn devices_of_the_main_module_are_never_probed() {
        let mut ctx = Context::new(Vec::new());
        protect(&mut ctx, &[0xC539]);
        assert!(ctx.is_claimed(0x046D, 0xC539) && ctx.is_claimed(0x046D, 0x0AF7));
        assert!(!ctx.is_claimed(0x046D, 0x0ABA));
    }

    #[test]
    fn conversions() {
        assert_eq!(map100(2, 0, 4), 50);
        assert_eq!(map100(0x7F, 0x64, 0x9A), 50);
        assert_eq!(map100(5, 5, 5), 0);
        assert_eq!(interpolate(3900, &[4000, 3800], &[100, 0]), 50);
        assert_eq!(interpolate(5000, &[4000, 3800], &[100, 0]), 100);
        assert_eq!(interpolate(1, &[4000, 3800], &[100, 0]), 0);
    }

    #[test]
    fn unusable_statuses_are_dropped() {
        let mut off = Status::new("a", "A", Kind::Headset);
        off.percent = Some(50);
        off.connected = false;
        let unknown = Status::new("b", "B", Kind::Mouse);
        let mut ok = Status::new("c", "C", Kind::Mouse);
        ok.percent = Some(70);
        let readings = to_readings(vec![off, unknown, ok.clone(), ok]);
        assert_eq!(readings.iter().map(|r| r.id.as_str()).collect::<Vec<_>>(), ["hid-c", "hid-c#2"]);
    }
}
