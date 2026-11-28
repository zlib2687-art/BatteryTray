//! Sources standard de Windows, toutes marques : propriété « niveau de batterie » (Bluetooth entre autres),
//! service Batterie Bluetooth LE (GATT 0x180F) et périphériques HID qui déclarent leur batterie.
//! Lecture seule. Aucun pourcentage n'est inventé : sans valeur, l'appareil est ignoré.

use crate::devices::{Kind, Reading};
use crate::hid;
use std::ptr::{null, null_mut};
use windows_sys::core::GUID;
use windows_sys::Win32::Devices::Bluetooth::*;
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::*;
use windows_sys::Win32::Devices::Properties::*;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Storage::FileSystem::*;

const BATTERY_LEVEL: DEVPROPKEY = DEVPROPKEY { fmtid: GUID::from_u128(0x104ea319_6ee2_4701_bd47_8ddbf425bbe5), pid: 2 };
const IS_CONNECTED: DEVPROPKEY = DEVPROPKEY { fmtid: GUID::from_u128(0x83da6326_97a6_4088_9453_a1923f573b29), pid: 15 };
pub(crate) const FRIENDLY_NAME: DEVPROPKEY = DEVPROPKEY { fmtid: GUID::from_u128(0xa45c254e_df1c_4efd_8020_67d146a850e0), pid: 14 };
const DESCRIPTION: DEVPROPKEY = DEVPROPKEY { fmtid: GUID::from_u128(0xa45c254e_df1c_4efd_8020_67d146a850e0), pid: 2 };
/// Interfaces des services Bluetooth LE.
const GATT_SERVICES: GUID = GUID::from_u128(0x6e3bb679_4372_40c8_9eaa_4509df260cd8);
const BATTERY_SERVICE: &str = "{0000180f-0000-1000-8000-00805f9b34fb}";
const BATTERY_LEVEL_CHARACTERISTIC: u16 = 0x2A19;

/// Nom sûr : sans caractères de contrôle ni d'inversion de texte, 60 caractères au plus.
pub fn clean_name(name: &str) -> String {
    let kept: String = name
        .chars()
        .filter(|c| !c.is_control() && !('\u{202A}'..='\u{202E}').contains(c) && !('\u{2066}'..='\u{2069}').contains(c))
        .collect();
    kept.trim().chars().take(60).collect::<String>().trim().to_string()
}

/// Type d'appareil deviné d'après le nom.
pub fn guess_kind(name: &str) -> Kind {
    let lower = name.to_lowercase();
    let words: Vec<&str> = lower.split(|c: char| !c.is_alphanumeric()).collect();
    let has = |list: &[&str]| list.iter().any(|k| lower.contains(k));
    if has(&["headset", "headphone", "buds", "airpods", "casque", "écouteur", "earphone", "wh-", "wf-"]) {
        Kind::Headset
    } else if has(&["keyboard", "clavier", "keychron", "keys"]) {
        Kind::Keyboard
    } else if has(&["controller", "gamepad", "manette", "xbox", "dualsense", "dualshock", "joy-con"]) {
        Kind::Gamepad
    } else if has(&["mouse", "souris", "mx master", "mx anywhere", "trackpad", "trackball"]) {
        Kind::Mouse
    } else if has(&["microphone", "dji mic"]) || words.contains(&"mic") || words.contains(&"micro") {
        Kind::Microphone
    } else {
        Kind::Other
    }
}

/// Identifiant stable qui ne conserve pas l'adresse Bluetooth ni le numéro de série.
fn stable_id(prefix: &str, text: &str) -> String {
    let hash = text.to_lowercase().bytes().fold(0xcbf2_9ce4_8422_2325u64, |h, b| (h ^ u64::from(b)).wrapping_mul(0x100_0000_01b3));
    format!("{prefix}-{hash:016x}")
}

fn reading(id: String, name: &str, percent: u8) -> Option<Reading> {
    let name = clean_name(name);
    (percent <= 100 && !name.is_empty()).then(|| Reading { id, kind: guess_kind(&name), name, percent, charging: false, estimated: false, pointer: Default::default() })
}

/// Deux noms désignent-ils le même appareil ? (« Razer DeathAdder » et « DeathAdder V4 » vus par deux sources)
pub fn same_device(a: &str, b: &str) -> bool {
    let norm = |s: &str| s.to_lowercase().chars().filter(|c| c.is_alphanumeric()).collect::<String>();
    let (a, b) = (norm(a), norm(b));
    a == b || (a.len().min(b.len()) >= 6 && (a.contains(&b) || b.contains(&a)))
}

/// Ajoute les lectures dont l'appareil n'a pas déjà été vu par une source plus précise.
pub fn merge(into: &mut Vec<Reading>, more: Vec<Reading>) {
    for r in more {
        if !into.iter().any(|o| o.id == r.id || same_device(&o.name, &r.name)) {
            into.push(r);
        }
    }
}

unsafe fn property(set: HDEVINFO, device: &SP_DEVINFO_DATA, key: &DEVPROPKEY) -> Option<(u32, Vec<u8>)> {
    let mut kind = 0;
    let mut buffer = vec![0u8; 512];
    let mut needed = 0;
    let ok = SetupDiGetDevicePropertyW(set, device, key, &mut kind, buffer.as_mut_ptr(), buffer.len() as u32, &mut needed, 0);
    (ok != 0).then(|| {
        buffer.truncate(needed as usize);
        (kind, buffer)
    })
}

pub(crate) fn text_of(bytes: &[u8]) -> String {
    let wide: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).take_while(|&c| c != 0).collect();
    String::from_utf16_lossy(&wide)
}

/// Tout périphérique présent pour lequel Windows connaît le niveau de batterie (casques et souris Bluetooth…).
pub fn windows_property() -> Vec<Reading> {
    let mut found = Vec::new();
    unsafe {
        let set = SetupDiGetClassDevsW(null(), null(), null_mut(), DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if set == -1 {
            return found;
        }
        let mut index = 0;
        loop {
            let mut device: SP_DEVINFO_DATA = std::mem::zeroed();
            device.cbSize = size_of::<SP_DEVINFO_DATA>() as u32;
            if SetupDiEnumDeviceInfo(set, index, &mut device) == 0 {
                break;
            }
            index += 1;
            let Some((_, level)) = property(set, &device, &BATTERY_LEVEL) else { continue };
            // un appareil appairé mais déconnecté garde sa dernière valeur : elle n'est pas actuelle
            if property(set, &device, &IS_CONNECTED).is_some_and(|(_, v)| v.first() == Some(&0)) {
                continue;
            }
            let Some(&percent) = level.first() else { continue };
            let name = property(set, &device, &FRIENDLY_NAME)
                .or_else(|| property(set, &device, &DESCRIPTION))
                .map(|(_, v)| text_of(&v))
                .unwrap_or_default();
            let mut instance = [0u16; 256];
            SetupDiGetDeviceInstanceIdW(set, &device, instance.as_mut_ptr(), instance.len() as u32, null_mut());
            let instance = String::from_utf16_lossy(&instance);
            found.extend(reading(stable_id("pnp", instance.trim_end_matches('\0')), &name, percent));
        }
        SetupDiDestroyDeviceInfoList(set);
    }
    found
}

pub(crate) unsafe fn node_property(node: u32, key: &DEVPROPKEY) -> Option<Vec<u8>> {
    let mut kind = 0;
    let mut buffer = vec![0u8; 512];
    let mut size = buffer.len() as u32;
    let status = CM_Get_DevNode_PropertyW(node, key, &mut kind, buffer.as_mut_ptr(), &mut size, 0);
    (status == CR_SUCCESS).then(|| {
        buffer.truncate(size as usize);
        buffer
    })
}

/// Lit la caractéristique « Battery Level » d'un service Batterie ouvert.
unsafe fn gatt_level(path: &[u16]) -> Option<u8> {
    let handle = CreateFileW(path.as_ptr(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, null(), OPEN_EXISTING, 0, null_mut());
    if handle == INVALID_HANDLE_VALUE {
        return None;
    }
    let level = (|| {
        let mut list: [BTH_LE_GATT_CHARACTERISTIC; 8] = std::mem::zeroed();
        let mut count = 0u16;
        if BluetoothGATTGetCharacteristics(handle, null(), list.len() as u16, list.as_mut_ptr(), &mut count, 0) != 0 {
            return None;
        }
        let characteristic = list
            .iter()
            .take(count as usize)
            .find(|c| c.CharacteristicUuid.IsShortUuid != 0 && c.CharacteristicUuid.Value.ShortUuid == BATTERY_LEVEL_CHARACTERISTIC)?;
        let mut buffer = [0u32; 16];
        let value = buffer.as_mut_ptr() as *mut BTH_LE_GATT_CHARACTERISTIC_VALUE;
        let mut needed = 0u16;
        let status = BluetoothGATTGetCharacteristicValue(
            handle,
            characteristic,
            size_of_val(&buffer) as u32,
            value,
            &mut needed,
            BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE,
        );
        (status == 0 && (*value).DataSize >= 1).then(|| (*value).Data[0])
    })();
    CloseHandle(handle);
    level
}

/// Service Batterie des appareils Bluetooth LE connectés. Plusieurs batteries (écouteurs) : la plus faible.
pub fn bluetooth_le() -> Vec<Reading> {
    let mut found: Vec<Reading> = Vec::new();
    unsafe {
        let set = SetupDiGetClassDevsW(&GATT_SERVICES, null(), null_mut(), DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if set == -1 {
            return found;
        }
        let mut index = 0;
        loop {
            let mut interface: SP_DEVICE_INTERFACE_DATA = std::mem::zeroed();
            interface.cbSize = size_of::<SP_DEVICE_INTERFACE_DATA>() as u32;
            if SetupDiEnumDeviceInterfaces(set, null(), &GATT_SERVICES, index, &mut interface) == 0 {
                break;
            }
            index += 1;
            let mut buffer = [0u64; 256];
            let detail = buffer.as_mut_ptr() as *mut SP_DEVICE_INTERFACE_DETAIL_DATA_W;
            (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;
            let mut device: SP_DEVINFO_DATA = std::mem::zeroed();
            device.cbSize = size_of::<SP_DEVINFO_DATA>() as u32;
            if SetupDiGetDeviceInterfaceDetailW(set, &interface, detail, size_of_val(&buffer) as u32, null_mut(), &mut device) == 0 {
                continue;
            }
            let start = std::ptr::addr_of!((*detail).DevicePath) as *const u16;
            let length = (0..).take_while(|&i| *start.add(i) != 0).count();
            let path = std::slice::from_raw_parts(start, length + 1);
            if !String::from_utf16_lossy(path).to_lowercase().contains(BATTERY_SERVICE) {
                continue;
            }
            // le nom et l'état de connexion sont ceux de l'appareil, parent du service
            let mut parent = 0;
            if CM_Get_Parent(&mut parent, device.DevInst, 0) != CR_SUCCESS {
                continue;
            }
            if node_property(parent, &IS_CONNECTED).is_some_and(|v| v.first() == Some(&0)) {
                continue;
            }
            let name = node_property(parent, &FRIENDLY_NAME).map(|v| text_of(&v)).unwrap_or_default();
            let mut instance = [0u16; 256];
            CM_Get_Device_IDW(parent, instance.as_mut_ptr(), instance.len() as u32, 0);
            let id = stable_id("ble", String::from_utf16_lossy(&instance).trim_end_matches('\0'));
            let Some(level) = gatt_level(path) else { continue };
            let Some(r) = reading(id, &name, level) else { continue };
            match found.iter_mut().find(|o| o.id == r.id) {
                Some(other) => other.percent = other.percent.min(r.percent),
                None => found.push(r),
            }
        }
        SetupDiDestroyDeviceInfoList(set);
    }
    found
}

/// Valeur brute ramenée en pourcentage selon l'étendue déclarée par l'appareil.
pub fn scaled(value: u32, minimum: i32, maximum: i32) -> Option<u8> {
    let (low, high, value) = (i64::from(minimum), i64::from(maximum), i64::from(value));
    (high > low && value >= low && value <= high).then(|| (((value - low) * 100 + (high - low) / 2) / (high - low)) as u8)
}

/// Périphériques HID qui déclarent leur batterie (page « Battery System » ou « Battery Strength »).
pub fn hid_standard() -> Vec<Reading> {
    hid::batteries()
        .into_iter()
        .filter_map(|b| reading(stable_id("hid", &b.path), &b.product, scaled(b.value, b.minimum, b.maximum)?))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hostile_names_are_cleaned() {
        assert_eq!(clean_name(" Ma\nsouris\u{202E}\t "), "Masouris");
        assert_eq!(clean_name(&"x".repeat(200)).len(), 60);
        assert!(reading("a".into(), "\u{0}", 50).is_none());
        assert!(reading("a".into(), "Souris", 255).is_none());
    }

    #[test]
    fn kinds_from_names() {
        assert_eq!(guess_kind("WH-1000XM5"), Kind::Headset);
        assert_eq!(guess_kind("MX Keys"), Kind::Keyboard);
        assert_eq!(guess_kind("Xbox Wireless Controller"), Kind::Gamepad);
        assert_eq!(guess_kind("MX Master 3S"), Kind::Mouse);
        assert_eq!(guess_kind("DJI Mic Mini"), Kind::Microphone);
        assert_eq!(guess_kind("Microsoft Device"), Kind::Other);
    }

    #[test]
    fn duplicates_and_scales() {
        assert!(same_device("Razer DeathAdder V4 Pro", "DeathAdder V4 Pro"));
        assert!(!same_device("Mouse", "Gaming Mouse"));
        assert_eq!(scaled(128, 0, 255), Some(50));
        assert_eq!(scaled(80, 0, 100), Some(80));
        assert_eq!(scaled(300, 0, 255), None);
        assert_eq!(scaled(1, 0, 0), None);
    }
}
