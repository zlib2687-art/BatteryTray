//! Sources externes facultatives : HeadsetControl (programme posé à côté de l'application) et Mobile connecté
//! de Microsoft (fichier local en lecture seule). Dérivé de BatteryHub (GPL-3.0).

use super::json::{self, Json};
use super::{clamp100, Context, Status};
use crate::devices::Kind;
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, SystemTime};

// ---------------------------------------------------------------- HeadsetControl

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

fn hex(value: Option<&str>) -> Option<u16> {
    let text = value?;
    u16::from_str_radix(text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")).unwrap_or(text), 16).ok()
}

/// (fabricant, produit, état) de chaque casque annoncé.
pub fn headset_control(output: &str) -> Vec<(u16, u16, Status)> {
    let Some(root) = output.find('{').and_then(|at| json::parse(output[at..].trim_end())) else { return Vec::new() };
    let Some(Json::Arr(devices)) = root.get("devices") else { return Vec::new() };
    devices
        .iter()
        .filter_map(|d| {
            let (vid, pid) = (hex(d.text("id_vendor"))?, hex(d.text("id_product"))?);
            let battery = d.get("battery").filter(|b| matches!(b, Json::Obj(_)))?;
            let state = battery.text("status").unwrap_or("");
            let name = d.text("device").filter(|n| !n.is_empty()).unwrap_or("Headset");
            let mut s = Status::new(format!("hsc:{vid:04X}:{pid:04X}"), name, Kind::Headset);
            s.charging = state == "BATTERY_CHARGING";
            s.connected = s.charging || state == "BATTERY_AVAILABLE";
            if let Some(&Json::Num(level)) = battery.get("level") {
                if s.connected && level >= 0.0 {
                    s.percent = Some(clamp100(level as i64));
                }
            }
            Some((vid, pid, s))
        })
        .collect()
}

/// Lance le programme sans fenêtre, avec une attente bornée.
fn run(exe: &std::path::Path) -> Option<String> {
    let mut child = Command::new(exe)
        .args(["-b", "-o", "json", "--timeout", "1500"])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .ok()?;
    let stdout = child.stdout.take()?;
    let (send, received) = mpsc::channel();
    std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stdout.take(1 << 20).read_to_end(&mut bytes);
        let _ = send.send(bytes);
    });
    let output = received.recv_timeout(Duration::from_secs(10)).ok();
    let _ = child.kill();
    let _ = child.wait();
    output.map(|b| String::from_utf8_lossy(&b).into_owned())
}

fn poll_headset_control(ctx: &mut Context) {
    // seulement s'il est posé à côté de l'application : jamais cherché ailleurs
    let Some(exe) = std::env::current_exe().ok().and_then(|p| Some(p.parent()?.join("headsetcontrol.exe"))) else { return };
    if !exe.is_file() {
        return;
    }
    for (vid, pid, status) in run(&exe).map(|o| headset_control(&o)).unwrap_or_default() {
        if !ctx.is_claimed(vid, pid) {
            ctx.add(status, vid, pid);
        }
    }
}

// ---------------------------------------------------------------- Mobile connecté (Phone Link)

const MAX_BYTES: u64 = 4 * 1024 * 1024;
const STALE: Duration = Duration::from_secs(3 * 60 * 60);
const JAPANESE_SUFFIX: &str = " - PC に接続しました";
const JAPANESE_TOOLTIP: &str = "バッテリー 残量 ";

/// (niveau, en charge) d'après l'infobulle de la carte, dans la langue attendue.
pub fn tooltip_battery(tooltip: &str, japanese: bool) -> Option<(u8, bool)> {
    let rest = tooltip.strip_prefix(if japanese { JAPANESE_TOOLTIP } else { "Battery " })?;
    let digits = rest.bytes().take(3).take_while(u8::is_ascii_digit).count();
    let level: u16 = rest.get(..digits)?.parse().ok()?;
    let remainder = rest[digits..].strip_prefix('%')?;
    let charging = match (japanese, remainder) {
        (false, " remaining") | (true, "") => false, // charge non établie pour les cartes japonaises
        (false, " remaining, charging") => true,
        _ => return None,
    };
    (level <= 100).then_some((level as u8, charging))
}

/// Parcourt la carte ; `None` dans le vecteur si elle ne dit rien.
fn walk(value: &Json, japanese: bool, found: &mut Vec<(u8, bool)>) {
    if let Json::Arr(items) = value {
        items.iter().for_each(|v| walk(v, japanese, found));
        return;
    }
    let kinds = ["AdaptiveCard", "Container", "ColumnSet", "Column", "TextBlock", "Image"];
    if !matches!(value, Json::Obj(_)) || value.text("type").is_some_and(|t| !kinds.contains(&t)) {
        return;
    }
    if let Some(reading) = value.text("tooltip").and_then(|t| tooltip_battery(t, japanese)) {
        if value.text("text").is_none_or(|t| t == format!("{}%", reading.0)) {
            found.push(reading);
        }
    }
    for field in ["body", "items", "columns"] {
        if let Some(child) = value.get(field) {
            walk(child, japanese, found);
        }
    }
}

/// (nom du téléphone, niveau, en charge) ; rien si la carte est ambiguë.
pub fn phone_link(text: &str) -> Option<(String, u8, bool)> {
    let root = json::parse(text.trim_start_matches('\u{FEFF}').trim_end())?;
    if root.has_duplicate_keys() {
        return None;
    }
    let speak = root.text("speak")?;
    let japanese = speak.ends_with(JAPANESE_SUFFIX);
    let name = speak.strip_suffix(JAPANESE_SUFFIX).or_else(|| speak.strip_suffix(" - Connected to your PC"))?;
    let mut found = Vec::new();
    walk(&root, japanese, &mut found);
    let &(level, charging) = found.first()?;
    (!name.is_empty() && found.iter().all(|&f| f == (level, charging))).then(|| (name.to_string(), level, charging))
}

fn poll_phone_link(ctx: &mut Context) {
    let Some(local) = std::env::var_os("LOCALAPPDATA") else { return };
    let path = std::path::PathBuf::from(local)
        .join(r"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalState\StartMenu\StartMenuCompanion.json");
    let Ok(meta) = std::fs::metadata(&path) else { return };
    if !meta.is_file() || meta.len() == 0 || meta.len() >= MAX_BYTES {
        return;
    }
    let Some((name, percent, charging)) = std::fs::read_to_string(&path).ok().and_then(|t| phone_link(&t)) else { return };
    let mut s = Status::new("phonelink", name, Kind::Other);
    (s.percent, s.charging) = (Some(percent), charging);
    // une carte ancienne n'est plus une lecture actuelle
    s.connected = meta.modified().ok().and_then(|m| SystemTime::now().duration_since(m).ok()).is_none_or(|age| age <= STALE);
    ctx.out.push(s);
}

pub fn poll(ctx: &mut Context) {
    poll_headset_control(ctx);
    poll_phone_link(ctx);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn headset_control_output() {
        let out = r#"warning {"devices":[{"id_vendor":"0x1b1c","id_product":"0x0a64","device":"Corsair","battery":{"status":"BATTERY_CHARGING","level":42}}]}
"#;
        let found = headset_control(out);
        assert_eq!((found[0].0, found[0].1, found[0].2.percent, found[0].2.charging), (0x1B1C, 0x0A64, Some(42), true));
        assert!(headset_control("rien").is_empty());
    }

    #[test]
    fn phone_card() {
        let card = r#"{"speak":"Mon téléphone - Connected to your PC","body":[{"type":"ColumnSet","columns":[{"type":"Column",
            "items":[{"type":"Image","tooltip":"Battery 64% remaining, charging"},{"type":"TextBlock","text":"64%","tooltip":"Battery 64% remaining, charging"}]}]}]}"#;
        assert_eq!(phone_link(card), Some(("Mon téléphone".into(), 64, true)));
        assert_eq!(phone_link(r#"{"speak":"x - Connected to your PC"}"#), None);
        assert_eq!(tooltip_battery("Battery 101% remaining", false), None);
    }
}
