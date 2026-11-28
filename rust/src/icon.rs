//! Icône de la barre : pictogramme en haut, pourcentage en bas, dessinés pixel par pixel sur une grille de 16 × 16.

use crate::devices::Kind;
use std::ptr::{null_mut};
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const DIGITS: [[u8; 7]; 10] = [
    [7, 5, 5, 5, 5, 5, 7],
    [2, 6, 2, 2, 2, 2, 7],
    [7, 1, 1, 7, 4, 4, 7],
    [7, 1, 1, 7, 1, 1, 7],
    [5, 5, 5, 7, 1, 1, 1],
    [7, 4, 4, 7, 1, 1, 7],
    [7, 4, 4, 7, 5, 5, 7],
    [7, 1, 1, 2, 2, 2, 2],
    [7, 5, 5, 7, 5, 5, 7],
    [7, 5, 5, 7, 1, 1, 7],
];

const MOUSE: [&str; 7] = ["..###..", ".#.#.#.", ".#.#.#.", ".#####.", ".#...#.", ".#...#.", "..###.."];
const HEADSET: [&str; 7] = ["..#####..", ".#.....#.", "#.......#", "##.....##", "##.....##", "##.....##", "........."];

const KEYBOARD: [&str; 7] = [".........", "#########", "#.#.#.#.#", "#########", "#..###..#", "#########", "........."];
const GAMEPAD: [&str; 7] = [".........", ".##...##.", "#########", "#.#####.#", "#########", "##.....##", "........."];
const MICROPHONE: [&str; 7] = ["..###..", "..###..", "#.###.#", "#.###.#", ".#...#.", "..###..", "...#..."];
const OTHER: [&str; 7] = [".#####.", ".#...#.", ".#...#.", ".#...#.", ".#...#.", ".#...#.", ".#####."];

/// Grille 16 × 16 : vrai = pixel allumé.
pub fn grid(kind: Kind, percent: u8) -> [[bool; 16]; 16] {
    let mut g = [[false; 16]; 16];
    let art: &[&str] = match kind {
        Kind::Mouse => &MOUSE,
        Kind::Headset => &HEADSET,
        Kind::Keyboard => &KEYBOARD,
        Kind::Gamepad => &GAMEPAD,
        Kind::Microphone => &MICROPHONE,
        Kind::Other => &OTHER,
    };
    let left = (16 - art[0].len()) / 2;
    for (y, row) in art.iter().enumerate() {
        for (x, c) in row.bytes().enumerate() {
            g[y][left + x] = c == b'#';
        }
    }
    let text = percent.min(100).to_string();
    let width = text.len() * 4 - 1;
    let mut x = (16 - width) / 2;
    for digit in text.bytes() {
        for (y, bits) in DIGITS[usize::from(digit - b'0')].iter().enumerate() {
            for column in 0..3 {
                if bits & (4 >> column) != 0 {
                    g[9 + y][x + column] = true;
                }
            }
        }
        x += 4;
    }
    g
}

/// Crée l'icône Windows (à détruire avec DestroyIcon). `rgb` = 0xRRGGBB.
pub fn create(kind: Kind, percent: u8, rgb: u32) -> HICON {
    let size = unsafe { GetSystemMetrics(SM_CXSMICON) }.clamp(16, 64);
    let pattern = grid(kind, percent);
    unsafe {
        let mut info: BITMAPINFO = std::mem::zeroed();
        info.bmiHeader.biSize = size_of::<BITMAPINFOHEADER>() as u32;
        info.bmiHeader.biWidth = size;
        info.bmiHeader.biHeight = -size; // de haut en bas
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        let mut bits: *mut core::ffi::c_void = null_mut();
        let color = CreateDIBSection(null_mut(), &info, DIB_RGB_COLORS, &mut bits, null_mut(), 0);
        if color.is_null() || bits.is_null() {
            return null_mut();
        }
        let pixels = std::slice::from_raw_parts_mut(bits as *mut u32, (size * size) as usize);
        for y in 0..size {
            for x in 0..size {
                let lit = pattern[(y * 16 / size) as usize][(x * 16 / size) as usize];
                pixels[(y * size + x) as usize] = if lit { 0xFF00_0000 | rgb } else { 0 };
            }
        }
        let mask_bits = vec![0u8; (((size + 15) / 16) * 2 * size) as usize];
        let mask = CreateBitmap(size, size, 1, 1, mask_bits.as_ptr() as _);
        let icon_info = ICONINFO { fIcon: 1, xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: color };
        let icon = CreateIconIndirect(&icon_info);
        DeleteObject(color);
        DeleteObject(mask);
        icon
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_level_fits() {
        for p in 0..=100 {
            assert!(grid(Kind::Mouse, p)[9..].iter().flatten().any(|&b| b));
            assert!(grid(Kind::Headset, p)[0].iter().any(|&b| b));
        }
    }
}
