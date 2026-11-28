//! Langue courante et textes de l'interface.

use crate::strings::{KEYS, LANGUAGES, TEXTS};
use std::sync::atomic::{AtomicUsize, Ordering};

static CURRENT: AtomicUsize = AtomicUsize::new(0);

#[link(name = "kernel32")]
extern "system" {
    fn GetUserDefaultUILanguage() -> u16;
}

/// Langue prise en charge pour un identifiant de langue Windows (le français sinon).
pub fn from_lang_id(id: u16) -> &'static str {
    let (primary, sub) = (id & 0x3FF, (id >> 10) & 0x3F);
    match primary {
        0x09 => "en",
        0x0A => "es",
        0x19 => "ru",
        0x01 => "ar-TN",
        0x07 => "de",
        0x29 => "fa",
        0x0D => "he",
        0x11 => "ja",
        0x12 => "ko",
        0x0E => "hu",
        0x15 => "pl",
        0x1D => "sv",
        0x2A => "vi",
        0x1F => "tr",
        0x22 => "uk",
        0x16 if sub == 1 => "pt-BR",
        0x16 => "pt",
        0x04 if sub == 2 || sub == 4 => "zh",
        0x04 => "zh-TW",
        _ => "fr",
    }
}

fn index_of(code: &str) -> usize {
    LANGUAGES.iter().position(|l| l.0 == code).unwrap_or(0)
}

/// `code` vide = langue de Windows.
pub fn set(code: &str) {
    let code = if code.is_empty() { from_lang_id(unsafe { GetUserDefaultUILanguage() }) } else { code };
    CURRENT.store(index_of(code), Ordering::Relaxed);
}

fn code() -> &'static str {
    LANGUAGES[CURRENT.load(Ordering::Relaxed)].0
}

/// Arabe, persan et hébreu s'écrivent de droite à gauche.
pub fn is_rtl() -> bool {
    matches!(code(), "ar-TN" | "fa" | "he")
}

pub fn font() -> &'static str {
    match code() {
        "zh" => "Microsoft YaHei UI",
        "zh-TW" => "Microsoft JhengHei UI",
        "ja" => "Yu Gothic UI",
        "ko" => "Malgun Gothic",
        _ => "Segoe UI",
    }
}

fn lookup(language: usize, key: &str) -> Option<&'static str> {
    KEYS.iter().position(|&k| k == key).map(|i| TEXTS[language][i])
}

pub fn t(key: &'static str) -> &'static str {
    lookup(CURRENT.load(Ordering::Relaxed), key).unwrap_or(key)
}

/// Remplace {0}, {1}… par les valeurs, en une seule passe (un nom contenant « {1} » reste intact).
pub fn format(pattern: &str, values: &[&str]) -> String {
    let mut out = String::new();
    let mut rest = pattern;
    while let Some(at) = rest.find('{') {
        out.push_str(&rest[..at]);
        let tail = &rest[at..];
        let value = tail.get(1..3).filter(|s| s.ends_with('}')).and_then(|s| s[..1].parse::<usize>().ok()).and_then(|i| values.get(i));
        match value {
            Some(v) => {
                out.push_str(v);
                rest = &tail[3..];
            }
            None => {
                out.push('{');
                rest = &tail[1..];
            }
        }
    }
    out.push_str(rest);
    out
}

pub fn tf(key: &'static str, values: &[&str]) -> String {
    format(t(key), values)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn placeholders(s: &str) -> Vec<&str> {
        ["{0}", "{1}"].into_iter().filter(|p| s.contains(p)).collect()
    }

    #[test]
    fn every_language_has_every_text_with_the_same_parameters() {
        for (language, texts) in TEXTS.iter().enumerate() {
            for (i, text) in texts.iter().enumerate() {
                assert!(!text.is_empty(), "{} {}", LANGUAGES[language].0, KEYS[i]);
                assert_eq!(placeholders(text), placeholders(TEXTS[0][i]), "{} {}", LANGUAGES[language].0, KEYS[i]);
            }
        }
    }

    #[test]
    fn windows_languages() {
        assert_eq!(from_lang_id(0x040C), "fr");
        assert_eq!(from_lang_id(0x0409), "en");
        assert_eq!(from_lang_id(0x0416), "pt-BR");
        assert_eq!(from_lang_id(0x0816), "pt");
        assert_eq!(from_lang_id(0x0804), "zh");
        assert_eq!(from_lang_id(0x0404), "zh-TW");
        assert_eq!(from_lang_id(0x1C01), "ar-TN");
    }

    #[test]
    fn formatting() {
        assert_eq!(format("{0} : {1} %", &["{1}", "5"]), "{1} : 5 %");
        assert_eq!(format("a { b }", &["x"]), "a { b }");
    }
}
