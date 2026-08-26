# Tajpán Show Controller — Codex UI Reference Pack

## Goal
Implement the supplied UI design in the existing **Tajpán Show Controller** WPF application.

This package is a **visual and layout specification**, not a replacement application. The existing playback engine, playlist logic, remote protocol/service layer, persistence and MVVM architecture remain authoritative unless a separate task explicitly changes them.

## Start here
1. Open `reference/index.html` in a desktop browser.
2. Read `docs/IMPLEMENTATION_GUIDE.md`.
3. Use `docs/DESIGN_TOKENS.json` and `wpf-reference/Theme.Reference.xaml` for colors/sizes.
4. Use `wpf-reference/MainWindow.Reference.xaml` as a structural map only — adapt it to the existing ViewModels/commands/services instead of replacing working logic.

## Critical visual rules
- Product name: **Tajpán Show Controller**.
- Windows desktop UI. Primary target: **1920×1080, 100% scaling**.
- Dark graphite surfaces, muted borders, restrained red Tajpán accent, green healthy-state indicators.
- The **playlist is the dominant working area**.
- Playback panel is intentionally compact.
- Main screen remote information is intentionally minimal.
- Drummer display is a **character LCD mirror**, not a graphical/progress display.
- No separate Project screen.
- No Queue / Prepare feature.
- Do not add decorative cards, oversized headings, giant time counters or mobile-style responsive stacking.

## Logo rule — IMPORTANT
The actual application/repository may already contain the previously supplied original Tajpán logo asset. **If it exists, reuse that original asset exactly and do not redraw or replace it.**

`reference/assets/taipan-logo-reference.svg` is only a self-contained layout fallback so the HTML reference renders on its own. It is not intended to supersede the original project logo.

## Implementation safety
- Preserve the existing project structure and MVVM separation.
- Do not move unrelated files.
- Do not install global packages/tools without explicit permission.
- Prefer normal WPF controls, ResourceDictionaries, bindings, commands, converters and styles.
- Do not embed a WebView/HTML renderer into the product. The HTML is reference material only.
