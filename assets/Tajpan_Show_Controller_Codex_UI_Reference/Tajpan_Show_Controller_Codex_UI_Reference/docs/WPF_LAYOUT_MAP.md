# WPF layout map

This document translates the HTML reference into practical WPF primitives.

## Main shell
Recommended hierarchy:

```text
Window
└─ Grid Rows: 58, *
   ├─ Header Grid
   │  ├─ Branding
   │  ├─ Top navigation
   │  └─ Audio / Remote health
   └─ ContentControl / Grid for selected page
```

Use a single shell and swap page content with DataTemplates, UserControls or an existing navigation mechanism. Do not open separate windows for Playback / Settings / Remote Debug unless the existing architecture already explicitly requires it.

## Playback page
```text
Grid Columns: 1.5*, 0.82*
Column 0: Playlist card
Column 1:
  Grid Rows: ~174, *
  Row 0: compact playback card
  Row 1:
    Grid Columns: 1.25*, 0.8*, 0.8*
    LCD | Audio | Remote
```

The numeric proportions are reference values, not permission to overflow the window. Use `MinWidth=0` behavior equivalents, sensible minimums and star sizing so 1920×1080 fits cleanly.

## Playlist row
Use a `ListBox`, `ListView` or `ItemsControl` with a custom item template.

Suggested columns:
- 22 px drag handle
- 32 px sequence number
- `*` filename
- 52 px duration

Current row visual:
- background `#1D2024`;
- border `#553034`;
- 3 px red left accent `#D62D32`.

Do not edit the filename as a separate display label. It represents the real audio filename.

## Drag reorder
Implement WPF drag-and-drop on playlist items.
After a drop:
1. update the bound collection order;
2. regenerate sequence numbers from collection index;
3. persist only when the existing save/autosave behavior says to persist.

Do not derive sequence numbers from the filename.

## Player
Use a compact `Border` + Grid/StackPanel.
Do not let player height grow with the window unnecessarily.

Suggested controls:
- `TextBlock` filename;
- elapsed/total row;
- thin progress `ProgressBar` or custom track;
- four compact Buttons.

## Character LCD
Recommended WPF construction:
- outer `Border` dark bezel;
- inner `Border` with `#ABC489` background;
- `TextBlock`/two rows in Consolas;
- preserve fixed-width appearance.

Do not use a graphical playback progress bar inside the LCD.

## Status cards
Use light-weight Borders with shared style. Do not create separate full-size diagnostic dashboards on Playback.

## Settings
Left vertical ListBox/RadioButtons or navigation buttons, right ContentControl. Keep spacing compact.

## Remote Debug
Use a top uniform-ish Grid for summary cards and a large monospace log region below. A virtualized list is preferable to one gigantic TextBox for long-running sessions.
