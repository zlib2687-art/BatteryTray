//! Réglages en cours d'une souris : sensibilité (DPI) et fréquence d'interrogation. Lecture seule.
//! Commandes Razer : rétro-ingénierie du projet OpenRazer ; fonctions HID++ 0x2201, 0x2202, 0x8060 et 0x8061.

use crate::devices::razer_command;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Pointer {
    /// (horizontal, vertical)
    pub dpi: Option<(u16, u16)>,
    /// Fréquence d'interrogation en Hz.
    pub rate: Option<u16>,
}

impl Pointer {
    /// « 800 DPI · 1000 Hz » ; vide si rien n'est connu.
    pub fn text(&self) -> String {
        let dpi = self.dpi.map(|(x, y)| if x == y { format!("{x} DPI") } else { format!("{x} × {y} DPI") });
        let rate = self.rate.map(|hz| format!("{hz} Hz"));
        [dpi, rate].into_iter().flatten().collect::<Vec<_>>().join(" · ")
    }
}

fn dpi(x: u16, y: u16) -> Option<(u16, u16)> {
    let y = if y == 0 { x } else { y };
    (x >= 50).then_some((x, y))
}

// ---------------------------------------------------------------- HID++ (Logitech)

/// Fonction 0x2201 « getSensorDpi » : capteur, valeur courante, valeur par défaut.
pub fn hidpp_dpi(answer: &[u8]) -> Option<(u16, u16)> {
    let value = u16::from_be_bytes([*answer.get(5)?, *answer.get(6)?]);
    let value = if value == 0 { u16::from_be_bytes([*answer.get(7)?, *answer.get(8)?]) } else { value };
    dpi(value, value)
}

/// Fonction 0x2202 « getSensorDpiParameters » : capteur, X, X par défaut, Y, Y par défaut.
pub fn hidpp_dpi_extended(answer: &[u8]) -> Option<(u16, u16)> {
    let word = |at: usize| Some(u16::from_be_bytes([*answer.get(at)?, *answer.get(at + 1)?]));
    dpi(word(5)?, word(9)?)
}

/// Fonction 0x8060 : intervalle en millisecondes.
pub fn hidpp_rate(answer: &[u8]) -> Option<u16> {
    let ms = u16::from(*answer.get(4)?);
    (1..=8).contains(&ms).then(|| 1000 / ms)
}

/// Fonction 0x8061 : rang dans 125, 250, 500… 8000 Hz.
pub fn hidpp_rate_extended(answer: &[u8]) -> Option<u16> {
    let index = *answer.get(4)?;
    (index <= 6).then(|| 125u16 << index)
}

// ---------------------------------------------------------------- Razer

/// Réponse réussie à la commande demandée : rend ses arguments.
fn razer_arguments(answer: &[u8], class: u8, command: u8) -> Option<&[u8]> {
    (answer.len() >= 16 && answer[1] == 0x02 && answer[7] == class && answer[8] == command).then(|| &answer[9..])
}

pub fn razer_dpi(answer: &[u8]) -> Option<(u16, u16)> {
    let a = razer_arguments(answer, 0x04, 0x85)?;
    dpi(u16::from_be_bytes([a[1], a[2]]), u16::from_be_bytes([a[3], a[4]]))
}

/// Commande récente (jusqu'à 8000 Hz).
pub fn razer_rate_extended(answer: &[u8]) -> Option<u16> {
    match razer_arguments(answer, 0x00, 0xC0)?[1] {
        0x01 => Some(8000),
        0x02 => Some(4000),
        0x04 => Some(2000),
        0x08 => Some(1000),
        0x10 => Some(500),
        0x40 => Some(125),
        _ => None,
    }
}

/// Commande ancienne (jusqu'à 1000 Hz).
pub fn razer_rate(answer: &[u8]) -> Option<u16> {
    match razer_arguments(answer, 0x00, 0x85)?[0] {
        0x01 => Some(1000),
        0x02 => Some(500),
        0x08 => Some(125),
        _ => None,
    }
}

/// Lit les réglages d'une souris Razer. `rate` : `None` pour ne pas lire la fréquence, sinon la commande à
/// utiliser (vrai : la récente).
pub fn razer_live(len: usize, transaction: u8, rate: Option<bool>, exchange: impl Fn(&[u8]) -> Option<Vec<u8>>) -> Pointer {
    let ask = |class, command, size| {
        let request = razer_command(len, transaction, class, command, size, &[0]);
        // 0x01 : appareil occupé, un second essai suffit
        exchange(&request).filter(|a| a.get(1) != Some(&0x01)).or_else(|| exchange(&request))
    };
    Pointer {
        dpi: ask(0x04, 0x85, 7).and_then(|a| razer_dpi(&a)),
        rate: rate.and_then(|extended| {
            if extended {
                ask(0x00, 0xC0, 1).and_then(|a| razer_rate_extended(&a))
            } else {
                ask(0x00, 0x85, 1).and_then(|a| razer_rate(&a))
            }
        }),
    }
}

/// Première lecture : choisit aussi la commande de fréquence que la souris comprend.
pub fn razer(len: usize, transaction: u8, exchange: impl Fn(&[u8]) -> Option<Vec<u8>>) -> (Pointer, bool) {
    let recent = razer_live(len, transaction, Some(true), &exchange);
    if recent.rate.is_some() {
        return (recent, true);
    }
    (razer_live(len, transaction, Some(false), &exchange), false)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn answer(class: u8, command: u8, arguments: &[u8]) -> Vec<u8> {
        let mut a = vec![0; 91];
        (a[1], a[7], a[8]) = (0x02, class, command);
        a[9..9 + arguments.len()].copy_from_slice(arguments);
        a
    }

    /// Diagnostic sans fenêtre : note ce que les deux souris envoient d'elles-mêmes (BT_JOURNAL = fichier,
    /// BT_SECONDES = durée). Lecture seule, rien n'est envoyé.
    #[test]
    #[ignore]
    fn listen_to_mice() {
        use std::io::Write;
        use std::sync::mpsc;
        use std::time::{Duration, Instant};
        let path = std::env::var("BT_JOURNAL").expect("BT_JOURNAL");
        let seconds = std::env::var("BT_SECONDES").ok().and_then(|s| s.parse().ok()).unwrap_or(120);
        let mut log = std::fs::File::create(path).unwrap();
        let (send, lines) = mpsc::channel::<String>();
        let start = Instant::now();
        let end = start + Duration::from_secs(seconds);
        for info in crate::hid::enumerate(&[0x1532, 0x046D]) {
            if info.pid != 0x00BF && info.pid != 0xC539 {
                continue;
            }
            let label = format!("{:04X}:{:04X} page {:04X} usage {:X} mi {} in {}", info.vid, info.pid, info.usage_page, info.usage, info.iface, info.input_len);
            let Some(stream) = crate::hid::Stream::open(&info) else {
                writeln!(log, "fermé  {label}").unwrap();
                continue;
            };
            writeln!(log, "écoute {label}").unwrap();
            let send = send.clone();
            // les poignées Windows peuvent changer de fil ; chaque flux n'appartient qu'à un seul
            struct Owned(crate::hid::Stream);
            unsafe impl Send for Owned {}
            let owned = Owned(stream);
            std::thread::spawn(move || {
                let owned = owned;
                while Instant::now() < end {
                    if let Some(r) = owned.0.read(1000) {
                        let used = r.iter().rposition(|&b| b != 0).map_or(1, |i| i + 1).min(24);
                        let hex: Vec<String> = r[..used].iter().map(|b| format!("{b:02X}")).collect();
                        let _ = send.send(format!("{:7.1} s  {label} : {}", start.elapsed().as_secs_f32(), hex.join(" ")));
                    }
                }
            });
        }
        drop(send);
        for (count, line) in lines.iter().enumerate() {
            if count < 2000 {
                writeln!(log, "{line}").unwrap();
                log.flush().unwrap();
            }
        }
        writeln!(log, "fin").unwrap();
    }

    /// Essai réel sans fenêtre : suit les réglages des souris (BT_SECONDES = durée).
    #[test]
    #[ignore]
    fn follow_real_mice() {
        use std::time::{Duration, Instant};
        let seconds = std::env::var("BT_SECONDES").ok().and_then(|s| s.parse().ok()).unwrap_or(5);
        let mut reader = crate::devices::Reader::default();
        for r in reader.read_all() {
            println!("départ  {}  {}", r.id, r.pointer.text());
        }
        let start = Instant::now();
        let (mut polls, mut busy) = (0u32, Duration::ZERO);
        while start.elapsed() < Duration::from_secs(seconds) {
            let before = Instant::now();
            polls += 1;
            for (id, pointer) in reader.poll_pointers(polls % 4 == 0) {
                println!("{:6.1} s  {id}  {}", start.elapsed().as_secs_f32(), pointer.text());
            }
            busy += before.elapsed();
            std::thread::sleep(Duration::from_millis(300));
        }
        println!("{polls} relectures, {} ms en moyenne", busy.as_millis() / u128::from(polls.max(1)));
    }

    #[test]
    fn razer_answers() {
        assert_eq!(razer_dpi(&answer(0x04, 0x85, &[0, 0x06, 0x40, 0x06, 0x40])), Some((1600, 1600)));
        assert_eq!(razer_dpi(&answer(0x00, 0x85, &[0, 6, 64, 6, 64])), None);
        assert_eq!(razer_rate_extended(&answer(0x00, 0xC0, &[0, 0x02])), Some(4000));
        assert_eq!(razer_rate(&answer(0x00, 0x85, &[0x02])), Some(500));
        let (mouse, extended) = razer(91, 0x1F, |request| {
            Some(match (request[7], request[8]) {
                (0x04, 0x85) => answer(0x04, 0x85, &[0, 0x03, 0x20, 0x03, 0x84]),
                _ => answer(0x00, 0xC0, &[0, 0x08]),
            })
        });
        assert_eq!(mouse.text(), "800 × 900 DPI · 1000 Hz");
        assert!(extended);
    }

    #[test]
    fn hidpp_answers() {
        assert_eq!(hidpp_dpi(&[0x11, 1, 5, 0x2A, 0, 0x09, 0x60, 0x03, 0x20]), Some((2400, 2400)));
        assert_eq!(hidpp_dpi(&[0x11, 1, 5, 0x2A, 0, 0, 0, 0x03, 0x20]), Some((800, 800)));
        assert_eq!(hidpp_dpi_extended(&[0x11, 1, 5, 0x5A, 0, 3, 32, 3, 32, 6, 64, 6, 64]), Some((800, 1600)));
        assert_eq!(hidpp_rate(&[0x11, 1, 5, 0x1A, 1]), Some(1000));
        assert_eq!(hidpp_rate(&[0x11, 1, 5, 0x1A, 0]), None);
        assert_eq!(hidpp_rate_extended(&[0x11, 1, 5, 0x2A, 6]), Some(8000));
        assert_eq!(Pointer::default().text(), "");
    }
}
