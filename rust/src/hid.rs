//! Accès HID minimal : énumération, rapports avec délai maximal, rapports de fonctionnalité.

use std::ptr::{null, null_mut};
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::*;
use windows_sys::Win32::Devices::HumanInterfaceDevice::*;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Storage::FileSystem::*;
use windows_sys::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
use windows_sys::Win32::System::IO::{CancelIo, GetOverlappedResult, OVERLAPPED};

pub struct Info {
    path: Vec<u16>,
    pub path_text: String,
    pub vid: u16,
    pub pid: u16,
    pub usage_page: u16,
    pub input_len: usize,
    pub output_len: usize,
    pub feature_len: usize,
    pub product: String,
}

pub struct Handle(HANDLE);

impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

fn create(path: &[u16], access: u32, flags: u32) -> Option<Handle> {
    let h = unsafe {
        CreateFileW(path.as_ptr(), access, FILE_SHARE_READ | FILE_SHARE_WRITE, null(), OPEN_EXISTING, flags, null_mut())
    };
    (h != INVALID_HANDLE_VALUE).then(|| Handle(h))
}

/// Périphériques HID présents appartenant à l'un des fabricants demandés.
pub fn enumerate(vendors: &[u16]) -> Vec<Info> {
    let mut found = Vec::new();
    unsafe {
        let mut guid = std::mem::zeroed();
        HidD_GetHidGuid(&mut guid);
        let set = SetupDiGetClassDevsW(&guid, null(), null_mut(), DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if set == -1 {
            return found;
        }
        let mut index = 0;
        loop {
            let mut data: SP_DEVICE_INTERFACE_DATA = std::mem::zeroed();
            data.cbSize = size_of::<SP_DEVICE_INTERFACE_DATA>() as u32;
            if SetupDiEnumDeviceInterfaces(set, null(), &guid, index, &mut data) == 0 {
                break;
            }
            index += 1;

            let mut buf = [0u64; 256]; // tampon aligné
            let detail = buf.as_mut_ptr() as *mut SP_DEVICE_INTERFACE_DETAIL_DATA_W;
            (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;
            let size = size_of_val(&buf) as u32;
            if SetupDiGetDeviceInterfaceDetailW(set, &data, detail, size, null_mut(), null_mut()) == 0 {
                continue;
            }
            let start = std::ptr::addr_of!((*detail).DevicePath) as *const u16;
            let mut len = 0;
            while *start.add(len) != 0 {
                len += 1;
            }
            let path: Vec<u16> = std::slice::from_raw_parts(start, len + 1).to_vec();
            if let Some(info) = describe(path, vendors) {
                found.push(info);
            }
        }
        SetupDiDestroyDeviceInfoList(set);
    }
    found
}

unsafe fn describe(path: Vec<u16>, vendors: &[u16]) -> Option<Info> {
    // accès nul : suffit pour interroger, même les souris que Windows interdit en lecture
    let h = create(&path, 0, 0)?;
    let mut attr: HIDD_ATTRIBUTES = std::mem::zeroed();
    attr.Size = size_of::<HIDD_ATTRIBUTES>() as u32;
    if HidD_GetAttributes(h.0, &mut attr) == 0 || !vendors.contains(&attr.VendorID) {
        return None;
    }
    let mut pre: PHIDP_PREPARSED_DATA = std::mem::zeroed();
    if HidD_GetPreparsedData(h.0, &mut pre) == 0 {
        return None;
    }
    let mut caps: HIDP_CAPS = std::mem::zeroed();
    let status = HidP_GetCaps(pre, &mut caps);
    HidD_FreePreparsedData(pre);
    if status != HIDP_STATUS_SUCCESS {
        return None;
    }
    let mut name = [0u16; 128];
    HidD_GetProductString(h.0, name.as_mut_ptr() as _, (size_of_val(&name) - 2) as u32);
    let end = name.iter().position(|&c| c == 0).unwrap_or(0);
    Some(Info {
        path_text: String::from_utf16_lossy(&path[..path.len() - 1]).to_lowercase(),
        vid: attr.VendorID,
        pid: attr.ProductID,
        usage_page: caps.UsagePage,
        input_len: caps.InputReportByteLength as usize,
        output_len: caps.OutputReportByteLength as usize,
        feature_len: caps.FeatureReportByteLength as usize,
        product: String::from_utf16_lossy(&name[..end]),
        path,
    })
}

/// Canal ouvert en lecture/écriture avec délais (un récepteur muet ne bloque jamais).
pub struct Stream {
    handle: Handle,
    event: Handle,
    input_len: usize,
    output_len: usize,
}

impl Stream {
    pub fn open(info: &Info) -> Option<Stream> {
        let handle = create(&info.path, GENERIC_READ | GENERIC_WRITE, FILE_FLAG_OVERLAPPED)?;
        let event = unsafe { CreateEventW(null(), 1, 0, null()) };
        if event.is_null() {
            return None;
        }
        Some(Stream { handle, event: Handle(event), input_len: info.input_len, output_len: info.output_len })
    }

    unsafe fn finish(&self, started: i32, ov: &mut OVERLAPPED, timeout_ms: u32) -> Option<usize> {
        if started == 0 && GetLastError() != ERROR_IO_PENDING {
            return None;
        }
        let mut done = 0u32;
        if WaitForSingleObject(self.event.0, timeout_ms) != WAIT_OBJECT_0 {
            CancelIo(self.handle.0);
            // attend la fin réelle de l'annulation : le tampon ne doit plus être utilisé par le pilote
            GetOverlappedResult(self.handle.0, ov, &mut done, 1);
            return None;
        }
        (GetOverlappedResult(self.handle.0, ov, &mut done, 0) != 0).then_some(done as usize)
    }

    pub fn write(&self, data: &[u8]) -> bool {
        let mut buf = vec![0u8; self.output_len.max(data.len())];
        buf[..data.len()].copy_from_slice(data);
        unsafe {
            let mut ov: OVERLAPPED = std::mem::zeroed();
            ov.hEvent = self.event.0;
            let started = WriteFile(self.handle.0, buf.as_ptr(), buf.len() as u32, null_mut(), &mut ov);
            self.finish(started, &mut ov, 1000).is_some()
        }
    }

    pub fn read(&self, timeout_ms: u32) -> Option<Vec<u8>> {
        let mut buf = vec![0u8; self.input_len.max(64)];
        unsafe {
            let mut ov: OVERLAPPED = std::mem::zeroed();
            ov.hEvent = self.event.0;
            let started = ReadFile(self.handle.0, buf.as_mut_ptr(), buf.len() as u32, null_mut(), &mut ov);
            let n = self.finish(started, &mut ov, timeout_ms)?;
            buf.truncate(n);
        }
        Some(buf)
    }
}

/// Envoie un rapport de fonctionnalité puis lit la réponse (protocole Razer).
pub fn feature_exchange(info: &Info, request: &[u8]) -> Option<Vec<u8>> {
    let h = create(&info.path, 0, 0)?;
    unsafe {
        if HidD_SetFeature(h.0, request.as_ptr() as _, request.len() as u32) == 0 {
            return None;
        }
        std::thread::sleep(std::time::Duration::from_millis(80));
        let mut response = vec![0u8; request.len()];
        (HidD_GetFeature(h.0, response.as_mut_ptr() as _, response.len() as u32) != 0).then_some(response)
    }
}
