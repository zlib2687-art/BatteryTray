//! Autonomie et temps de charge estimés. Portage de AutonomyEstimator, LevelTrend et AutonomyRatchet (C#).

use crate::devices::Reading;

const MAX_GAP_MINUTES: f64 = 12.0;
const PRIOR_WEIGHT: f64 = 6.0;
const MAX_LEARNED_OVER_RATED: f64 = 1.5;
pub const MAX_LOWER_BOUND_HOURS: f64 = 24.0;
const NOISE: u8 = 2;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Sample {
    pub t: u64,
    pub p: u8,
}

#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub enum Estimate {
    #[default]
    None,
    Pending,
    /// Heures restantes, mesurées.
    Left(f64),
    /// « plus de … »
    AtLeast(f64),
    /// D'après l'autonomie annoncée.
    Rated(f64),
    /// Heures avant charge complète.
    Full(f64),
}

#[derive(Default)]
struct Track {
    id: String,
    discharge: Vec<Sample>,
    charge: Vec<Sample>,
    rate: Option<f64>,
    charge_rate: Option<f64>,
    /// Dernière durée affichée (niveau, heures, instant) : elle ne remonte jamais en décharge.
    shown: Option<(u8, f64, u64)>,
}

#[derive(Default)]
pub struct Estimator {
    tracks: Vec<Track>,
}

/// Autonomie de référence des modèles connus, tant qu'aucune mesure n'existe.
pub fn rated_hours(name: &str) -> Option<f64> {
    let upper = name.to_uppercase();
    [("PRO X 2", 50.0), ("G502 LIGHTSPEED", 25.0), ("DEATHADDER V4 PRO", 150.0), ("DEATHADDER V3 PRO", 90.0), ("VIPER V3 PRO", 95.0), ("BLACKSHARK V2 PRO", 70.0)]
        .iter()
        .find(|(fragment, _)| upper.contains(fragment))
        .map(|&(_, hours)| hours)
}

fn hours_between(a: u64, b: u64) -> f64 {
    (b as f64 - a as f64) / 3600.0
}

/// Instants où le niveau a changé (placés au milieu entre deux lectures rapprochées) : (heure en s, niveau).
fn crossings(samples: &[Sample], falling: bool) -> Vec<(u64, f64)> {
    let mut out = Vec::new();
    for w in samples.windows(2) {
        let (a, b) = (w[0], w[1]);
        let minutes = hours_between(a.t, b.t) * 60.0;
        if minutes <= 0.0 || minutes > MAX_GAP_MINUTES {
            continue;
        }
        let t = a.t + (b.t - a.t) / 2;
        let steps = if falling { a.p.saturating_sub(b.p) } else { b.p.saturating_sub(a.p) };
        for step in 1..=steps {
            let level = if falling { a.p - step } else { a.p + step };
            out.push((t, f64::from(level)));
        }
    }
    out
}

/// Pente (points par heure) de la droite passant au mieux par les changements.
fn rate(points: &[(u64, f64)], min_points: usize, min_span: f64) -> Option<f64> {
    if points.len() < min_points.max(2) {
        return None;
    }
    let (first, last) = (points[0], points[points.len() - 1]);
    let span = hours_between(first.0, last.0);
    if span < min_span || span <= 0.0 || first.1 == last.1 {
        return None;
    }
    let n = points.len() as f64;
    let (mut sx, mut sy, mut sxx, mut sxy) = (0.0, 0.0, 0.0, 0.0);
    for &(t, y) in points {
        let x = hours_between(first.0, t);
        sx += x;
        sy += y;
        sxx += x * x;
        sxy += x * y;
    }
    let denominator = n * sxx - sx * sx;
    if denominator.abs() < 1e-12 {
        return None;
    }
    let slope = (n * sxy - sx * sy) / denominator;
    ((slope < 0.0) == (last.1 < first.1) && slope != 0.0).then_some(slope.abs())
}

fn level_now(last: (u64, f64), per_hour: f64, now: u64, falling: bool) -> f64 {
    let moved = per_hour * hours_between(last.0, now).max(0.0);
    let level = if falling { last.1 + 0.5 - moved } else { last.1 - 0.5 + moved };
    level.clamp(last.1 - 0.5, last.1 + 0.5)
}

fn observed_hours(samples: &[Sample]) -> f64 {
    samples
        .windows(2)
        .map(|w| hours_between(w[0].t, w[1].t))
        .filter(|&h| h > 0.0 && h * 60.0 <= MAX_GAP_MINUTES)
        .sum()
}

fn push(list: &mut Vec<Sample>, sample: Sample, keep_hours: f64) {
    if list.last().is_none_or(|l| sample.t.saturating_sub(l.t) >= 45) {
        list.push(sample);
    }
    list.retain(|s| hours_between(s.t, sample.t) <= keep_hours);
}

fn learn(slot: &mut Option<f64>, solid: f64) {
    *slot = Some(slot.map_or(solid, |old| 0.7 * old + 0.3 * solid));
}

impl Estimator {
    fn track(&mut self, id: &str) -> &mut Track {
        let index = match self.tracks.iter().position(|t| t.id == id) {
            Some(i) => i,
            None => {
                self.tracks.push(Track { id: id.to_string(), ..Default::default() });
                self.tracks.len() - 1
            }
        };
        &mut self.tracks[index]
    }

    /// Mesures de la période de décharge en cours, pour le graphique.
    pub fn samples(&self, id: &str) -> Vec<Sample> {
        self.tracks.iter().find(|t| t.id == id).map_or_else(Vec::new, |t| t.discharge.clone())
    }

    /// `rating` : autonomie annoncée (saisie ou connue) pour ce modèle.
    pub fn apply(&mut self, r: &Reading, rating: Option<f64>, now: u64) -> Estimate {
        let track = self.track(&r.id);
        let mut p = r.percent.min(100);
        let plugged = r.charging;
        if let Some(last) = track.discharge.last() {
            // oscillation de 1 à 2 points : pas une recharge
            if !plugged && p > last.p && p - last.p <= NOISE {
                p = last.p;
            }
        }
        if plugged || track.discharge.last().is_some_and(|l| p > l.p) {
            track.discharge.clear();
        }
        push(&mut track.discharge, Sample { t: now, p }, 48.0);

        if plugged {
            track.shown = None;
            return Self::charge(track, p, now);
        }
        track.charge.clear();

        let points = crossings(&track.discharge, true);
        let measured = rate(&points, 3, 0.5);
        let n = track.discharge.len();
        if n >= 2 && track.discharge[n - 1].p < track.discharge[n - 2].p {
            if let Some(solid) = rate(&points, 5, 1.5) {
                learn(&mut track.rate, solid);
            }
        }
        let per_hour = match (measured, track.rate, rating) {
            (Some(m), _, Some(full)) => {
                let count = points.len() as f64;
                Some((count * m + PRIOR_WEIGHT * (100.0 / full)) / (count + PRIOR_WEIGHT))
            }
            (Some(m), _, None) => Some(m),
            (None, Some(learned), Some(full)) => Some(learned.max(100.0 / (MAX_LEARNED_OVER_RATED * full))),
            (None, learned, _) => learned,
        };

        let estimate = if let Some(per_hour) = per_hour {
            let level = points.last().map_or(f64::from(p), |&c| level_now(c, per_hour, now, true));
            Estimate::Left(level / per_hour)
        } else if let Some(full) = rating {
            // pas encore de mesure : autonomie annoncée
            Estimate::Rated(f64::from(p) / 100.0 * full)
        } else {
            let hours = observed_hours(&track.discharge);
            if points.is_empty() && hours >= 0.25 {
                Estimate::AtLeast((f64::from(p) * hours).min(MAX_LOWER_BOUND_HOURS))
            } else {
                Estimate::Pending
            }
        };
        Self::ratchet(track, estimate, p, now)
    }

    fn charge(track: &mut Track, p: u8, now: u64) -> Estimate {
        if track.charge.last().is_some_and(|l| p < l.p) {
            track.charge.clear();
        }
        push(&mut track.charge, Sample { t: now, p }, 12.0);
        if p >= 100 {
            return Estimate::None;
        }
        let points = crossings(&track.charge, false);
        let measured = rate(&points, 3, 0.25);
        let n = track.charge.len();
        if n >= 2 && track.charge[n - 1].p > track.charge[n - 2].p {
            if let Some(solid) = rate(&points, 5, 0.5) {
                learn(&mut track.charge_rate, solid);
            }
        }
        match measured.or(track.charge_rate) {
            Some(per_hour) => {
                let level = points.last().map_or(f64::from(p), |&c| level_now(c, per_hour, now, false));
                Estimate::Full(((100.0 - level) / per_hour).max(0.0))
            }
            None => Estimate::Pending,
        }
    }

    /// La durée affichée ne remonte jamais tant que le niveau ne remonte pas.
    fn ratchet(track: &mut Track, estimate: Estimate, mut p: u8, now: u64) -> Estimate {
        let hours = match estimate {
            Estimate::Left(h) | Estimate::Rated(h) => h,
            other => return other,
        };
        let mut shown = hours;
        if let Some((before, previous, at)) = track.shown {
            let gap = hours_between(at, now);
            if p > before.saturating_add(NOISE) || !(0.0..=72.0).contains(&gap) {
                // recharge ou très longue absence : on repart de la nouvelle estimation
            } else if gap > 1.0 {
                if before > 0 {
                    shown = hours.min(previous * f64::from(p.min(before)) / f64::from(before));
                }
                p = p.min(before);
            } else {
                shown = hours.min((previous - gap).max(0.0));
                p = p.min(before);
            }
        }
        track.shown = Some((p, shown, now));
        match estimate {
            Estimate::Rated(_) => Estimate::Rated(shown),
            _ => Estimate::Left(shown),
        }
    }

    pub fn serialize(&self) -> String {
        let mut text = String::new();
        for t in &self.tracks {
            for (kind, list) in [("d", &t.discharge), ("c", &t.charge)] {
                for s in list {
                    text += &format!("{}\t{kind}\t{}\t{}\n", t.id, s.t, s.p);
                }
            }
            for (kind, value) in [("r", t.rate), ("cr", t.charge_rate)] {
                if let Some(v) = value {
                    text += &format!("{}\t{kind}\t{v}\n", t.id);
                }
            }
            if let Some((p, h, at)) = t.shown {
                text += &format!("{}\ta\t{p}\t{h}\t{at}\n", t.id);
            }
        }
        text
    }

    /// Les lignes abîmées sont ignorées.
    pub fn parse(text: &str) -> Estimator {
        let mut e = Estimator::default();
        for line in text.lines() {
            let f: Vec<&str> = line.split('\t').collect();
            if f.len() < 3 {
                continue;
            }
            let valid = |v: &str| v.parse::<f64>().ok().filter(|x| x.is_finite() && *x >= 0.0);
            let level = |v: &str| v.parse::<u8>().ok().filter(|&p| p <= 100);
            match (f[1], f.len()) {
                ("d" | "c", 4) => {
                    if let (Ok(t), Some(p)) = (f[2].parse(), level(f[3])) {
                        let track = e.track(f[0]);
                        let list = if f[1] == "d" { &mut track.discharge } else { &mut track.charge };
                        list.push(Sample { t, p });
                    }
                }
                ("r", 3) => e.track(f[0]).rate = valid(f[2]).filter(|&v| v > 0.0 && v <= 1000.0),
                ("cr", 3) => e.track(f[0]).charge_rate = valid(f[2]).filter(|&v| v > 0.0 && v <= 1000.0),
                ("a", 5) => {
                    if let (Some(p), Some(h), Ok(at)) = (level(f[2]), valid(f[3]), f[4].parse()) {
                        e.track(f[0]).shown = Some((p, h, at));
                    }
                }
                _ => {}
            }
        }
        e
    }
}

/// « 5 h 31 min », « 2 j 5 h », « 40 min ».
pub fn duration(hours: f64) -> String {
    let hours = if hours.is_nan() { 0.0 } else { hours.clamp(0.0, 24.0 * 999.0) };
    let total = ((hours * 60.0).round() as u64).max(1);
    let (d, h, m) = (total / 1440, total % 1440 / 60, total % 60);
    match (d, h, m) {
        (0, 0, m) => format!("{m} min"),
        (0, h, 0) => format!("{h} h"),
        (0, h, m) => format!("{h} h {m} min"),
        (d, 0, _) => format!("{d} j"),
        (d, h, _) => format!("{d} j {h} h"),
    }
}

pub fn describe(estimate: Estimate) -> Option<String> {
    Some(match estimate {
        Estimate::None => return None,
        Estimate::Pending => "calcul en cours…".into(),
        Estimate::Left(h) if h > 720.0 => "reste plus de 30 j".into(),
        Estimate::Left(h) => format!("reste ≈ {}", duration(h)),
        Estimate::AtLeast(h) => format!("reste plus de {}", duration(h)),
        Estimate::Rated(h) => format!("≈ {} (annoncée)", duration(h)),
        Estimate::Full(h) => format!("plein dans ≈ {}", duration(h)),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::devices::Kind;

    fn reading(percent: u8, charging: bool) -> Reading {
        Reading { id: "a".into(), name: "A".into(), kind: Kind::Mouse, percent, charging, estimated: false }
    }

    #[test]
    fn steady_discharge_is_measured_and_never_rises() {
        let mut e = Estimator::default();
        let mut previous = f64::MAX;
        let mut measured = false;
        for step in 0..120u64 {
            // un point toutes les 20 min : 3 points par heure
            let level = 80 - (step / 10) as u8;
            if let Estimate::Left(h) = e.apply(&reading(level, false), None, step * 120) {
                measured = true;
                assert!(h <= previous + 1e-9, "la durée remonte : {previous} -> {h}");
                previous = h;
            }
        }
        assert!(measured);
        assert!((previous - 23.0).abs() < 2.0, "{previous}");
    }

    #[test]
    fn rating_used_before_any_measure() {
        let mut e = Estimator::default();
        assert_eq!(e.apply(&reading(50, false), Some(50.0), 0), Estimate::Rated(25.0));
        assert_eq!(rated_hours("Razer DeathAdder V4 Pro"), Some(150.0));
    }

    #[test]
    fn charge_time() {
        let mut e = Estimator::default();
        let mut last = Estimate::None;
        for step in 0..40u64 {
            last = e.apply(&reading(50 + (step / 4) as u8, true), None, step * 120);
        }
        assert!(matches!(last, Estimate::Full(h) if h > 4.0 && h < 7.0), "{last:?}");
    }

    #[test]
    fn saved_and_reloaded() {
        let mut e = Estimator::default();
        e.apply(&reading(50, false), Some(50.0), 100);
        let back = Estimator::parse(&e.serialize());
        assert_eq!(back.samples("a"), e.samples("a"));
        assert_eq!(back.serialize(), e.serialize());
        assert_eq!(Estimator::parse("abîmé\na\td\tx\ty\na\tr\tNaN").samples("a"), vec![]);
    }

    #[test]
    fn durations() {
        assert_eq!(duration(5.0 + 31.0 / 60.0), "5 h 31 min");
        assert_eq!(duration(53.0), "2 j 5 h");
        assert_eq!(duration(f64::NAN), "1 min");
    }
}
