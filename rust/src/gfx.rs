//! Dessin GDI+ minimal : GDI+ n'est chargé que tant qu'une fenêtre est ouverte (mémoire).

use std::cell::Cell;
use std::ptr::{null, null_mut};
use windows_sys::core::GUID;
use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::Graphics::GdiPlus::*;

#[derive(Clone, Copy, Debug)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

pub const fn rect(x: f32, y: f32, w: f32, h: f32) -> Rect {
    Rect { x, y, w, h }
}

impl Rect {
    pub fn contains(&self, x: f32, y: f32) -> bool {
        x >= self.x && x < self.x + self.w && y >= self.y && y < self.y + self.h
    }
}

/// Couleurs du thème clair ou sombre (0xAARRGGBB).
pub struct Theme {
    pub back: u32,
    pub nav: u32,
    pub card: u32,
    pub hover: u32,
    pub border: u32,
    pub text: u32,
    pub sub: u32,
    pub accent: u32,
    pub on_accent: u32,
    pub green: u32,
    pub dim: u32,
}

pub fn theme(light: bool) -> Theme {
    if light {
        Theme {
            back: 0xFFF3F3F3, nav: 0xFFEAEAEA, card: 0xFFFFFFFF, hover: 0xFFF7F9FC, border: 0x22000000, text: 0xFF1C1C1C,
            sub: 0xFF606060, accent: 0xFF005FB8, on_accent: 0xFFFFFFFF, green: 0xFF0F7B0F, dim: 0xFF7D7D7D,
        }
    } else {
        Theme {
            back: 0xFF202020, nav: 0xFF1A1A1A, card: 0xFF2D2D2D, hover: 0xFF383838, border: 0x2EFFFFFF, text: 0xFFFFFFFF,
            sub: 0xFFBEBEBE, accent: 0xFF60CDFF, on_accent: 0xFF000000, green: 0xFF4CD964, dim: 0xFF7D7D7D,
        }
    }
}

#[derive(Clone, Copy)]
pub enum Align {
    Left,
    Center,
    Right,
}

thread_local!(static USERS: Cell<(u32, usize)> = const { Cell::new((0, 0)) });

/// À appeler quand une fenêtre s'ouvre ; `release` quand elle se ferme.
pub fn acquire() {
    USERS.with(|u| {
        let (mut count, mut token) = u.get();
        if count == 0 {
            let mut input: GdiplusStartupInput = unsafe { std::mem::zeroed() };
            input.GdiplusVersion = 1;
            unsafe { GdiplusStartup(&mut token, &input, null_mut()) };
        }
        count += 1;
        u.set((count, token));
    });
}

pub fn release() {
    USERS.with(|u| {
        let (count, token) = u.get();
        if count == 1 {
            unsafe { GdiplusShutdown(token) };
        }
        u.set((count.saturating_sub(1), token));
    });
}

pub struct Canvas {
    g: *mut GpGraphics,
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

impl Canvas {
    unsafe fn new(g: *mut GpGraphics, scale: f32) -> Canvas {
        GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
        GdipSetTextRenderingHint(g, TextRenderingHintClearTypeGridFit);
        GdipScaleWorldTransform(g, scale, scale, MatrixOrderPrepend);
        Canvas { g }
    }

    unsafe fn path(&self, r: Rect, radius: f32) -> *mut GpPath {
        let mut path = null_mut();
        GdipCreatePath(FillModeAlternate, &mut path);
        let d = (radius * 2.0).min(r.w).min(r.h).max(0.1);
        GdipAddPathArc(path, r.x, r.y, d, d, 180.0, 90.0);
        GdipAddPathArc(path, r.x + r.w - d, r.y, d, d, 270.0, 90.0);
        GdipAddPathArc(path, r.x + r.w - d, r.y + r.h - d, d, d, 0.0, 90.0);
        GdipAddPathArc(path, r.x, r.y + r.h - d, d, d, 90.0, 90.0);
        GdipClosePathFigure(path);
        path
    }

    pub fn fill(&self, r: Rect, radius: f32, color: u32) {
        unsafe {
            let mut brush = null_mut();
            GdipCreateSolidFill(color, &mut brush);
            let path = self.path(r, radius);
            GdipFillPath(self.g, brush.cast(), path);
            GdipDeletePath(path);
            GdipDeleteBrush(brush.cast());
        }
    }

    fn with_pen(&self, color: u32, width: f32, draw: impl FnOnce(*mut GpPen)) {
        unsafe {
            let mut pen = null_mut();
            GdipCreatePen1(color, width, UnitPixel, &mut pen);
            draw(pen);
            GdipDeletePen(pen);
        }
    }

    pub fn stroke(&self, r: Rect, radius: f32, color: u32, width: f32) {
        self.with_pen(color, width, |pen| unsafe {
            let path = self.path(r, radius);
            GdipDrawPath(self.g, pen, path);
            GdipDeletePath(path);
        });
    }

    pub fn line(&self, from: (f32, f32), to: (f32, f32), color: u32, width: f32) {
        self.with_pen(color, width, |pen| unsafe {
            GdipDrawLine(self.g, pen, from.0, from.1, to.0, to.1);
        });
    }

    pub fn arc(&self, r: Rect, start: f32, sweep: f32, color: u32, width: f32) {
        self.with_pen(color, width, |pen| unsafe {
            GdipDrawArc(self.g, pen, r.x, r.y, r.w, r.h, start, sweep);
        });
    }

    fn text_with(&self, family: &str, r: Rect, text: &str, size: f32, color: u32, align: Align) {
        unsafe {
            let name = wide(family);
            let mut font_family = null_mut();
            if GdipCreateFontFamilyFromName(name.as_ptr(), null_mut(), &mut font_family) != 0 {
                let fallback = wide("Segoe UI");
                GdipCreateFontFamilyFromName(fallback.as_ptr(), null_mut(), &mut font_family);
            }
            let mut font = null_mut();
            GdipCreateFont(font_family, size, 0, UnitPixel, &mut font);
            let mut format = null_mut();
            GdipCreateStringFormat(StringFormatFlagsNoWrap, 0, &mut format);
            let alignment = match align {
                Align::Left => StringAlignmentNear,
                Align::Center => StringAlignmentCenter,
                Align::Right => StringAlignmentFar,
            };
            GdipSetStringFormatAlign(format, alignment);
            GdipSetStringFormatLineAlign(format, StringAlignmentCenter);
            GdipSetStringFormatTrimming(format, StringTrimmingEllipsisCharacter);
            let mut brush = null_mut();
            GdipCreateSolidFill(color, &mut brush);
            let layout = RectF { X: r.x, Y: r.y, Width: r.w, Height: r.h };
            let text = wide(text);
            GdipDrawString(self.g, text.as_ptr(), -1, font, &layout, format, brush.cast());
            GdipDeleteBrush(brush.cast());
            GdipDeleteStringFormat(format);
            GdipDeleteFont(font);
            GdipDeleteFontFamily(font_family);
        }
    }

    pub fn text(&self, r: Rect, text: &str, size: f32, color: u32, align: Align) {
        self.text_with("Segoe UI", r, text, size, color, align);
    }

    pub fn strong(&self, r: Rect, text: &str, size: f32, color: u32, align: Align) {
        self.text_with("Segoe UI Semibold", r, text, size, color, align);
    }

    /// Icône de la police système de Windows 11.
    pub fn symbol(&self, r: Rect, symbol: char, size: f32, color: u32) {
        self.text_with("Segoe Fluent Icons", r, &symbol.to_string(), size, color, Align::Center);
    }
}

/// Dessine dans la fenêtre pendant WM_PAINT, via une image en mémoire (pas de scintillement).
pub fn paint(window: HWND, scale: f32, draw: impl FnOnce(&Canvas)) {
    unsafe {
        let mut ps: PAINTSTRUCT = std::mem::zeroed();
        let screen = BeginPaint(window, &mut ps);
        let mut client = std::mem::zeroed();
        windows_sys::Win32::UI::WindowsAndMessaging::GetClientRect(window, &mut client);
        let memory = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, client.right.max(1), client.bottom.max(1));
        let old = SelectObject(memory, bitmap);
        let mut g = null_mut();
        if GdipCreateFromHDC(memory, &mut g) == 0 {
            draw(&Canvas::new(g, scale));
            GdipDeleteGraphics(g);
        }
        BitBlt(screen, 0, 0, client.right, client.bottom, memory, 0, 0, SRCCOPY);
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        EndPaint(window, &ps);
    }
}

/// Rejoue un dessin sur une image (sans rien afficher), pour connaître les zones cliquables.
pub fn on_image(bitmap: *mut GpBitmap, draw: impl FnOnce(&Canvas)) {
    unsafe {
        let mut g = null_mut();
        if GdipGetImageGraphicsContext(bitmap.cast(), &mut g) == 0 {
            draw(&Canvas::new(g, 1.0));
            GdipDeleteGraphics(g);
        }
    }
}

/// Dessine dans un fichier PNG (aperçus de vérification).
pub fn render_png(path: &str, width: f32, height: f32, scale: f32, draw: impl FnOnce(&Canvas)) -> bool {
    const PNG: GUID = GUID::from_u128(0x557cf406_1a04_11d3_9a73_0000f81ef32e);
    const ARGB32: i32 = 0x26200A;
    acquire();
    let ok = unsafe {
        let mut bitmap = null_mut();
        GdipCreateBitmapFromScan0((width * scale) as i32, (height * scale) as i32, 0, ARGB32, null(), &mut bitmap);
        let mut g = null_mut();
        GdipGetImageGraphicsContext(bitmap.cast(), &mut g);
        draw(&Canvas::new(g, scale));
        GdipDeleteGraphics(g);
        let file = wide(path);
        let status = GdipSaveImageToFile(bitmap.cast(), file.as_ptr(), &PNG, null());
        GdipDisposeImage(bitmap.cast());
        status == 0
    };
    release();
    ok
}
