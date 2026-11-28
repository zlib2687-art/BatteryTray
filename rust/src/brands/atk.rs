//! Claviers VIA / PMO Hub, claviers ATK / VGN et casque ATK N9 (port série). Dérivé de BatteryHub (GPL-3.0),
//! src/drivers/ (via_dongle, atk_keyboard, atk_serial). Lecture seule.

use super::{clamp100, Context, Dev, Status};
use crate::devices::Kind;
use crate::hid::Info;
use std::cell::{Cell, RefCell};
use std::ptr::{null, null_mut};
use std::time::{Duration, Instant};
use windows_sys::Win32::Devices::Communication::*;
use windows_sys::Win32::Foundation::{CloseHandle, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, ReadFile, WriteFile, OPEN_EXISTING};
use windows_sys::Win32::System::Registry::*;

fn level(value: u8) -> Option<u8> {
    Some(value).filter(|&v| v <= 100)
}

fn keyboard(key: impl Into<String>, name: impl Into<String>, percent: Option<u8>) -> Status {
    let mut s = Status::new(key, name, Kind::Keyboard);
    s.percent = percent;
    s
}

// ---------------------------------------------------------------- VIA / PMO Hub

const VIA_VID: u16 = 0x36B0;
const DONGLE: u16 = 0x3002;
const BOARDS: [(u16, &str); 4] = [(0x3009, "WAVE 75"), (0x3004, "Aurora 80"), (0x302B, "PMO P75"), (0x3012, "PMO 80")];

fn board_name(pid: u16) -> &'static str {
    BOARDS.iter().find(|b| b.0 == pid).map_or("Wireless keyboard", |b| b.1)
}

fn raw_hid(c: &Info) -> bool {
    c.iface == 1 && c.usage_page == 0xFF60 && c.usage == 0x0061 && c.input_len == c.output_len && (c.output_len == 33 || c.output_len == 65)
}

/// Trame PMO ; l'octet 0 est l'identifiant de rapport.
pub fn pmo_frame(payload: usize, command: u8, len: u8) -> Vec<u8> {
    let mut f = vec![0; 1 + payload];
    (f[1], f[2], f[5]) = (0xAA, command, len);
    f
}

pub fn paired_pid(start: &[u8]) -> u16 {
    if start.len() > 12 && u16::from_le_bytes([start[9], start[10]]) == VIA_VID {
        u16::from_le_bytes([start[11], start[12]])
    } else {
        0
    }
}

pub fn via_name(product: &str) -> String {
    if product.contains("Dongle") {
        return "Wireless keyboard".into();
    }
    let mut name = product;
    for suffix in [" 2.4G", " Wireless"] {
        if let Some(rest) = name.strip_suffix(suffix).filter(|r| !r.is_empty()) {
            name = rest;
        }
    }
    if name.is_empty() { "Wireless keyboard".into() } else { name.into() }
}

fn ask(d: &Dev, request: &[u8], echo: &[u8], timeout: u64) -> Option<Vec<u8>> {
    d.flush();
    d.query(request, timeout, |r| r.len() >= echo.len() + 6 && r.starts_with(echo))
}

/// (niveau, identifiant du clavier appairé).
fn pmo_query(d: &Dev, output_len: usize) -> Option<(u8, u16)> {
    let payload = output_len - 1;
    let len = (payload - 8) as u8;
    let start_frame = pmo_frame(payload, 0x10, len);
    let end_frame = pmo_frame(payload, 0x11, len);
    let start_echo = [0xAA, 0x10, 0, 0];
    let end_echo = [0xAA, 0x11, 0, 0];
    let ok = |r: &[u8]| r.len() > 8 && r[7] == 0x55;
    // la liaison se réveille : un second essai
    let start = ask(d, &start_frame, &start_echo, 600).or_else(|| ask(d, &start_frame, &start_echo, 600));
    let Some(start) = start else {
        d.write(&end_frame);
        return None;
    };
    if !ok(&start) {
        ask(d, &end_frame, &end_echo, 300);
        return None;
    }
    let battery = ask(d, &pmo_frame(payload, 0x30, 0x18), &[0xAA, 0x30, 0, 0], 600);
    let found = battery.filter(|r| ok(r)).and_then(|r| level(r[8]));
    ask(d, &end_frame, &end_echo, 600); // on termine toujours la session
    found.map(|v| (v, paired_pid(&start)))
}

fn via_query(d: &Dev, output_len: usize) -> Option<u8> {
    let mut request = vec![0; output_len];
    (request[1], request[3]) = (0x08, 0x07);
    // la première requête peut être manquée pendant le réveil de la liaison
    let r = (0..2).find_map(|_| ask(d, &request, &[0x08, 0x00, 0x07], 600))?;
    level(r[3]).filter(|&v| v > 0)
}

#[derive(Clone, Copy, PartialEq)]
enum Proto {
    Unknown,
    Pmo,
    Via,
}

#[derive(Clone, Copy)]
struct State {
    proto: Proto,
    misses: u32,
    next_try: Option<Instant>,
    paired: u16,
}

const NEW_STATE: State = State { proto: Proto::Unknown, misses: 0, next_try: None, paired: 0 };

impl State {
    fn waiting(&self) -> bool {
        self.next_try.is_some_and(|t| Instant::now() < t)
    }

    fn missed(&mut self) {
        self.misses += 1;
        if self.misses >= 6 {
            self.proto = Proto::Unknown;
        }
        let seconds = if self.proto != Proto::Unknown { 20 } else { (30u64 << (self.misses - 1).min(4)).min(300) };
        self.next_try = Some(Instant::now() + Duration::from_secs(seconds));
    }

    fn answered(&mut self, proto: Proto) {
        (self.proto, self.misses, self.next_try) = (proto, 0, None);
    }
}

thread_local!(static STATES: RefCell<Vec<(u16, State)>> = const { RefCell::new(Vec::new()) });

fn load(pid: u16) -> State {
    STATES.with_borrow(|all| all.iter().find(|e| e.0 == pid).map_or(NEW_STATE, |e| e.1))
}

fn store(pid: u16, state: Option<State>) {
    STATES.with_borrow_mut(|all| {
        all.retain(|e| e.0 != pid);
        if let Some(s) = state {
            all.push((pid, s));
        }
    });
}

fn via_status(key_pid: u16, name: impl Into<String>, percent: u8) -> Status {
    // récepteur et câble partagent l'identifiant du clavier
    keyboard(format!("via:{VIA_VID:04X}:{key_pid:04X}"), name, Some(percent))
}

fn poll_cable(ctx: &mut Context, pid: u16) -> bool {
    if ctx.is_claimed(VIA_VID, pid) {
        return false;
    }
    let found = ctx.find(VIA_VID, pid, raw_hid);
    if found.is_empty() {
        store(pid, None); // débranché : on repart de zéro au retour
        return false;
    }
    let mut state = load(pid);
    if state.waiting() {
        return false;
    }
    for i in found {
        let c = &ctx.colls[i];
        let Some((percent, _)) = Dev::open(c).and_then(|d| pmo_query(&d, c.output_len)) else { continue };
        state.answered(Proto::Pmo);
        store(pid, Some(state));
        ctx.add(via_status(pid, board_name(pid), percent), VIA_VID, pid);
        return true;
    }
    state.missed();
    store(pid, Some(state));
    false
}

fn poll_dongle(ctx: &mut Context, cable_answered: bool) {
    if ctx.is_claimed(VIA_VID, DONGLE) {
        return;
    }
    let Some(&i) = ctx.find(VIA_VID, DONGLE, |c| raw_hid(c) && c.output_len == 33).first() else {
        store(DONGLE, None);
        return;
    };
    let mut state = load(DONGLE);
    if cable_answered && state.proto != Proto::Via {
        ctx.claim(VIA_VID, DONGLE);
        return;
    }
    if state.waiting() {
        return;
    }
    let c = &ctx.colls[i];
    let Some(d) = Dev::open(c) else { return };
    let via_first = state.proto == Proto::Via || (state.proto == Proto::Unknown && c.product.contains("URX"));
    let order = if via_first { [Proto::Via, Proto::Pmo] } else { [Proto::Pmo, Proto::Via] };
    let tries = if state.proto == Proto::Unknown { 2 } else { 1 }; // protocole connu : on ne demande que lui
    let mut found = None;
    for &proto in &order[..tries] {
        found = match proto {
            Proto::Pmo => pmo_query(&d, c.output_len).map(|(percent, paired)| {
                if paired != 0 {
                    state.paired = paired;
                }
                let key = if state.paired != 0 { state.paired } else { DONGLE };
                via_status(key, board_name(key), percent)
            }),
            _ => via_query(&d, c.output_len).map(|percent| via_status(DONGLE, via_name(&c.product), percent)),
        };
        if found.is_some() {
            state.answered(proto);
            break;
        }
    }
    match found {
        Some(s) => ctx.add(s, VIA_VID, DONGLE),
        None => state.missed(),
    }
    store(DONGLE, Some(state));
}

fn poll_via(ctx: &mut Context) {
    let mut cable = false;
    for (pid, _) in BOARDS {
        cable |= poll_cable(ctx, pid);
    }
    poll_dongle(ctx, cable);
}

// ---------------------------------------------------------------- Claviers ATK / VGN

const MODELS: [(&str, bool); 18] = [
    ("ATK V75X 2.4G Dongle", false), ("ATK V75X", false), ("VGN S99 2.4G Dongle", true), ("VGN S99", true),
    ("VGN V98pro V2 2.4G Dongle", true), ("VGN V98pro V2", true), ("VGN V98 PRO 2.4G Dongle", true), ("VGN V98pro", true),
    ("VGN N75 PRO 2.4G Dongle", true), ("VGN N75 PRO", true), ("VGN VXE75 V2 2.4G Dongle", true), ("VGN VXE75 V2", true),
    ("VGN VXE75 2.4G Dongle", true), ("VGN VXE75", true), ("VXE V87 2.4G Dongle", true), ("VXE V87", true), ("VXE V87 RGB", true),
    ("Wireless Gaming Keyboard", false), // nom générique du récepteur (Feker Alice80…)
];

pub fn find_model(product: &str) -> Option<(&'static str, bool)> {
    MODELS.into_iter().find(|m| m.0.eq_ignore_ascii_case(product.trim_end_matches(' ')))
}

pub fn atk_request(sequence: u16, vgn: bool) -> [u8; 32] {
    let mut r = [0u8; 32];
    let s = sequence.to_le_bytes();
    r[..5].copy_from_slice(&[0x04, s[0], s[1], 0x1A, if vgn { 0x06 } else { 0 }]);
    r
}

/// `Some(None)` : réponse à notre demande mais sans niveau exploitable.
pub fn atk_reply(r: &[u8], sequence: u16) -> Option<Option<(u8, bool)>> {
    if r.len() < 10 || r[0] != 0x04 || r[1..3] != sequence.to_le_bytes() || r[3] != 0x1A || r[5] != 0 || r[6] != 0 {
        return None;
    }
    Some((r[8] <= 100 && r[9] <= 2).then(|| (r[8], r[9] > 0)))
}

#[derive(Clone, Copy)]
struct Cached {
    asked: Option<Instant>,
    value: Option<(u8, bool)>,
}

const EMPTY: Cached = Cached { asked: None, value: None };
const RECEIVER_INTERVAL: Duration = Duration::from_secs(600);

thread_local! {
    static SEQUENCE: Cell<u16> = const { Cell::new(0) };
    static ATK: Cell<Cached> = const { Cell::new(EMPTY) };
    static Z87: Cell<(Option<Instant>, Option<(u8, bool)>)> = const { Cell::new((None, None)) };
}

fn atk_read(info: &Info, vgn: bool, timeout: u64) -> Option<(u8, bool)> {
    let sequence = SEQUENCE.get() % 0xFFFF + 1;
    SEQUENCE.set(sequence);
    let d = Dev::open(info)?;
    d.flush();
    let r = d.query(&atk_request(sequence, vgn), timeout, |r| atk_reply(r, sequence).is_some())?;
    atk_reply(&r, sequence).flatten()
}

fn atk_status(key: &str, name: &str, value: Option<(u8, bool)>) -> Status {
    let mut s = keyboard(key, name, value.map(|v| v.0));
    s.charging = value.is_some_and(|v| v.1);
    s.connected = value.is_some();
    s
}

fn poll_z87(ctx: &mut Context) {
    const VID: u16 = 0x373B;
    const PID: u16 = 0x1012;
    if ctx.is_claimed(VID, PID) {
        return;
    }
    let fits = |c: &Info| {
        c.usage_page == 0xFF1C && c.usage == 0x92 && c.input_len == 64 && c.output_len == 64
            && c.product.trim_end_matches(' ').eq_ignore_ascii_case("ATK Z87 Dongle")
    };
    let Some(&i) = ctx.find(VID, PID, fits).first() else {
        Z87.set((None, None)); // une reconnexion n'hérite pas d'une ancienne lecture
        return;
    };
    let (mut next, mut value) = Z87.get();
    if next.is_none_or(|t| Instant::now() >= t) {
        value = atk_read(&ctx.colls[i], false, 1000);
        next = Some(Instant::now() + if value.is_some() { RECEIVER_INTERVAL } else { Duration::from_secs(30) });
        Z87.set((next, value));
    }
    ctx.add(atk_status("atk_keyboard:1012", "ATK Z87", value), VID, PID);
}

fn poll_atk_keyboard(ctx: &mut Context) {
    const VID: u16 = 0x320F;
    const CABLE: u16 = 0x5055;
    const RECEIVER: u16 = 0x5088;
    poll_z87(ctx);
    // le câble l'emporte quand les deux sont branchés
    for pid in [CABLE, RECEIVER] {
        if ctx.is_claimed(VID, pid) {
            continue;
        }
        let cable = pid == CABLE;
        for i in ctx.find(VID, pid, |c| c.usage_page == 0xFF1C && c.usage == 0x92) {
            let Some((model, vgn)) = find_model(&ctx.colls[i].product) else { continue };
            let mut cached = ATK.get();
            // par le récepteur, on ne réveille le clavier que toutes les 10 minutes
            if cable || cached.asked.is_none_or(|t| t.elapsed() >= RECEIVER_INTERVAL) {
                let value = atk_read(&ctx.colls[i], vgn, 2500);
                cached = Cached { asked: Some(Instant::now()), value };
                ATK.set(cached);
            }
            let name = model.strip_suffix(" 2.4G Dongle").unwrap_or(model);
            ctx.add(atk_status("atk_keyboard:5088", name, cached.value), VID, pid);
            if cable {
                ctx.claim(VID, RECEIVER);
            }
            return;
        }
    }
}

// ---------------------------------------------------------------- Casque ATK N9 (port série)

const SERIAL_VID: u16 = 0x373B;
const SERIAL_MODELS: [(u16, &str); 4] = [(0x11CB, "ATK N9 Pro"), (0x11CA, "ATK N9 Pro"), (0x1204, "ATK N9 Ultra"), (0x1203, "ATK N9 Ultra")];

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

pub(super) struct Key(pub(super) HKEY);

impl Drop for Key {
    fn drop(&mut self) {
        unsafe { RegCloseKey(self.0) };
    }
}

pub(super) fn open_key(parent: HKEY, path: &str) -> Option<Key> {
    let mut key: HKEY = null_mut();
    let status = unsafe { RegOpenKeyExW(parent, wide(path).as_ptr(), 0, KEY_READ, &mut key) };
    (status == 0).then(|| Key(key))
}

/// Noms des sous-clés.
pub(super) fn subkeys(key: &Key) -> Vec<String> {
    let mut names = Vec::new();
    for index in 0..512 {
        let mut name = [0u16; 256];
        let mut len = name.len() as u32;
        let status = unsafe { RegEnumKeyExW(key.0, index, name.as_mut_ptr(), &mut len, null(), null_mut(), null_mut(), null_mut()) };
        if status != 0 {
            break;
        }
        names.push(String::from_utf16_lossy(&name[..len as usize]));
    }
    names
}

/// Ports « COMn » attribués par Windows à un produit USB.
fn ports(pid: u16) -> Vec<String> {
    let mut found = Vec::new();
    let path = format!(r"SYSTEM\CurrentControlSet\Enum\USB\VID_{SERIAL_VID:04X}&PID_{pid:04X}");
    let Some(product) = open_key(HKEY_LOCAL_MACHINE, &path) else { return found };
    for instance in subkeys(&product) {
        let Some(parameters) = open_key(product.0, &format!(r"{instance}\Device Parameters")) else { continue };
        let mut value = [0u16; 32];
        let mut size = size_of_val(&value) as u32;
        let status = unsafe {
            RegQueryValueExW(parameters.0, wide("PortName").as_ptr(), null(), null_mut(), value.as_mut_ptr() as *mut u8, &mut size)
        };
        if status == 0 {
            let port = String::from_utf16_lossy(&value[..(size as usize / 2).min(value.len())]);
            let port = port.trim_end_matches('\0');
            if port.starts_with("COM") {
                found.push(port.to_string());
            }
        }
    }
    found
}

pub fn serial_frame(command: u8, sub: u8) -> [u8; 64] {
    let mut f = [0u8; 64];
    (f[0], f[1], f[2]) = (2, command, sub);
    f
}

/// Données de la première trame de 64 octets qui répond à la commande.
pub fn serial_reply(buffer: &[u8], command: u8, sub: u8) -> Option<Vec<u8>> {
    buffer.chunks_exact(64).find(|f| f[1] == command && f[2] == sub).map(|f| f[3..].to_vec())
}

/// (connecté, niveau, en charge) d'après les réponses « état » et « batterie ».
pub fn serial_decode(status: &[u8], battery: Option<&[u8]>) -> (bool, Option<u8>, bool) {
    let connected = status.get(1) == Some(&1);
    match battery.and_then(|b| b.get(1)).filter(|_| connected) {
        Some(&b) => (true, Some(clamp100(i64::from(b & 0x7F))), b & 0x80 != 0),
        None => (connected, None, false),
    }
}

struct Serial(HANDLE);

impl Drop for Serial {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

impl Serial {
    /// 9600 bauds, 8N1, DTR et RTS actifs. Échoue si l'application ATK tient le port.
    fn open(port: &str) -> Option<Serial> {
        let path = wide(&format!(r"\\.\{port}"));
        unsafe {
            let h = CreateFileW(path.as_ptr(), GENERIC_READ | GENERIC_WRITE, 0, null(), OPEN_EXISTING, 0, null_mut());
            if h == INVALID_HANDLE_VALUE {
                return None;
            }
            let serial = Serial(h);
            let mut dcb: DCB = std::mem::zeroed();
            dcb.DCBlength = size_of::<DCB>() as u32;
            GetCommState(h, &mut dcb);
            (dcb.BaudRate, dcb.ByteSize, dcb.Parity, dcb.StopBits) = (9600, 8, 0, 0);
            dcb._bitfield = 1 | (1 << 4) | (1 << 12); // binaire, DTR, RTS, sans contrôle de flux
            let timeouts = COMMTIMEOUTS {
                ReadIntervalTimeout: 50,
                ReadTotalTimeoutMultiplier: 0,
                ReadTotalTimeoutConstant: 300,
                WriteTotalTimeoutMultiplier: 0,
                WriteTotalTimeoutConstant: 300,
            };
            if SetCommState(h, &dcb) == 0 || SetCommTimeouts(h, &timeouts) == 0 {
                return None;
            }
            PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR);
            Some(serial)
        }
    }

    fn command(&self, command: u8, sub: u8) -> Option<Vec<u8>> {
        let frame = serial_frame(command, sub);
        let mut written = 0;
        if unsafe { WriteFile(self.0, frame.as_ptr(), 64, &mut written, null_mut()) } == 0 || written != 64 {
            return None;
        }
        let mut buffer = Vec::new();
        let mut chunk = [0u8; 128];
        let end = Instant::now() + Duration::from_millis(800);
        while Instant::now() < end {
            let mut read = 0;
            if unsafe { ReadFile(self.0, chunk.as_mut_ptr(), chunk.len() as u32, &mut read, null_mut()) } == 0 {
                return None;
            }
            buffer.extend_from_slice(&chunk[..read as usize]);
            if let Some(data) = serial_reply(&buffer, command, sub) {
                return Some(data);
            }
        }
        None
    }
}

fn poll_atk_serial(ctx: &mut Context) {
    for (pid, name) in SERIAL_MODELS {
        for port in ports(pid) {
            if ctx.is_claimed(SERIAL_VID, pid) || ctx.out_of_time() {
                break;
            }
            // le registre garde les anciens ports : un port absent ne s'ouvre pas
            let Some(serial) = Serial::open(&port) else { continue };
            let Some(status) = serial.command(0x00, 0x04) else { continue };
            let battery = (status.get(1) == Some(&1)).then(|| serial.command(0x00, 0x03)).flatten();
            let mut s = Status::new(format!("atk-serial:{pid:04X}"), name, Kind::Headset);
            (s.connected, s.percent, s.charging) = serial_decode(&status, battery.as_deref());
            ctx.add(s, SERIAL_VID, pid);
        }
    }
}

pub fn poll(ctx: &mut Context) {
    for step in [poll_atk_keyboard, poll_atk_serial, poll_via] {
        if !ctx.out_of_time() {
            step(ctx);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames() {
        assert_eq!(pmo_frame(32, 0x10, 24)[..6], [0, 0xAA, 0x10, 0, 0, 24]);
        assert_eq!(paired_pid(&[0, 0, 0, 0, 0, 0, 0, 0, 0, 0xB0, 0x36, 0x09, 0x30]), 0x3009);
        assert_eq!(via_name("Foo 75 2.4G"), "Foo 75");
        assert_eq!(via_name("USB Dongle"), "Wireless keyboard");
        assert_eq!(find_model("vgn s99 "), Some(("VGN S99", true)));
        assert_eq!(atk_reply(&[4, 5, 0, 0x1A, 0, 0, 0, 0, 80, 1], 5), Some(Some((80, true))));
        assert_eq!(atk_reply(&[4, 5, 0, 0x1A, 0, 0, 0, 0, 200, 0], 5), Some(None));
        assert_eq!(atk_reply(&[4, 5, 0, 0x1A, 0, 0, 0, 0, 80, 1], 6), None);
        let mut reply = [0u8; 128];
        (reply[65], reply[66], reply[68]) = (0, 3, 0x80 | 55);
        let data = serial_reply(&reply, 0, 3).unwrap();
        assert_eq!(serial_decode(&[0, 1], Some(&data)), (true, Some(55), true));
        assert_eq!(serial_decode(&[0, 0], None), (false, None, false));
    }
}
