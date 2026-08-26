# UI implementation guide

## 1. Intent
The application should feel like a dedicated live-show playback controller: compact, readable from a FOH position, dark, calm and operational. The UI must prioritize **playlist operation and state confidence**, not technical telemetry.

The selected reference is intentionally denser than a typical consumer media player. Do not “improve” it into a large hero-style Now Playing page.

## 2. Main window structure
### Header
Single 58 px dark header integrated with the window content.

Left:
- original Tajpán logo;
- `Tajpán Show Controller`;
- small subtitle `PLAYBACK SYSTEM`.

Center tabs:
- Playback
- Settings
- Remote Debug

Active tab:
- slightly lighter header background;
- thin red underline at bottom.

Right:
- small `AUDIO` healthy indicator;
- small `REMOTE` healthy indicator.

These are status-at-a-glance indicators only. Do not show COM port, baud, counters, last-RX timing etc. in the header.

## 3. Playback page
Two major columns.

### 3.1 Playlist panel — dominant
The playlist occupies most of the main work area.

Header content:
- small uppercase `PLAYLIST` label;
- playlist title, e.g. `Summer Show 2026`;
- path to the playlist text file;
- New / Open / Save / Save As buttons.

Playlist item columns:
1. drag handle;
2. generated order number;
3. actual audio filename;
4. duration.

Rules:
- Order number is generated from playlist position and is **not derived from the filename**.
- Display the actual filename, not a separate editable display title.
- Drag-and-drop reorders the list.
- After reorder, order numbers update automatically.
- Active/current track uses a subtle charcoal selection plus a thin Tajpán-red accent on the left.
- Do not introduce queue/prepared states.

Bottom:
- `+ Add files`
- `Remove`
- subtle `Drag to reorder` hint.

### 3.2 Playlist persistence
Playlist management belongs on the Playback page; there is no separate Project page.

A playlist:
- has a title;
- is stored as a plain-text file;
- stays in the same folder as the referenced audio files;
- stores relative filenames/paths so the entire folder can be moved without relinking absolute paths.

The UI must expose New / Open / Save / Save As.

## 4. Compact playback panel
The playback block is deliberately small.

Show only:
- current filename;
- elapsed time;
- total time;
- one thin progress bar;
- Previous;
- Play/Pause toggle;
- Stop;
- Next.

Avoid:
- giant Now Playing heading;
- giant timers;
- album artwork;
- artist metadata;
- playlist-item counters in the player;
- queue/prepare state;
- oversized transport controls.

## 5. Bottom status area
Three cards under the compact playback panel.

### 5.1 Drummer display
This is a **character LCD mirror**.

Important:
- no LCD progress bar;
- track sequence number is a separate value;
- filename is a separate value;
- filename may be truncated/scrolled according to the actual remote display width;
- use monospace character-cell styling;
- pale green/olive LCD background, dark characters;
- show a small `SYNC` state under the display.

Reference content:
```
03 HIGHWAY_TO_HE
02:14       PLAY
```

This represents the layout concept, not a new protocol definition. Bind it to the application's existing remote-display state.

### 5.2 Audio
Minimal main-page status only:
- `AUDIO` label;
- `ACTIVE` / fault state;
- compact L/R meters;
- selected device name in small muted text.

Detailed device configuration belongs in Settings.

### 5.3 Remote
Minimal main-page status only:
- `REMOTE` label;
- ONLINE/OFFLINE/FAULT state;
- one short health line.

Do not place COM port, baud, TX/RX counts or error counters here.

## 6. Settings page
Use the same top header/tabs.

Left sub-navigation:
- Audio
- Remote / RS-485
- Playback
- Playlist
- Application

Right content panel should use compact rows and native WPF input controls styled to match the dark theme.

Show all important configuration and health information here rather than on the main page.

Typical values shown by the reference:
- output device;
- output channels;
- sample rate;
- COM port;
- baud rate (192000);
- connection state.

Do not invent new protocol behavior in the UI layer.

## 7. Remote Debug page
This page may be technical and information-dense.

Top summary cards:
- REMOTE
- PORT
- BAUD
- LAST RX
- TX / RX
- ERRORS

Below:
- terminal-style runtime log;
- timestamps;
- TX / RX distinction;
- parsed lines may use a third muted accent color;
- should support pause/resume, clear, autoscroll and raw/parsed filtering if those functions already exist or are part of the implementation task.

Keep debug telemetry out of the Playback page.

## 8. Typography
- Default: Segoe UI.
- Character LCD and debug console: Consolas or another Windows monospace fallback.
- Avoid bold everywhere. Bold is for playlist title/current filename/status only.
- Tiny uppercase labels are deliberately muted and letter-spaced.

## 9. Visual density
This is a desktop control application, not a touch-first/mobile UI.

At 1920×1080:
- content should fit without horizontal overflow;
- header tabs must remain visually inside the header;
- playlist and right-side panels must not clip each other;
- leave narrow but consistent gaps between panels;
- avoid large empty decorative areas.

## 10. Binding behavior
All sample text in the reference must become bindings to existing application state:
- playlist title/path;
- rows and durations;
- current item;
- elapsed/total time;
- playback state;
- L/R meters;
- selected audio device;
- remote connection state;
- LCD mirror content;
- runtime debug counters/log.

Do not hard-code production values from the mockup.
