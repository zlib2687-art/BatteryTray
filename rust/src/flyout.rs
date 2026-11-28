//! Panneau « Périphériques » ouvert au clic : une carte par appareil, mini-graphique au clic, engrenage.

use crate::autonomy::Sample;
use crate::devices::Kind;
use crate::gfx::{rect, theme, Align, Canvas, Rect};
use crate::i18n;

pub const WIDTH: f32 = 312.0;
const HEADER: f32 = 42.0;
const ROW: f32 = 62.0;
const PAD: f32 = 12.0;
const CHART: f32 = 66.0;
const EXTRA: f32 = 15.0;

pub struct Row {
    pub id: String,
    pub name: String,
    pub kind: Kind,
    pub percent: u8,
    pub estimated: bool,
    pub offline: bool,
    pub charging: bool,
    pub line: String,
    /// Troisième ligne facultative (DPI et fréquence), vide sinon.
    pub extra: String,
    /// Couleur d'état (barre et pastille), 0xAARRGGBB.
    pub status: u32,
}

pub struct Chart {
    /// Niveaux depuis la dernière charge, avec l'heure locale du premier et du dernier.
    pub samples: Vec<Sample>,
    pub first: String,
    pub last: String,
}

pub struct Model {
    pub rows: Vec<Row>,
    pub light: bool,
    pub expanded: Option<(String, Chart)>,
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Hit {
    Gear,
    Row(usize),
}

fn expanded_index(m: &Model) -> Option<usize> {
    let (id, _) = m.expanded.as_ref()?;
    m.rows.iter().position(|r| &r.id == id)
}

fn row_height(row: &Row) -> f32 {
    ROW + if row.extra.is_empty() { 0.0 } else { EXTRA }
}

pub fn height(m: &Model) -> f32 {
    let rows = if m.rows.is_empty() { ROW } else { m.rows.iter().map(row_height).sum() };
    HEADER + rows + PAD + if expanded_index(m).is_some() { CHART } else { 0.0 }
}

fn gear() -> Rect {
    rect(WIDTH - PAD - 30.0, 6.0, 30.0, 30.0)
}

fn card(m: &Model, i: usize) -> Rect {
    let open = expanded_index(m);
    let shift = if open.is_some_and(|e| i > e) { CHART } else { 0.0 };
    let chart = if open == Some(i) { CHART } else { 0.0 };
    let above: f32 = m.rows[..i].iter().map(row_height).sum();
    rect(PAD, HEADER + above + shift, WIDTH - 2.0 * PAD, row_height(&m.rows[i]) - 8.0 + chart)
}

pub fn hit(m: &Model, x: f32, y: f32) -> Option<Hit> {
    if gear().contains(x, y) {
        return Some(Hit::Gear);
    }
    (0..m.rows.len()).find(|&i| card(m, i).contains(x, y)).map(Hit::Row)
}

fn pictogram(c: &Canvas, kind: Kind, tile: Rect, color: u32) {
    let (cx, cy) = (tile.x + tile.w / 2.0, tile.y + tile.h / 2.0);
    match kind {
        Kind::Mouse => {
            c.stroke(rect(cx - 7.0, cy - 11.0, 14.0, 22.0), 7.0, color, 1.6);
            c.line((cx, cy - 7.0), (cx, cy - 2.0), color, 1.8);
        }
        Kind::Headset => {
            c.arc(rect(cx - 11.0, cy - 11.0, 22.0, 22.0), 180.0, 180.0, color, 1.8);
            c.fill(rect(cx - 12.0, cy - 1.0, 6.0, 11.0), 2.5, color);
            c.fill(rect(cx + 6.0, cy - 1.0, 6.0, 11.0), 2.5, color);
        }
        Kind::Keyboard => {
            c.stroke(rect(cx - 13.0, cy - 7.0, 26.0, 15.0), 3.0, color, 1.5);
            for k in 0..4 {
                c.fill(rect(cx - 9.5 + k as f32 * 5.5, cy - 3.0, 2.5, 2.5), 0.5, color);
            }
            c.line((cx - 6.0, cy + 4.0), (cx + 6.0, cy + 4.0), color, 1.6);
        }
        Kind::Gamepad => {
            c.stroke(rect(cx - 13.0, cy - 7.0, 26.0, 15.0), 7.0, color, 1.6);
            c.line((cx - 9.0, cy), (cx - 3.0, cy), color, 1.6);
            c.line((cx - 6.0, cy - 3.0), (cx - 6.0, cy + 3.0), color, 1.6);
            c.fill(rect(cx + 4.0, cy - 3.0, 3.0, 3.0), 1.5, color);
            c.fill(rect(cx + 7.0, cy + 1.0, 3.0, 3.0), 1.5, color);
        }
        Kind::Microphone => {
            c.fill(rect(cx - 4.0, cy - 12.0, 8.0, 15.0), 4.0, color);
            c.arc(rect(cx - 8.0, cy - 8.0, 16.0, 15.0), 0.0, 180.0, color, 1.6);
            c.line((cx, cy + 7.0), (cx, cy + 12.0), color, 1.6);
        }
        Kind::Other => {
            c.stroke(rect(cx - 7.0, cy - 11.0, 14.0, 22.0), 3.0, color, 1.6);
        }
    }
}

pub fn draw(c: &Canvas, m: &Model, hover: Option<Hit>) {
    let t = theme(m.light);
    c.fill(rect(-2.0, -2.0, WIDTH + 4.0, height(m) + 4.0), 0.0, t.back);
    c.strong(rect(PAD + 4.0, 10.0, 220.0, 24.0), i18n::t("app.title"), 15.0, t.text, Align::Left);
    if hover == Some(Hit::Gear) {
        c.fill(gear(), 6.0, t.hover);
    }
    c.symbol(gear(), '\u{E713}', 15.0, t.sub);

    if m.rows.is_empty() {
        c.text(rect(PAD + 4.0, HEADER + 8.0, WIDTH - 2.0 * PAD, 24.0), i18n::t("flyout.empty"), 11.0, t.sub, Align::Left);
        return;
    }

    let open = expanded_index(m);
    for (i, row) in m.rows.iter().enumerate() {
        let area = card(m, i);
        let y = area.y;
        let fg = if row.offline { t.dim } else { t.text };
        let plugged = row.charging && !row.offline;

        c.fill(area, 8.0, if hover == Some(Hit::Row(i)) { t.hover } else { t.card });
        c.stroke(area, 8.0, t.border, 1.0);

        let tile = rect(PAD + 10.0, y + 9.0, 36.0, 36.0);
        c.fill(tile, 9.0, (row.status & 0x00FF_FFFF) | if row.offline { 0x1C00_0000 } else { 0x3400_0000 });
        pictogram(c, row.kind, tile, fg);

        c.strong(rect(PAD + 53.0, y + 6.0, 162.0, 20.0), &row.name, 12.5, fg, Align::Left);
        let line_color = if plugged { t.green } else if row.offline { t.dim } else { t.sub };
        c.text(rect(PAD + 53.0, y + 25.0, WIDTH - 2.0 * PAD - 63.0, 17.0), &row.line, 10.5, line_color, Align::Left);

        let percent = format!("{}{} %", if row.estimated { "~" } else { "" }, row.percent);
        let color = if row.offline { t.dim } else if plugged || row.percent < 40 { row.status } else { t.text };
        c.strong(rect(WIDTH - PAD - 90.0, y + 4.0, 82.0, 26.0), &percent, 18.0, color, Align::Right);

        let mut bar = y + 44.0;
        if !row.extra.is_empty() {
            c.text(rect(PAD + 53.0, y + 41.0, WIDTH - 2.0 * PAD - 63.0, 15.0), &row.extra, 10.5, t.sub, Align::Left);
            bar += EXTRA;
        }
        let track = rect(PAD + 56.0, bar, WIDTH - 2.0 * PAD - 68.0, 4.0);
        c.fill(track, 2.0, (t.sub & 0x00FF_FFFF) | 0x3800_0000);
        let fill = (track.w * f32::from(row.percent) / 100.0).max(4.0);
        c.fill(rect(track.x, track.y, fill, track.h), 2.0, row.status);

        if open == Some(i) {
            if let Some((_, chart)) = &m.expanded {
                draw_chart(c, chart, y, row, m.light);
            }
        }
    }
}

/// Échelle adaptée : bornes à la dizaine, 20 points d'écart au minimum.
pub fn range(samples: &[Sample]) -> (u8, u8) {
    let Some(min) = samples.iter().map(|s| s.p).min() else { return (0, 100) };
    let max = samples.iter().map(|s| s.p).max().unwrap_or(min);
    let mut low = (i32::from(min) - 3).div_euclid(10).max(0) * 10;
    let mut high = ((i32::from(max) + 3 + 9) / 10 * 10).min(100);
    while high - low < 20 {
        if high < 100 {
            high += 10;
        } else {
            low -= 10;
        }
    }
    (low as u8, high as u8)
}

fn draw_chart(c: &Canvas, chart: &Chart, top: f32, row: &Row, light: bool) {
    let t = theme(light);
    let area = rect(PAD + 8.0, top + row_height(row) - 2.0, WIDTH - 2.0 * (PAD + 8.0), CHART - 28.0);
    let grid = (t.sub & 0x00FF_FFFF) | 0x4600_0000;
    for k in 0..3 {
        let y = area.y + area.h * k as f32 / 2.0;
        c.line((area.x, y), (area.x + area.w, y), grid, 1.0);
    }
    let samples = &chart.samples;
    if samples.len() < 2 {
        c.text(area, i18n::t("chart.none"), 10.5, t.sub, Align::Center);
        return;
    }
    let (low, high) = range(samples);
    c.text(rect(area.x + area.w - 44.0, area.y, 42.0, 12.0), &format!("{high} %"), 9.5, t.sub, Align::Right);
    c.text(rect(area.x + area.w - 44.0, area.y + area.h - 13.0, 42.0, 12.0), &format!("{low} %"), 9.5, t.sub, Align::Right);

    let start = samples[0].t;
    let span = samples[samples.len() - 1].t.saturating_sub(start).max(1) as f32;
    let point = |s: &Sample| {
        let x = area.x + s.t.saturating_sub(start) as f32 / span * area.w;
        let ratio = f32::from(s.p.clamp(low, high) - low) / f32::from(high - low);
        (x, area.y + area.h - ratio * area.h)
    };
    for pair in samples.windows(2) {
        c.line(point(&pair[0]), point(&pair[1]), row.status, 2.0);
    }
    let labels = rect(area.x, area.y + area.h + 2.0, area.w, 14.0);
    c.text(labels, &chart.first, 10.0, t.sub, Align::Left);
    c.text(labels, &chart.last, 10.0, t.sub, Align::Right);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chart_range() {
        let s = |p| Sample { t: 0, p };
        assert_eq!(range(&[s(72), s(80)]), (60, 90));
        assert_eq!(range(&[s(100)]), (80, 100));
        assert_eq!(range(&[s(0)]), (0, 20));
    }

    #[test]
    fn clicks() {
        let row = |id: &str| Row {
            id: id.into(), name: id.into(), kind: Kind::Mouse, percent: 50, estimated: false, offline: false,
            charging: false, line: String::new(), extra: String::new(), status: 0,
        };
        let mut m = Model { rows: vec![row("a"), row("b")], light: false, expanded: None };
        assert_eq!(hit(&m, 280.0, 20.0), Some(Hit::Gear));
        assert_eq!(hit(&m, 50.0, HEADER + 10.0), Some(Hit::Row(0)));
        let before = height(&m);
        m.expanded = Some(("a".into(), Chart { samples: vec![], first: String::new(), last: String::new() }));
        assert_eq!(height(&m), before + CHART);
        assert_eq!(hit(&m, 50.0, HEADER + ROW + 10.0), Some(Hit::Row(0)));
        assert_eq!(hit(&m, 50.0, HEADER + ROW + CHART + 10.0), Some(Hit::Row(1)));
        m.rows[0].extra = "800 DPI".into();
        assert_eq!(height(&m), before + CHART + EXTRA);
        assert_eq!(hit(&m, 50.0, HEADER + ROW + CHART + 5.0), Some(Hit::Row(0)));
    }
}
