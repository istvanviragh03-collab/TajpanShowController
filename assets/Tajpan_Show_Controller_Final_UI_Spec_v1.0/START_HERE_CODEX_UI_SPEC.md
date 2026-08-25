# Tajpán Show Controller — final UI source of truth

## Codex instruction

Reproduce the WPF user interface in this package. Treat `references/FINAL_UI_REFERENCE.png` as the sole visual source of truth and `spec/UI_LAYOUT_1920x1080.json` as the machine-readable layout contract. Use `assets/Tajpan_Logo_Full.png` as the actual application logo; do not redraw, recolor, crop destructively, or replace it.

This package describes the final UI state that the user selected after explicitly returning to the version from two iterations earlier. Do not combine it with the blue/neon, three-column dashboard, playback-queue, oversized Now Playing, or detailed Remote Debug mockups from earlier iterations.

The application name displayed in the title bar is exactly:

`Tajpán Show Controller`

The visual target is a native Windows WPF desktop application at 1920×1080. Build the UI from WPF controls and vector/icon resources. Do not use the reference screenshot as a full-window background image.

## Authority and intentional data corrections

Visual hierarchy, spacing, shapes, colors, component placement, and typography follow the reference image. Dynamic values are examples, not hard-coded UI text.

One value in the old mockup is obsolete: the reference image shows `115200 baud`; the implemented UI must show and use `192000 baud`, matching the finalized protocol. This is a data correction only; it must not change the layout.

The system clock must be live. COM port, audio device, connection status, signal state, playlist contents, time codes, selected track, progress, and volume must be bindable values. At design time use the sample data listed below.

## Non-negotiable final decisions

- Dark, restrained, professional show-control appearance.
- Red Tajpán branding and red selection/accent color. Do not replace the accent with blue.
- The playlist is the largest and most central part of the main screen.
- There is no playback queue and no queue terminology anywhere.
- Playback information is compact and lives in its own full-width horizontal band below the main area.
- The playback band must never overlap, protrude into, or visually hang out of the main content area.
- The main screen contains only minimal controller state. Detailed counters, raw RX/TX traffic, errors, and debug logs belong on another screen and must not be added here.
- The 16×2 remote LCD preview remains visible in the left sidebar.
- The main playlist page and Settings are the only sidebar navigation items shown in this final main-screen design.
- No oversized Now Playing card, no album artwork, no waveform, no spectrum analyzer, no equalizer, no extra dashboard tiles, and no decorative neon glow.
- Do not add features or panels merely to fill the 1920-pixel width.

## Window and global composition

At a 1920×1080 client size and 100% Windows scaling, use four vertical bands:

1. Custom title bar: 50 px.
2. Main area: 700 px.
3. Compact playback band: 182 px.
4. Bottom status band: 148 px.

Total: exactly 1080 px. The title bar, playback band, and bottom status band span the entire window width. The main area has a fixed 330 px sidebar and a fluid playlist content region. The sidebar must stay between 310 and 360 px if the window is resized. The fluid content region consumes all remaining width.

Do not uniformly stretch the 1536×1024 reference bitmap to 1920×1080. Recreate the layout with WPF `Grid` rows and columns so the central playlist receives the additional horizontal space while vertical proportions stay consistent.

## 1. Title bar

- Height: 50 px; background near-black `#0F1214`.
- Bottom separator: 1 px `#2B3032`.
- Left: a small 27×27 presentation of the Tajpán logo, followed by `Tajpán Show Controller` in Segoe UI, 16 px, regular, `#F3F3F3`.
- Right: standard minimize, maximize/restore, and close controls, white/gray glyphs, aligned like native Windows controls.
- The entire center area is draggable. Double-click toggles maximize/restore.
- No extra menu, status, or tabs in this title bar.

## 2. Main area — left sidebar

- Width: 330 px at the target size.
- Background: `#111315`; right separator: 1 px `#303436`.
- Top brand image: use the supplied transparent PNG with `Stretch=Uniform`, centered, maximum width about 290 px and maximum height about 235 px. Preserve its aspect ratio and transparency.
- Under the logo, show the label `REMOTE LCD PREVIEW` in 13 px uppercase gray text with slight character spacing.
- LCD module: about 280×124 px, dark bezel with a thin gray border and four small screw circles. Inner screen is a muted yellow-green (`#6D9E2D` to `#8CBE43`) with black 5×7-style characters.
- LCD sample content is exactly two lines and must never wrap:

  `01 NYITÁNY`

  `STOP   00:00`

- Center `16 x 2` beneath the LCD in secondary gray.
- Navigation buttons below: full-width inset buttons, 48 px high, 12 px gap.
- Selected item: icon + `Lejátszási lista`, dark red background `#731E1C`, 1 px red border `#E23E3C`, white text.
- Unselected item: gear icon + `Beállítások`, transparent/dark background, gray-white text.
- Version `v1.0.0` sits near the lower-left of the sidebar in 13 px gray.

## 3. Main area — playlist content

- Content background: subtle near-black panel, approximately `#111314` with a restrained darker edge vignette only if it can be implemented without obvious gradients.
- Outer horizontal padding: 28 px left and 40 px right. Top padding: 12 px.
- Header row: `LEJÁTSZÁSI LISTA` at left, 20 px semibold uppercase.
- Toolbar at right, one line only; it must not wrap or extend under the title bar:

  - `Fájl hozzáadása` split button with plus icon and dropdown chevron.
  - `Megnyitás...` with folder icon.
  - `Lista mentése` with save icon.
  - `Lista ürítése` with trash icon and red text.
  - Ellipsis button.

- Toolbar button height: 43 px. Corner radius: 4 px. Gap: 14–16 px. Border: `#323638`; fill: `#1B1E20`.
- Playlist table begins below the header row. Border: 1 px `#303436`; radius: 4 px. No heavy grid lines.
- Header columns: drag/status, number, `CÍM`, `IDŐTARTAM`, `MŰVELETEK`.
- Rows are 57 px high at the target size.
- Selected/active row: very dark red translucent fill, 2–3 px red bar on the far left, red play triangle, red top/bottom hairline.
- Other rows: near-black alternating tone only; separators `#2B3032`.
- Drag handle: six small gray dots. Operations: vertical ellipsis.
- Title is left-aligned. Duration is right-aligned within its column.
- Keep at least seven rows visible without scrolling at 1920×1080.
- Beneath the table: a dashed-border drop zone about 110 px high with centered upload-cloud icon and:

  - `Húzza ide a fájlokat, vagy kattintson a hozzáadáshoz`
  - `Támogatott formátum: WAV (44.1kHz / 16bit)`

The first line is 16 px; the second is 14 px secondary gray. The entire drop zone is clickable and accepts drag-and-drop.

## 4. Compact playback band

- Separate full-width row with top and bottom 1 px borders `#303436`.
- Background: `#121618`. It is not a floating card and must not overlap the main row.
- Left block: 108×108 dark square with a muted music-note icon. Beside it:

  - red uppercase label `JELENLEG KIJELÖLVE`, 13 px semibold;
  - track name `Nyitány`, 24 px semibold;
  - position `1 / 7`, 16 px gray.

- Center: one compact line of transport controls. Order is shuffle, previous, play/pause, next, stop. The primary circular play button is approximately 68 px; other glyph buttons remain visually lighter and smaller.
- Progress row below the controls: current time at left, thin gray track with red thumb/progress, total duration at right. Sample `00:00` and `04:32`.
- Right: speaker icon and a horizontal volume slider. No detailed stereo meters in this band.
- Preserve generous spacing. Do not add a giant filename, giant elapsed time, separate duration card, status badge, or album/artist data.

## 5. Bottom status band

- Height: 148 px; background `#15191C`.
- Five horizontally arranged status groups separated by 1 px vertical lines `#303436`.
- Group headings are 13 px uppercase secondary gray with slight tracking.
- Group 1 — `KIMENET`: speaker icon, `Hangkimenet`, `Realtek ASIO`, compact green `OK` badge.
- Group 2 — `HANGERŐ`: speaker icon, `-12.0 dB`, short segmented green level indicator.
- Group 3 — `REMOTE KAPCSOLAT`: chain icon, green `Csatlakoztatva`, `COM3   192000 baud`, one small green status dot.
- Group 4 — `REMOTE JEL`: signal-bars icon, green `Erős`, one small green status dot.
- Group 5 — `RENDSZERIDŐ`: clock icon, live localized date and time. The screenshot value is sample data only.
- Keep this information concise. Do not show RX/TX packet counts, last-RX milliseconds, errors, raw serial traffic, or debug controls here.

## Typography and icons

- Primary font: Segoe UI / Segoe UI Variable.
- Normal text: 14–18 px depending on role.
- Section headings: 20 px semibold uppercase.
- Track title in the compact playback band: 24 px semibold.
- Primary text: `#F2F2F2`; secondary: `#A5A7A8`; subdued: `#777B7D`.
- Use Segoe Fluent Icons, Fluent-style vector paths, or equivalent locally bundled vector icons. Do not use emoji or mismatched bitmap icons.
- Render the LCD characters with a local 5×7 dot-matrix glyph control or locally bundled project font. Do not require a global font installation.

## Color tokens

Use the exact tokens from `spec/UI_LAYOUT_1920x1080.json`. Important anchors sampled from the final reference:

- window/sidebar: `#111315`
- main background: `#111314`
- panel: `#131618`
- table header: `#171A1B`
- border/separator: `#2B3032`
- primary red: `#F24341`
- selected dark red: `#731E1C`
- success green: `#55D43D`
- LCD green: `#6D9E2D` / `#8CBE43`

Avoid bright blue accents. Green is reserved for healthy/live states. Red is reserved for Tajpán branding, the selected track, destructive actions, and playback progress.

## Design-time sample playlist

| # | Cím | Időtartam |
|---:|---|---:|
| 1 | Nyitány | 04:32 |
| 2 | Bevonulás | 03:48 |
| 3 | Jelenetváltás | 01:17 |
| 4 | Fő playback | 05:21 |
| 5 | Finálé | 04:06 |
| 6 | Ráadás | 02:59 |
| 7 | Lezárás | 03:15 |

Use Unicode correctly in every Hungarian label. Do not remove accents.

## WPF implementation constraints

- Use a root `Grid` with the four exact row roles and a nested two-column `Grid` for the main area.
- Prefer `DynamicResource` theme tokens and reusable styles for buttons, DataGrid rows, status blocks, and typography.
- Keep layout responsive down to 1600×900 without overlap. The authoritative acceptance viewport is 1920×1080.
- Use clipping only inside intentionally bounded controls such as the logo and LCD; do not use clipping to hide layout mistakes.
- Use `TextTrimming=CharacterEllipsis` for long track names, but never trim the fixed labels in this specification.
- Do not install system-wide fonts or packages to obtain the look. Any added font/icon dependency must be project-local.
- Preserve standard keyboard focus and button behavior, while keeping focus visuals consistent with the red theme.

## Acceptance checklist

Before considering the UI complete, render a 1920×1080 screenshot and compare it with `references/FINAL_UI_REFERENCE.png`.

The implementation fails acceptance if any of the following is true:

- a queue panel exists;
- the playlist is not the dominant main element;
- playback content overlaps the main area or bottom status band;
- the LCD preview is clipped or missing;
- the title bar or toolbar wraps to a second line;
- detailed remote/debug counters appear on the main screen;
- blue becomes the primary accent;
- the Tajpán logo is replaced, distorted, recolored, or omitted;
- `115200` appears anywhere in the UI;
- there is horizontal or vertical overflow at 1920×1080;
- Hungarian accents are missing;
- extra unrequested cards, charts, meters, artwork, or navigation items are added.

The reference image is authoritative for appearance. This document is authoritative for the finalized 1920×1080 adaptation and for the explicit exclusions/corrections above.
