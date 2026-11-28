//! Dernières valeurs connues et notifications déjà envoyées.

use crate::autonomy::Estimate;
use crate::devices::{Kind, Reading};
use crate::settings::Settings;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

const HYSTERESIS: u8 = 5;
const KEEP_SECONDS: u64 = 7 * 24 * 3600;

#[derive(Clone, Debug)]
pub struct Device {
    pub reading: Reading,
    pub seen: u64,
    pub online: bool,
    pub estimate: Estimate,
    low_notified: bool,
    full_notified: bool,
}

#[derive(Debug, PartialEq)]
pub enum Alert {
    /// (identifiant, nom affiché, niveau)
    Low(String, String, u8),
    Full(String, String),
}

#[derive(Default)]
pub struct State {
    pub devices: Vec<Device>,
}

pub fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs())
}

pub fn data_dir() -> PathBuf {
    PathBuf::from(std::env::var_os("APPDATA").unwrap_or_default()).join("BatteryTrayRust")
}

impl State {
    /// Intègre une lecture complète et renvoie les notifications à afficher (une par passage de seuil).
    pub fn apply(&mut self, readings: Vec<Reading>, at: u64, settings: &Settings) -> Vec<Alert> {
        let mut alerts = Vec::new();
        for d in &mut self.devices {
            d.online = false;
        }
        for r in readings {
            let index = match self.devices.iter().position(|d| d.reading.id == r.id) {
                Some(i) => i,
                None => {
                    self.devices.push(Device { reading: r.clone(), seen: at, online: true, estimate: Estimate::None, low_notified: false, full_notified: false });
                    self.devices.len() - 1
                }
            };
            let d = &mut self.devices[index];
            d.reading = r;
            d.seen = at;
            d.online = true;
            let (p, plugged) = (d.reading.percent, d.reading.charging);
            let pref = settings.pref(&d.reading.id);
            let threshold = if pref.low > 0 { pref.low } else { settings.low_threshold };
            let name = if pref.name.trim().is_empty() { d.reading.name.clone() } else { pref.name.trim().to_string() };

            if !plugged && p <= threshold {
                if !d.low_notified {
                    d.low_notified = true;
                    if settings.notify_low && !pref.hidden {
                        alerts.push(Alert::Low(d.reading.id.clone(), name.clone(), p));
                    }
                }
            } else if plugged || p > threshold.saturating_add(HYSTERESIS) {
                d.low_notified = false;
            }
            if plugged && p >= 100 {
                if !d.full_notified {
                    d.full_notified = true;
                    if settings.notify_full && !pref.hidden {
                        alerts.push(Alert::Full(d.reading.id.clone(), name));
                    }
                }
            } else if !plugged || p < 95 {
                d.full_notified = false;
            }
        }
        self.devices.retain(|d| d.online || at.saturating_sub(d.seen) < KEEP_SECONDS);
        alerts
    }

    pub fn load() -> State {
        let text = std::fs::read_to_string(data_dir().join("etat.tsv")).unwrap_or_default();
        State { devices: text.lines().filter_map(parse_line).collect() }
    }

    pub fn save(&self) {
        let mut text = String::new();
        for d in &self.devices {
            let r = &d.reading;
            let kind = if r.kind == Kind::Headset { "casque" } else { "souris" };
            let name = r.name.replace(['\t', '\r', '\n'], " ");
            text += &format!(
                "{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\n",
                r.id, name, kind, r.percent, r.estimated as u8, d.seen, d.low_notified as u8, d.full_notified as u8
            );
        }
        let dir = data_dir();
        let _ = std::fs::create_dir_all(&dir);
        // écriture « tout ou rien »
        let temp = dir.join("etat.tmp");
        if std::fs::write(&temp, text).is_ok() {
            let _ = std::fs::rename(temp, dir.join("etat.tsv"));
        }
    }
}

fn parse_line(line: &str) -> Option<Device> {
    let f: Vec<&str> = line.split('\t').collect();
    if f.len() != 8 {
        return None;
    }
    let reading = Reading {
        id: f[0].into(),
        name: f[1].into(),
        kind: if f[2] == "casque" { Kind::Headset } else { Kind::Mouse },
        percent: f[3].parse().ok().filter(|&p| p <= 100)?,
        charging: false,
        estimated: f[4] == "1",
    };
    Some(Device { reading, seen: f[5].parse().ok()?, online: false, estimate: Estimate::None, low_notified: f[6] == "1", full_notified: f[7] == "1" })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reading(percent: u8, charging: bool) -> Reading {
        Reading { id: "a".into(), name: "A".into(), kind: Kind::Mouse, percent, charging, estimated: false }
    }

    #[test]
    fn low_alert_once_then_rearmed() {
        let mut s = State::default();
        assert_eq!(s.apply(vec![reading(20, false)], 1, &Settings::default()), vec![Alert::Low("a".into(), "A".into(), 20)]);
        assert!(s.apply(vec![reading(19, false)], 2, &Settings::default()).is_empty());
        assert!(s.apply(vec![reading(23, false)], 3, &Settings::default()).is_empty());
        assert!(s.apply(vec![reading(18, false)], 4, &Settings::default()).is_empty());
        assert!(s.apply(vec![reading(30, false)], 5, &Settings::default()).is_empty());
        assert_eq!(s.apply(vec![reading(18, false)], 6, &Settings::default()).len(), 1);
    }

    #[test]
    fn full_alert_once() {
        let mut s = State::default();
        assert_eq!(s.apply(vec![reading(100, true)], 1, &Settings::default()), vec![Alert::Full("a".into(), "A".into())]);
        assert!(s.apply(vec![reading(100, true)], 2, &Settings::default()).is_empty());
    }

    #[test]
    fn offline_kept_seven_days() {
        let mut s = State::default();
        s.apply(vec![reading(50, false)], 0, &Settings::default());
        s.apply(vec![], 10, &Settings::default());
        assert!(!s.devices[0].online);
        s.apply(vec![], KEEP_SECONDS + 1, &Settings::default());
        assert!(s.devices.is_empty());
    }

    #[test]
    fn damaged_lines_ignored() {
        assert!(parse_line("n'importe quoi").is_none());
        assert!(parse_line("a\tA\tsouris\t250\t0\t1\t0\t0").is_none());
    }
}
