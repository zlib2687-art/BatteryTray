//! Écouteurs et manette en Bluetooth : AirPods / Beats et Fast Pair (annonces), Galaxy Buds et Nothing Ear
//! (liaison série RFCOMM), GameSir Cyclone Pro (GATT). Seuls les appareils déjà appairés à Windows sont lus.
//! Dérivé de BatteryHub (GPL-3.0), src/drivers/.

use super::atk::{open_key, subkeys};
use super::{adverts, pause, Context, Status};
use crate::devices::Kind;
use crate::generic::{node_property, text_of, FRIENDLY_NAME};
use std::cell::RefCell;
use std::ffi::c_void;
use std::ptr::{null, null_mut};
use std::sync::Mutex;
use std::time::{Duration, Instant};
use windows_sys::core::GUID;
use windows_sys::Win32::Devices::Bluetooth::*;
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::*;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Networking::WinSock::*;
use windows_sys::Win32::Storage::FileSystem::*;
use windows_sys::Win32::System::Registry::HKEY_LOCAL_MACHINE;

fn level(value: u8) -> Option<u8> {
    Some(value).filter(|&v| v <= 100)
}

fn headset(key: String, name: &str, percent: u8, charging: bool) -> Status {
    let mut s = Status::new(key, name, Kind::Headset);
    (s.percent, s.charging) = (Some(percent), charging);
    s
}

// ---------------------------------------------------------------- Appareils appairés

struct Paired {
    address: u64,
    name: String,
    connected: bool,
}

/// Appareils Bluetooth classiques appairés (aucune recherche radio n'est lancée).
fn paired_devices() -> Vec<Paired> {
    let mut found = Vec::new();
    unsafe {
        let mut search: BLUETOOTH_DEVICE_SEARCH_PARAMS = std::mem::zeroed();
        search.dwSize = size_of::<BLUETOOTH_DEVICE_SEARCH_PARAMS>() as u32;
        (search.fReturnAuthenticated, search.fReturnRemembered, search.fReturnConnected) = (1, 1, 1);
        let mut info: BLUETOOTH_DEVICE_INFO = std::mem::zeroed();
        info.dwSize = size_of::<BLUETOOTH_DEVICE_INFO>() as u32;
        let handle = BluetoothFindFirstDevice(&search, &mut info);
        if handle.is_null() {
            return found;
        }
        loop {
            if info.fAuthenticated != 0 || info.fConnected != 0 {
                let len = info.szName.iter().position(|&c| c == 0).unwrap_or(info.szName.len());
                found.push(Paired {
                    address: info.Address.Anonymous.ullLong,
                    name: String::from_utf16_lossy(&info.szName[..len]),
                    connected: info.fConnected != 0,
                });
            }
            if BluetoothFindNextDevice(handle, &mut info) == 0 {
                break;
            }
        }
        BluetoothFindDeviceClose(handle);
    }
    found
}

// ---------------------------------------------------------------- Écouteurs : trois batteries

/// Gauche, droite, boîtier : (niveau, en charge).
#[derive(Clone, Copy, Default, Debug, PartialEq)]
pub struct Parts {
    pub left: Option<(u8, bool)>,
    pub right: Option<(u8, bool)>,
    pub case: Option<(u8, bool)>,
}

impl Parts {
    /// Le plus faible des écouteurs qui ne chargent pas, sinon le plus faible, sinon le boîtier.
    pub fn summary(&self) -> Option<(u8, bool)> {
        let buds = [self.left, self.right];
        let lowest = |charging: bool| buds.iter().flatten().filter(|b| b.1 == charging).map(|b| b.0).min();
        match (lowest(false), lowest(true)) {
            (Some(v), _) => Some((v, false)),
            (None, Some(v)) => Some((v, true)),
            _ => self.case,
        }
    }

    fn is_empty(&self) -> bool {
        self.left.is_none() && self.right.is_none() && self.case.is_none()
    }
}

// ---------------------------------------------------------------- AirPods / Beats

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Airpods {
    pub model: u16,
    pub single: bool,
    pub parts: Parts,
}

const SINGLE_BATTERY: [u16; 12] =
    [0x200A, 0x201F, 0x202D, 0x2003, 0x2005, 0x2006, 0x2009, 0x200C, 0x200D, 0x2010, 0x2017, 0x2025];

fn proximity(s: &[u8]) -> Option<Airpods> {
    if s.len() < 27 || s[..3] != [0x07, 0x19, 0x01] {
        return None;
    }
    let model = u16::from_le_bytes([s[3], s[4]]);
    if model & 0xFF00 != 0x2000 {
        return None;
    }
    let tenth = |nibble: u8| (nibble <= 10).then_some(nibble * 10);
    let single = SINGLE_BATTERY.contains(&model);
    let left_broadcasts = s[5] & 0x20 != 0;
    let (current, other, charge) = (s[6] & 0x0F, s[6] >> 4, s[7] >> 4);
    let mut parts = Parts::default();
    if single {
        let (mut value, mut charging) = (tenth(current), charge & 1 != 0);
        if current == 0 && (1..=10).contains(&other) {
            (value, charging) = (tenth(other), charge & 2 != 0);
        }
        parts.left = Some((value?, charging));
    } else {
        let (current_bit, other_bit) = (charge & 1 != 0, charge & 2 != 0);
        let (l, r) = if left_broadcasts { (current, other) } else { (other, current) };
        let (lc, rc) = if left_broadcasts { (current_bit, other_bit) } else { (other_bit, current_bit) };
        parts.left = tenth(l).map(|v| (v, lc));
        parts.right = tenth(r).map(|v| (v, rc));
        parts.case = tenth(s[7] & 0x0F).map(|v| (v, charge & 4 != 0));
    }
    (!parts.is_empty()).then_some(Airpods { model, single, parts })
}

/// Données fabricant Apple (sans l'identifiant de société) : suite de blocs type, longueur, valeur.
pub fn airpods(data: &[u8]) -> Option<Airpods> {
    let mut at = 0;
    while at + 1 < data.len() {
        let len = usize::from(data[at + 1]);
        let block = data.get(at..at + 2 + len)?;
        if let Some(found) = proximity(block) {
            return Some(found);
        }
        at += 2 + len;
    }
    None
}

#[derive(Clone, Copy)]
struct Seen {
    address: u64,
    rssi: i16,
    at: Instant,
    pods: Airpods,
}

static APPLE: Mutex<Vec<Seen>> = Mutex::new(Vec::new());
static FAST_PAIR: Mutex<Vec<(u64, Instant, Parts)>> = Mutex::new(Vec::new());

/// Appelé par Windows pour chaque section d'annonce reçue.
fn on_section(address: u64, rssi: i16, kind: u8, data: &[u8]) {
    let now = Instant::now();
    if kind == 0xFF && data.starts_with(&[0x4C, 0x00]) {
        let Some(pods) = airpods(&data[2..]) else { return };
        let Ok(mut all) = APPLE.lock() else { return };
        // les adresses changent : on abandonne les anciennes
        all.retain(|s| s.address != address && now - s.at < Duration::from_secs(600));
        all.push(Seen { address, rssi, at: now, pods });
    } else if kind == 0x16 && data.starts_with(&[0x2C, 0xFE]) {
        let Some(parts) = fast_pair(&data[2..]) else { return };
        let Ok(mut all) = FAST_PAIR.lock() else { return };
        all.retain(|e| e.0 != address && now - e.1 < Duration::from_secs(300));
        all.push((address, now, parts));
    }
}

/// Meilleure annonce pour un modèle : la plus fraîche et la plus forte.
fn pick(model: u16, connected: bool, all: &[Seen]) -> Option<Airpods> {
    let max_age = Duration::from_secs(if connected { 300 } else { 90 });
    let min_rssi = if connected { -90 } else { -80 };
    let fresh = |s: &Seen| s.at.elapsed() < Duration::from_secs(90);
    let best = all
        .iter()
        .filter(|s| s.pods.model == model && s.at.elapsed() <= max_age && s.rssi >= min_rssi)
        .max_by_key(|s| (fresh(s), if fresh(s) { s.rssi } else { 0 }, s.at))?;
    let mut pods = best.pods;
    if !pods.single {
        // chaque écouteur annonce sa vue : on complète avec les annonces voisines
        for s in all.iter().filter(|s| s.pods.model == model && !s.pods.single) {
            let gap = if s.at > best.at { s.at - best.at } else { best.at - s.at };
            if gap <= Duration::from_secs(15) {
                pods.parts.left = pods.parts.left.or(s.pods.parts.left);
                pods.parts.right = pods.parts.right.or(s.pods.parts.right);
                pods.parts.case = pods.parts.case.or(s.pods.parts.case);
            }
        }
    }
    Some(pods)
}

/// « …&<12 chiffres hexadécimaux>_C… » dans le nom d'une instance Bluetooth.
pub fn instance_address(instance: &str) -> Option<u64> {
    let upper = instance.to_ascii_uppercase();
    let end = upper.find("_C")?;
    let digits = upper.get(end.checked_sub(12)?..end)?;
    (upper[..end - 12].ends_with('&')).then(|| u64::from_str_radix(digits, 16).ok()).flatten()
}

/// Produit Apple de chaque appareil audio appairé, d'après le registre.
fn apple_products() -> Vec<(u64, u16)> {
    let mut found = Vec::new();
    let Some(root) = open_key(HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Enum\BTHENUM") else { return found };
    for name in subkeys(&root) {
        let upper = name.to_ascii_uppercase();
        let marker = "_VID&0001004C_PID&";
        let Some(at) = upper.find(marker) else { continue };
        let Some(product) = upper.get(at + marker.len()..at + marker.len() + 4).and_then(|t| u16::from_str_radix(t, 16).ok())
        else {
            continue;
        };
        let Some(key) = open_key(root.0, &name) else { continue };
        found.extend(subkeys(&key).iter().filter_map(|i| instance_address(i)).map(|a| (a, product)));
    }
    found
}

/// Laisse aux écouteurs le temps de diffuser, juste après le démarrage de l'écoute.
fn wait_for(ready: impl Fn() -> bool) {
    for _ in 0..15 {
        if ready() {
            return;
        }
        pause(200);
    }
}

fn poll_airpods(ctx: &mut Context, paired: &[Paired]) {
    let products = apple_products();
    let mut devices: Vec<(&Paired, u16)> = paired
        .iter()
        .filter_map(|p| {
            let product = products.iter().find(|e| e.0 == p.address)?.1;
            (product & 0xFF00 == 0x2000).then_some((p, product))
        })
        .collect();
    if devices.is_empty() {
        return;
    }
    devices.sort_by_key(|d| !d.0.connected);
    let snapshot = || APPLE.lock().map(|a| a.clone()).unwrap_or_default();
    if adverts::ensure(on_section) {
        wait_for(|| {
            let all = snapshot();
            devices.iter().any(|d| pick(d.1, d.0.connected, &all).is_some())
        });
    }
    let all = snapshot();
    let mut used = Vec::new();
    for (device, product) in devices {
        if !device.connected && used.contains(&product) {
            continue;
        }
        let Some(pods) = pick(product, device.connected, &all) else { continue };
        used.push(product);
        let value = if pods.single { pods.parts.left } else { pods.parts.summary() };
        let Some((percent, charging)) = value else { continue };
        let name = if device.name.is_empty() { "AirPods" } else { &device.name };
        ctx.out.push(headset(format!("airpods:{product:04X}:{}", device.name), name, percent, charging));
    }
}

// ---------------------------------------------------------------- Fast Pair

/// Données de service 0xFE2C : bloc « batterie » (types 3 et 4).
pub fn fast_pair(d: &[u8]) -> Option<Parts> {
    if d.len() < 2 || d[0] != 0 {
        return None;
    }
    let mut at = 1;
    while at < d.len() {
        let (len, kind) = (usize::from(d[at] >> 4), d[at] & 0x0F);
        let values = d.get(at + 1..at + 1 + len)?;
        if (kind == 3 || kind == 4) && len >= 1 {
            let part = |i: usize| values.get(i).and_then(|&b| level(b & 0x7F).map(|v| (v, b & 0x80 != 0)));
            let parts = Parts { left: part(0), right: part(1), case: part(2) };
            return (!parts.is_empty()).then_some(parts);
        }
        at += 1 + len;
    }
    None
}

fn poll_fast_pair(ctx: &mut Context, paired: &[Paired]) {
    // pas d'écoute tant qu'aucun appareil appairé n'est connecté
    if !paired.iter().any(|p| p.connected) {
        return;
    }
    let lookup = |address: u64| {
        let all = FAST_PAIR.lock().ok()?;
        all.iter().find(|e| e.0 == address && e.1.elapsed() < Duration::from_secs(300)).map(|e| e.2)
    };
    if adverts::ensure(on_section) {
        wait_for(|| paired.iter().any(|p| lookup(p.address).is_some()));
    }
    for p in paired.iter().filter(|p| p.connected) {
        let Some((percent, charging)) = lookup(p.address).and_then(|parts| parts.summary()) else { continue };
        let name = if p.name.is_empty() { "Bluetooth earbuds" } else { &p.name };
        ctx.out.push(headset(format!("fastpair:{:012X}", p.address), name, percent, charging));
    }
}

// ---------------------------------------------------------------- Galaxy Buds, Nothing Ear (RFCOMM)

fn crc_ccitt(data: &[u8]) -> u16 {
    data.iter().fold(0u16, |mut crc, &b| {
        crc ^= u16::from(b) << 8;
        for _ in 0..8 {
            crc = if crc & 0x8000 != 0 { (crc << 1) ^ 0x1021 } else { crc << 1 };
        }
        crc
    })
}

fn crc_modbus(data: &[u8]) -> u16 {
    data.iter().fold(0xFFFFu16, |mut crc, &b| {
        crc ^= u16::from(b);
        for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0xA001 } else { crc >> 1 };
        }
        crc
    })
}

/// Dernière trame d'état d'un flux Galaxy Buds : gauche, droite, boîtier.
pub fn galaxy(data: &[u8], legacy: bool) -> Option<Parts> {
    let (start, end) = if legacy { (0xFE, 0xEE) } else { (0xFD, 0xDD) };
    let part = |b: u8| level(b).filter(|&v| v > 0).map(|v| (v, false));
    let mut result = None;
    let mut i = 0;
    while i + 6 <= data.len() {
        i += 1;
        let at = i - 1;
        if data[at] != start {
            continue;
        }
        let len = if legacy {
            usize::from(data[at + 2])
        } else {
            let header = u16::from_le_bytes([data[at + 1], data[at + 2]]);
            if header & 0x2000 != 0 {
                continue; // fragments : données de micrologiciel seulement
            }
            usize::from(header & 0x3FF)
        };
        if len < 3 || at + 3 + len >= data.len() || data[at + 3 + len] != end {
            continue;
        }
        let body = &data[at + 3..at + 3 + len]; // identifiant, charge utile, somme
        if crc_ccitt(&body[..len - 2]) != u16::from_le_bytes([body[len - 2], body[len - 1]]) {
            continue;
        }
        let p = &body[1..len - 2];
        let first = match body[0] {
            0x61 if p.len() >= 4 => 2,
            0x60 if p.len() >= 3 => 1,
            _ => continue,
        };
        let case = p.get(first + 5).filter(|_| !legacy).and_then(|&b| part(b));
        result = Some(Parts { left: part(p[first]), right: part(p[first + 1]), case });
        i = at + 4 + len;
    }
    result
}

pub fn nothing_query() -> [u8; 10] {
    let mut f = [0x55, 0x60, 0x01, 0x07, 0xC0, 0x00, 0x00, 0x01, 0x00, 0x00];
    let crc = crc_modbus(&f[..8]).to_le_bytes();
    (f[8], f[9]) = (crc[0], crc[1]);
    f
}

pub fn nothing(data: &[u8]) -> Option<Parts> {
    let mut result = None;
    let mut i = 0;
    while i + 10 <= data.len() {
        let at = i;
        i += 1;
        if data[at..at + 3] != [0x55, 0x60, 0x01] {
            continue;
        }
        let len = usize::from(data[at + 5]);
        let total = 8 + len + 2;
        let Some(frame) = data.get(at..at + total) else { continue };
        if crc_modbus(&frame[..8 + len]) != u16::from_le_bytes([frame[8 + len], frame[9 + len]]) {
            continue;
        }
        let command = u16::from_le_bytes([frame[3], frame[4]]);
        if command != 0xC007 && command != 0xE001 {
            i = at + total;
            continue;
        }
        let payload = &frame[8..8 + len];
        let Some((&count, pairs)) = payload.split_first() else { continue };
        let Some(pairs) = pairs.get(..usize::from(count) * 2) else { continue };
        let mut parts = Parts::default();
        for pair in pairs.chunks_exact(2) {
            let Some(value) = level(pair[1] & 0x7F).map(|v| (v, pair[1] & 0x80 != 0)) else { continue };
            match pair[0] {
                0x02 => parts.left = Some(value),
                0x03 => parts.right = Some(value),
                0x04 => parts.case = Some(value),
                _ => {}
            }
        }
        if !parts.is_empty() {
            result = Some(parts);
        }
        i = at + total;
    }
    result
}

struct Socket(SOCKET);

impl Drop for Socket {
    fn drop(&mut self) {
        unsafe { closesocket(self.0) };
    }
}

fn winsock_ready() -> bool {
    static READY: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *READY.get_or_init(|| unsafe {
        let mut data: WSADATA = std::mem::zeroed();
        WSAStartup(0x0202, &mut data) == 0
    })
}

/// Se connecte au service série `service`, envoie éventuellement une demande et lit jusqu'à `done`.
fn rfcomm<T>(address: u64, service: u128, request: Option<&[u8]>, connect_ms: u32, read_ms: u64, done: impl Fn(&[u8]) -> Option<T>) -> Option<T> {
    if !winsock_ready() {
        return None;
    }
    unsafe {
        let raw = socket(i32::from(AF_BTH), SOCK_STREAM, BTHPROTO_RFCOMM as i32);
        if raw == INVALID_SOCKET {
            return None;
        }
        let s = Socket(raw);
        let mut target: SOCKADDR_BTH = std::mem::zeroed();
        target.addressFamily = AF_BTH;
        target.btAddr = address;
        target.serviceClassId = GUID::from_u128(service); // port 0 : Windows le cherche dans l'annonce du service
        let mut blocking = 1u32;
        ioctlsocket(s.0, FIONBIO, &mut blocking);
        let status = connect(s.0, &target as *const SOCKADDR_BTH as *const SOCKADDR, size_of::<SOCKADDR_BTH>() as i32);
        if status != 0 {
            if WSAGetLastError() != WSAEWOULDBLOCK {
                return None;
            }
            let mut writable: FD_SET = std::mem::zeroed();
            (writable.fd_count, writable.fd_array[0]) = (1, s.0);
            let mut failed = std::ptr::read(&writable);
            let wait = TIMEVAL { tv_sec: (connect_ms / 1000) as i32, tv_usec: ((connect_ms % 1000) * 1000) as i32 };
            if select(0, null_mut(), &mut writable, &mut failed, &wait) <= 0 || writable.fd_count == 0 {
                return None;
            }
        }
        blocking = 0;
        ioctlsocket(s.0, FIONBIO, &mut blocking);
        let timeout = 300u32;
        setsockopt(s.0, SOL_SOCKET, SO_RCVTIMEO, &timeout as *const u32 as *const u8, 4);
        setsockopt(s.0, SOL_SOCKET, SO_SNDTIMEO, &timeout as *const u32 as *const u8, 4);
        if let Some(r) = request {
            if send(s.0, r.as_ptr(), r.len() as i32, 0) != r.len() as i32 {
                return None;
            }
        }
        let mut data = Vec::new();
        let mut chunk = [0u8; 512];
        let end = Instant::now() + Duration::from_millis(read_ms);
        while Instant::now() < end && data.len() < 8192 {
            let n = recv(s.0, chunk.as_mut_ptr(), chunk.len() as i32, 0);
            if n == 0 || (n < 0 && WSAGetLastError() != WSAETIMEDOUT) {
                break;
            }
            if n > 0 {
                data.extend_from_slice(&chunk[..n as usize]);
                if let Some(found) = done(&data) {
                    return Some(found);
                }
            }
        }
        None
    }
}

const SPP_NEW: u128 = 0x2e73a4ad_332d_41fc_90e2_16bef06523f2;
const SPP_STANDARD: u128 = 0x00001101_0000_1000_8000_00805f9b34fb;
const SPP_LEGACY: u128 = 0x00001102_0000_1000_8000_00805f9b34fd;
const NOTHING_LINK: u128 = 0xaeac4a03_dff5_498f_843a_34487cf133eb;

pub fn is_nothing(name: &str) -> bool {
    let n = name.to_lowercase();
    (n.contains("nothing") && (n.contains("ear") || n.contains("buds")))
        || n.contains("ear (")
        || n.contains("ear 3a")
        || n.contains("cmf buds")
}

/// Le plus faible des deux écouteurs, ou le seul connu.
fn lowest_bud(parts: Parts) -> Option<u8> {
    [parts.left, parts.right].iter().flatten().map(|b| b.0).min()
}

fn poll_buds(ctx: &mut Context, paired: &[Paired]) {
    // jamais un appareil non connecté
    for p in paired.iter().filter(|p| p.connected) {
        if ctx.out_of_time() {
            return;
        }
        let galaxy_buds = p.name.to_lowercase().contains("buds") && !is_nothing(&p.name);
        if galaxy_buds {
            let found = [(SPP_NEW, false), (SPP_STANDARD, false), (SPP_LEGACY, true)]
                .into_iter()
                .find_map(|(service, legacy)| rfcomm(p.address, service, None, 4000, 3000, |d| galaxy(d, legacy)));
            if let Some(percent) = found.and_then(lowest_bud) {
                ctx.out.push(headset(format!("galaxybuds:{:012X}", p.address), &p.name, percent, false));
            }
        } else if is_nothing(&p.name) {
            let Some(parts) = rfcomm(p.address, NOTHING_LINK, Some(&nothing_query()), 3000, 2000, nothing) else { continue };
            let Some(percent) = lowest_bud(parts) else { continue };
            let charging = [parts.left, parts.right].iter().flatten().any(|b| b.1);
            ctx.out.push(headset(format!("nothingear:{:012X}", p.address), &p.name, percent, charging));
        }
    }
}

// ---------------------------------------------------------------- GameSir Cyclone Pro (GATT)

const GAMESIR_SERVICE: &str = "00008650-0000-1000-8000-00805f9b34fb";
const GAMESIR_CHARACTERISTIC: u128 = 0x0000865f_0000_1000_8000_00805f9b34fb;

/// `Some(None)` : en charge, sans niveau.
pub fn gamesir(p: &[u8]) -> Option<Option<u8>> {
    let p = if p.len() == 20 && p[0] == 0xC9 { &p[1..] } else { p };
    if p.len() != 19 {
        return None;
    }
    if p[18] == 0xFF {
        return Some(None);
    }
    level(p[18]).map(Some)
}

static NOTIFIED: Mutex<Option<Option<u8>>> = Mutex::new(None);

unsafe extern "system" fn on_notify(_: BTH_LE_GATT_EVENT_TYPE, parameter: *const c_void, _: *const c_void) {
    if parameter.is_null() {
        return;
    }
    let event = &*(parameter as *const BLUETOOTH_GATT_VALUE_CHANGED_EVENT);
    let value = event.CharacteristicValue;
    if value.is_null() || (*value).DataSize > 64 {
        return;
    }
    let data = std::slice::from_raw_parts(std::ptr::addr_of!((*value).Data) as *const u8, (*value).DataSize as usize);
    if let (Some(reading), Ok(mut slot)) = (gamesir(data), NOTIFIED.lock()) {
        *slot = Some(reading);
    }
}

/// Chemins et nœuds des interfaces présentes d'une classe.
fn interfaces(class: &GUID) -> Vec<(Vec<u16>, u32)> {
    let mut found = Vec::new();
    unsafe {
        let set = SetupDiGetClassDevsW(class, null(), null_mut(), DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if set == -1 {
            return found;
        }
        for index in 0.. {
            let mut interface: SP_DEVICE_INTERFACE_DATA = std::mem::zeroed();
            interface.cbSize = size_of::<SP_DEVICE_INTERFACE_DATA>() as u32;
            if SetupDiEnumDeviceInterfaces(set, null(), class, index, &mut interface) == 0 {
                break;
            }
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
            found.push((std::slice::from_raw_parts(start, length + 1).to_vec(), device.DevInst));
        }
        SetupDiDestroyDeviceInfoList(set);
    }
    found
}

/// Adresse (texte) d'une manette « GameSir-Cyclone » appairée en Bluetooth LE.
fn gamesir_address() -> Option<String> {
    interfaces(&GUID_BLUETOOTHLE_DEVICE_INTERFACE).into_iter().find_map(|(path, node)| {
        let name = unsafe { node_property(node, &FRIENDLY_NAME) }.map(|v| text_of(&v))?;
        if !name.to_ascii_lowercase().starts_with("gamesir-cyclone") {
            return None;
        }
        let path = String::from_utf16_lossy(&path).to_ascii_lowercase();
        let at = path.find("dev_")? + 4;
        path.get(at..at + 12).map(String::from)
    })
}

unsafe fn is_wanted(uuid: *const BTH_LE_UUID) -> bool {
    let short = std::ptr::addr_of!((*uuid).IsShortUuid).read_unaligned() != 0;
    let value = std::ptr::addr_of!((*uuid).Value);
    if short {
        // Windows abrège les identifiants de la base Bluetooth
        std::ptr::addr_of!((*value).ShortUuid).read_unaligned() == (GAMESIR_CHARACTERISTIC >> 96) as u16
    } else {
        let g = std::ptr::addr_of!((*value).LongUuid).read_unaligned();
        let wanted = GUID::from_u128(GAMESIR_CHARACTERISTIC);
        (g.data1, g.data2, g.data3, g.data4) == (wanted.data1, wanted.data2, wanted.data3, wanted.data4)
    }
}

/// S'abonne, envoie la demande « 01 01 » et attend la notification.
unsafe fn gamesir_exchange(handle: HANDLE) -> Option<Option<u8>> {
    let mut list: [BTH_LE_GATT_CHARACTERISTIC; 16] = std::mem::zeroed();
    let mut count = 0u16;
    if BluetoothGATTGetCharacteristics(handle, null(), list.len() as u16, list.as_mut_ptr(), &mut count, 0) != 0 {
        return None;
    }
    let characteristic = list.iter().take(usize::from(count)).find(|c| is_wanted(std::ptr::addr_of!(c.CharacteristicUuid)))?;
    if characteristic.IsNotifiable == 0 {
        return None;
    }
    let mut descriptors: [BTH_LE_GATT_DESCRIPTOR; 8] = std::mem::zeroed();
    let mut found = 0u16;
    if BluetoothGATTGetDescriptors(handle, characteristic, descriptors.len() as u16, descriptors.as_mut_ptr(), &mut found, 0) != 0 {
        return None;
    }
    let config = descriptors.iter().take(usize::from(found)).find(|d| d.DescriptorType == ClientCharacteristicConfiguration)?;
    let subscribe = |on: u8| {
        let mut value: BTH_LE_GATT_DESCRIPTOR_VALUE = std::mem::zeroed();
        value.DescriptorType = ClientCharacteristicConfiguration;
        value.Anonymous.ClientCharacteristicConfiguration.IsSubscribeToNotification = on;
        BluetoothGATTSetDescriptorValue(handle, config, &value, 0) == 0
    };
    if let Ok(mut slot) = NOTIFIED.lock() {
        *slot = None;
    }
    if !subscribe(1) {
        return None;
    }
    let registration = BLUETOOTH_GATT_VALUE_CHANGED_EVENT_REGISTRATION { NumCharacteristics: 1, Characteristics: [*characteristic] };
    let mut event = 0isize;
    let registered = BluetoothGATTRegisterEvent(
        handle,
        CharacteristicValueChangedEvent,
        &registration as *const _ as *const c_void,
        Some(on_notify),
        null(),
        &mut event,
        0,
    ) == 0;
    let mut result = None;
    if registered {
        // BTH_LE_GATT_CHARACTERISTIC_VALUE : longueur puis octets
        let request = [2u32, u32::from_le_bytes([0x01, 0x01, 0, 0])];
        let flags = if characteristic.IsWritableWithoutResponse != 0 { BLUETOOTH_GATT_FLAG_WRITE_WITHOUT_RESPONSE } else { 0 };
        if BluetoothGATTSetCharacteristicValue(handle, characteristic, request.as_ptr() as *const _, 0, flags) == 0 {
            let end = Instant::now() + Duration::from_secs(3);
            while result.is_none() && Instant::now() < end {
                pause(50);
                result = NOTIFIED.lock().ok().and_then(|slot| *slot);
            }
        }
        BluetoothGATTUnregisterEvent(event, 0);
    }
    subscribe(0);
    result
}

fn gamesir_query(address: &str) -> Option<Option<u8>> {
    let services = GUID::from_u128(0x6e3bb679_4372_40c8_9eaa_4509df260cd8);
    interfaces(&services).into_iter().find_map(|(path, _)| {
        let text = String::from_utf16_lossy(&path).to_ascii_lowercase();
        if !text.contains(GAMESIR_SERVICE) || !text.contains(address) {
            return None;
        }
        unsafe {
            let handle = CreateFileW(path.as_ptr(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, null(), OPEN_EXISTING, 0, null_mut());
            if handle == INVALID_HANDLE_VALUE {
                return None;
            }
            let result = gamesir_exchange(handle);
            CloseHandle(handle);
            result
        }
    })
}

#[derive(Default)]
struct Pad {
    address: String,
    asked: Option<Instant>,
    answered: Option<Instant>,
    last_failed: bool,
    value: Option<(Option<u8>, bool)>,
}

thread_local!(static PAD: RefCell<Pad> = RefCell::new(Pad::default()));

fn poll_gamesir(ctx: &mut Context) {
    let Some(address) = gamesir_address() else { return };
    let value = PAD.with_borrow_mut(|pad| {
        if pad.address != address {
            // une autre manette appairée : on oublie l'ancienne lecture
            *pad = Pad { address: address.clone(), ..Pad::default() };
        }
        let wait = Duration::from_secs(if pad.last_failed { 60 } else { 120 });
        if pad.asked.is_none_or(|t| t.elapsed() >= wait) {
            pad.asked = Some(Instant::now());
            // les premiers appels après la liaison échouent souvent : une reprise suffit
            let reading = gamesir_query(&address).or_else(|| {
                pause(1500);
                gamesir_query(&address)
            });
            pad.last_failed = reading.is_none();
            if let Some(reading) = reading {
                // en charge, aucun niveau : on garde le dernier connu
                let known = reading.or_else(|| pad.value.and_then(|v| v.0));
                pad.value = Some((known, reading.is_none()));
                pad.answered = Some(Instant::now());
            }
        }
        pad.value.filter(|_| pad.answered.is_some_and(|t| t.elapsed() < Duration::from_secs(360)))
    });
    let Some((Some(percent), charging)) = value else { return };
    let mut s = Status::new(format!("gamesir-ble:{address}"), "GameSir Cyclone Pro", Kind::Gamepad);
    (s.percent, s.charging) = (Some(percent), charging);
    ctx.out.push(s);
}

pub fn poll(ctx: &mut Context) {
    let paired = paired_devices();
    if !paired.is_empty() {
        for step in [poll_airpods, poll_fast_pair, poll_buds] {
            if !ctx.out_of_time() {
                step(ctx, &paired);
            }
        }
    }
    if !ctx.out_of_time() {
        poll_gamesir(ctx);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn apple_advert() {
        let mut data = [0u8; 27];
        data[..8].copy_from_slice(&[0x07, 0x19, 0x01, 0x0E, 0x20, 0x20, 0x98, 0x15]);
        let found = airpods(&data).unwrap();
        assert_eq!(found.model, 0x200E);
        assert_eq!(found.parts, Parts { left: Some((80, true)), right: Some((90, false)), case: Some((50, false)) });
        assert_eq!(found.parts.summary(), Some((90, false)));
        assert_eq!(airpods(&data[..20]), None);
        assert_eq!(instance_address("7&abc&0&A1B2C3D4E5F6_C00000000"), Some(0xA1B2_C3D4_E5F6));
        assert_eq!(instance_address("x_C"), None);
    }

    #[test]
    fn fast_pair_and_buds() {
        let parts = fast_pair(&[0x00, 0x33, 0x40, 0xC1, 0x7F]).unwrap();
        assert_eq!((parts.left, parts.right, parts.case), (Some((64, false)), Some((65, true)), None));
        assert_eq!(fast_pair(&[0x00, 0x33, 0x40]), None);

        let body = [0x60u8, 0, 80, 70, 0, 0, 0, 55];
        let mut frame = vec![0xFD, body.len() as u8 + 2, 0];
        frame.extend_from_slice(&body);
        frame.extend_from_slice(&crc_ccitt(&body).to_le_bytes());
        frame.push(0xDD);
        let parts = galaxy(&frame, false).unwrap();
        assert_eq!((parts.left, parts.right, parts.case), (Some((80, false)), Some((70, false)), Some((55, false))));

        assert_eq!(crc_modbus(b"123456789"), 0x4B37);
        let mut frame = vec![0x55, 0x60, 0x01, 0x07, 0xC0, 5, 0, 1, 2, 2, 60, 3, 0x80 | 50];
        let crc = crc_modbus(&frame).to_le_bytes();
        frame.extend_from_slice(&crc);
        let parts = nothing(&frame).unwrap();
        assert_eq!((parts.left, parts.right), (Some((60, false)), Some((50, true))));
        assert!(is_nothing("CMF Buds Pro") && !is_nothing("Galaxy Buds2"));
    }

    #[test]
    fn gamesir_notification() {
        let mut p = [0u8; 19];
        p[18] = 70;
        assert_eq!(gamesir(&p), Some(Some(70)));
        p[18] = 0xFF;
        assert_eq!(gamesir(&p), Some(None));
        assert_eq!(gamesir(&p[1..]), None);
    }

    /// Essai réel sans fenêtre : `cargo test -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn real_paired_list() {
        println!("appareils appairés : {}", paired_devices().len());
    }
}
