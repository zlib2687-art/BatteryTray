//! Écoute passive des annonces Bluetooth LE (AirPods, Beats, Fast Pair). L'écoute n'existe que dans WinRT :
//! les quelques interfaces utiles sont appelées directement (numéros de méthode lus dans les métadonnées de
//! Windows), sans dépendance supplémentaire. Rien n'est émis.

use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicUsize, Ordering};
use windows_sys::core::GUID;
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

type Object = *mut c_void;

/// (adresse, force du signal, type de section, données).
pub type Listener = fn(u64, i16, u8, &[u8]);

const IID_UNKNOWN: u128 = 0x00000000_0000_0000_c000_000000000046;
const IID_AGILE: u128 = 0x94ea2b94_e9cc_49e0_c0ff_ee64ca8f5b90;
/// TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>
const IID_HANDLER: u128 = 0x90eb4eca_d465_5ea0_a61c_033c8c5ecef2;
const IID_WATCHER: u128 = 0xa6ac336f_f3d3_4297_8d6c_c81ea6623f40;
const IID_BYTE_ACCESS: u128 = 0x905a0fef_bc53_11df_8c49_001e4fc686da;
const WATCHER_CLASS: &str = "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher";

static WATCHER: AtomicUsize = AtomicUsize::new(0);
static LISTENER: AtomicUsize = AtomicUsize::new(0);

/// Entrée `index` de la table de méthodes d'un objet COM.
unsafe fn slot<F: Copy>(object: Object, index: usize) -> F {
    let table = *(object as *const *const *const c_void);
    std::mem::transmute_copy(&*table.add(index))
}

struct Com(Object);

impl Drop for Com {
    fn drop(&mut self) {
        unsafe {
            let release: unsafe extern "system" fn(Object) -> u32 = slot(self.0, 2);
            release(self.0);
        }
    }
}

impl Com {
    unsafe fn query(&self, iid: u128) -> Option<Com> {
        let query: unsafe extern "system" fn(Object, *const GUID, *mut Object) -> i32 = slot(self.0, 0);
        let mut out = null_mut();
        (query(self.0, &GUID::from_u128(iid), &mut out) >= 0 && !out.is_null()).then(|| Com(out))
    }

    /// Méthode qui rend une valeur par pointeur.
    unsafe fn get<T: Default>(&self, index: usize) -> Option<T> {
        let method: unsafe extern "system" fn(Object, *mut T) -> i32 = slot(self.0, index);
        let mut value = T::default();
        (method(self.0, &mut value) >= 0).then_some(value)
    }

    /// Méthode qui rend un objet.
    unsafe fn object(&self, index: usize) -> Option<Com> {
        let method: unsafe extern "system" fn(Object, *mut Object) -> i32 = slot(self.0, index);
        let mut out = null_mut();
        (method(self.0, &mut out) >= 0 && !out.is_null()).then(|| Com(out))
    }
}

// ---------------------------------------------------------------- Délégué « annonce reçue »

#[repr(C)]
struct HandlerTable {
    query: unsafe extern "system" fn(Object, *const GUID, *mut Object) -> i32,
    add_ref: unsafe extern "system" fn(Object) -> u32,
    release: unsafe extern "system" fn(Object) -> u32,
    invoke: unsafe extern "system" fn(Object, Object, Object) -> i32,
}

#[repr(C)]
struct Handler {
    table: &'static HandlerTable,
}

unsafe extern "system" fn handler_query(this: Object, iid: *const GUID, out: *mut Object) -> i32 {
    let g = &*iid;
    let asked = (u128::from(g.data1) << 96)
        | (u128::from(g.data2) << 80)
        | (u128::from(g.data3) << 64)
        | u128::from(u64::from_be_bytes(g.data4));
    if [IID_UNKNOWN, IID_AGILE, IID_HANDLER].contains(&asked) {
        *out = this;
        0
    } else {
        *out = null_mut();
        0x8000_4002_u32 as i32 // E_NOINTERFACE
    }
}

// objet statique : le compte de références est sans objet
unsafe extern "system" fn handler_ref(_: Object) -> u32 {
    1
}

unsafe extern "system" fn handler_invoke(_: Object, _sender: Object, args: Object) -> i32 {
    if !args.is_null() {
        // les arguments appartiennent à l'appelant : pas de libération
        let args = std::mem::ManuallyDrop::new(Com(args));
        received(&args);
    }
    0
}

static TABLE: HandlerTable = HandlerTable { query: handler_query, add_ref: handler_ref, release: handler_ref, invoke: handler_invoke };
static HANDLER: Handler = Handler { table: &TABLE };

unsafe fn received(args: &Com) -> Option<()> {
    let listener = LISTENER.load(Ordering::Acquire);
    if listener == 0 {
        return None;
    }
    let listener: Listener = std::mem::transmute::<usize, Listener>(listener);
    let rssi: i16 = args.get(6)?;
    let address: u64 = args.get(7)?;
    let sections = args.object(10)?.object(12)?;
    let count: u32 = sections.get(7)?;
    for index in 0..count.min(64) {
        let get_at: unsafe extern "system" fn(Object, u32, *mut Object) -> i32 = slot(sections.0, 6);
        let mut section = null_mut();
        if get_at(sections.0, index, &mut section) < 0 || section.is_null() {
            continue;
        }
        let section = Com(section);
        let kind: u8 = section.get(6)?;
        let buffer = section.object(8)?;
        let length: u32 = buffer.get(7)?;
        let access = buffer.query(IID_BYTE_ACCESS)?;
        let bytes: unsafe extern "system" fn(Object, *mut *const u8) -> i32 = slot(access.0, 3);
        let mut data = std::ptr::null();
        if bytes(access.0, &mut data) < 0 || data.is_null() || length > 1024 {
            continue;
        }
        listener(address, rssi, kind, std::slice::from_raw_parts(data, length as usize));
    }
    Some(())
}

// ---------------------------------------------------------------- Démarrage

unsafe fn create_watcher() -> Option<Com> {
    let library: Vec<u16> = "combase.dll\0".encode_utf16().collect();
    let combase = LoadLibraryW(library.as_ptr());
    if combase.is_null() {
        return None;
    }
    let find = |name: &[u8]| GetProcAddress(combase, name.as_ptr());
    let initialize: unsafe extern "system" fn(i32) -> i32 = std::mem::transmute(find(b"RoInitialize\0")?);
    let create_string: unsafe extern "system" fn(*const u16, u32, *mut Object) -> i32 =
        std::mem::transmute(find(b"WindowsCreateString\0")?);
    let delete_string: unsafe extern "system" fn(Object) -> i32 = std::mem::transmute(find(b"WindowsDeleteString\0")?);
    let activate: unsafe extern "system" fn(Object, *mut Object) -> i32 = std::mem::transmute(find(b"RoActivateInstance\0")?);

    initialize(1); // multithread ; déjà initialisé : sans effet
    let class: Vec<u16> = WATCHER_CLASS.encode_utf16().collect();
    let mut name = null_mut();
    if create_string(class.as_ptr(), class.len() as u32, &mut name) < 0 {
        return None;
    }
    let mut instance = null_mut();
    let status = activate(name, &mut instance);
    delete_string(name);
    if status < 0 || instance.is_null() {
        return None;
    }
    let watcher = Com(instance).query(IID_WATCHER)?;
    let add: unsafe extern "system" fn(Object, *const Handler, *mut i64) -> i32 = slot(watcher.0, 19);
    let mut token = 0;
    (add(watcher.0, &HANDLER, &mut token) >= 0).then_some(watcher)
}

unsafe fn start(watcher: Object) -> bool {
    let method: unsafe extern "system" fn(Object) -> i32 = slot(watcher, 17);
    method(watcher) >= 0
}

/// Démarre (ou relance) l'écoute ; vrai si elle vient de démarrer. Appelé par le seul fil de lecture.
pub fn ensure(listener: Listener) -> bool {
    LISTENER.store(listener as usize, Ordering::Release);
    unsafe {
        let existing = WATCHER.load(Ordering::Acquire) as Object;
        if !existing.is_null() {
            let watcher = std::mem::ManuallyDrop::new(Com(existing));
            // 3 : arrêtée ; 4 : interrompue
            return matches!(watcher.get::<i32>(10), Some(3 | 4)) && start(existing);
        }
        // radio absente ou refusée : aucune diffusion à lire
        let Some(watcher) = create_watcher() else { return false };
        if !start(watcher.0) {
            return false;
        }
        // l'écoute dure autant que l'application
        WATCHER.store(std::mem::ManuallyDrop::new(watcher).0 as usize, Ordering::Release);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    static SECTIONS: AtomicUsize = AtomicUsize::new(0);

    fn count(_: u64, _: i16, _: u8, _: &[u8]) {
        SECTIONS.fetch_add(1, Ordering::Relaxed);
    }

    /// Essai sur la radio réelle : `cargo test -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn real_radio() {
        let started = ensure(count);
        std::thread::sleep(std::time::Duration::from_secs(4));
        println!("écoute démarrée : {started} ; sections reçues : {}", SECTIONS.load(Ordering::Relaxed));
    }
}
