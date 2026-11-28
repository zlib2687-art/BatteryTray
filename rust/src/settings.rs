//! Réglages de l'utilisateur, un par ligne dans %AppData%\BatteryTrayRust\reglages.tsv.

pub const POLL_CHOICES: [u32; 4] = [1, 2, 5, 10];
pub const SCALE_CHOICES: [u32; 4] = [85, 100, 125, 150];

#[derive(Clone, Debug, PartialEq, Default)]
pub struct Pref {
    pub id: String,
    pub hidden: bool,
    /// Vide = nom d'origine.
    pub name: String,
    /// 0 = seuil par défaut.
    pub low: u8,
    /// 0 = autonomie connue ou mesurée.
    pub rated: u32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Settings {
    /// Code de langue ; vide = langue de Windows.
    pub language: String,
    pub poll_minutes: u32,
    pub scale: u32,
    pub brand_colors: bool,
    pub orange_below: u8,
    pub red_below: u8,
    pub notify_low: bool,
    pub notify_full: bool,
    pub low_threshold: u8,
    /// Dans l'ordre d'affichage.
    pub devices: Vec<Pref>,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            language: String::new(),
            poll_minutes: 2,
            scale: 100,
            brand_colors: true,
            orange_below: 40,
            red_below: 15,
            notify_low: true,
            notify_full: true,
            low_threshold: 20,
            devices: Vec::new(),
        }
    }
}

impl Settings {
    pub fn pref(&self, id: &str) -> Pref {
        self.devices.iter().find(|d| d.id == id).cloned().unwrap_or_else(|| Pref { id: id.into(), ..Default::default() })
    }

    /// Ajoute les appareils nouveaux à la fin de l'ordre. Vrai si la liste a changé.
    pub fn remember<'a>(&mut self, ids: impl Iterator<Item = &'a str>) -> bool {
        let mut changed = false;
        for id in ids {
            if !self.devices.iter().any(|d| d.id == id) {
                self.devices.push(Pref { id: id.into(), ..Default::default() });
                changed = true;
            }
        }
        changed
    }

    /// Ramène toute valeur absurde dans ses bornes.
    pub fn normalize(&mut self) {
        if !crate::strings::LANGUAGES.iter().any(|l| l.0 == self.language) {
            self.language.clear();
        }
        self.poll_minutes = self.poll_minutes.clamp(1, 60);
        self.scale = self.scale.clamp(70, 200);
        self.orange_below = self.orange_below.clamp(2, 100);
        self.red_below = self.red_below.clamp(1, self.orange_below - 1);
        self.low_threshold = self.low_threshold.clamp(1, 99);
        for d in &mut self.devices {
            d.low = d.low.min(99);
            d.rated = d.rated.min(9999);
            d.name = d.name.chars().filter(|c| !c.is_control()).take(40).collect();
        }
    }

    pub fn serialize(&self) -> String {
        let mut text = format!(
            "poll\t{}\nscale\t{}\nbrand\t{}\norange\t{}\nred\t{}\nnotify_low\t{}\nnotify_full\t{}\nlow\t{}\n",
            self.poll_minutes,
            self.scale,
            self.brand_colors as u8,
            self.orange_below,
            self.red_below,
            self.notify_low as u8,
            self.notify_full as u8,
            self.low_threshold
        );
        text += &format!("lang\t{}\n", self.language);
        for d in &self.devices {
            let name: String = d.name.chars().filter(|c| !c.is_control()).collect();
            text += &format!("device\t{}\t{}\t{}\t{}\t{}\n", d.id, d.hidden as u8, name, d.low, d.rated);
        }
        text
    }

    pub fn parse(text: &str) -> Settings {
        let mut s = Settings::default();
        for line in text.lines() {
            let f: Vec<&str> = line.split('\t').collect();
            if f.len() == 6 && f[0] == "device" {
                s.devices.push(Pref {
                    id: f[1].into(),
                    hidden: f[2] == "1",
                    name: f[3].into(),
                    low: f[4].parse().unwrap_or(0),
                    rated: f[5].parse().unwrap_or(0),
                });
                continue;
            }
            let Some((key, value)) = line.split_once('\t') else { continue };
            if key == "lang" {
                s.language = value.into();
                continue;
            }
            let Ok(n) = value.parse::<u32>() else { continue };
            let small = n.min(255) as u8;
            match key {
                "poll" => s.poll_minutes = n,
                "scale" => s.scale = n,
                "brand" => s.brand_colors = n == 1,
                "orange" => s.orange_below = small,
                "red" => s.red_below = small,
                "notify_low" => s.notify_low = n == 1,
                "notify_full" => s.notify_full = n == 1,
                "low" => s.low_threshold = small,
                _ => {}
            }
        }
        s.normalize();
        s
    }
}

pub fn write_atomic(file: &str, text: &str) {
    let dir = crate::state::data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let temp = dir.join(format!("{file}.tmp"));
    if std::fs::write(&temp, text).is_ok() {
        let _ = std::fs::rename(temp, dir.join(file));
    }
}

pub fn read(file: &str) -> String {
    std::fs::read_to_string(crate::state::data_dir().join(file)).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip() {
        let mut s = Settings { scale: 125, brand_colors: false, language: "ar-TN".into(), ..Default::default() };
        s.devices.push(Pref { id: "a".into(), hidden: true, name: "Ma souris".into(), low: 30, rated: 80 });
        assert_eq!(Settings::parse(&s.serialize()), s);
    }

    #[test]
    fn absurd_values_are_repaired() {
        let s = Settings::parse("poll\t100000\norange\t0\nred\t90\nlow\t0\nn'importe quoi");
        assert_eq!((s.poll_minutes, s.orange_below, s.red_below, s.low_threshold), (60, 2, 1, 1));
    }
}
