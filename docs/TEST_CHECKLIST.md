# Tajpán Show Controller V1.0.0 acceptance checklist

## Repository és build

- [ ] A working tree tiszta, a `v1.0.0-preopt` checkpoint elérhető GitHubon.
- [ ] A solution a `src/TajpanShowController.slnx` útvonalon restore-olható és buildelhető.
- [ ] A Release build 0 errorral és a release-változtatásokból eredő warning nélkül készül.
- [ ] A teljes automatizált suite sikeres; csak explicit hardware-interakciót igénylő teszt maradhat opt-in.
- [ ] A `build/TajpanShowController-v1.0.0-win-x64` artifact self-contained és PDB/debug/test fájloktól mentes.

## Alkalmazás és playlist

- [ ] A publikált `TajpanShowController.exe` tisztán elindul.
- [ ] Playlist betöltés, mentés és automatikus persistence működik.
- [ ] Hozzáadás, törlés, drag-and-drop és fel/le rendezés működik.
- [ ] Play, Pause, Resume, Stop, Previous, Next, seek és hangerő működik.
- [ ] Az idő `MM:SS.d` formátumú.
- [ ] A kijelölt és ténylegesen játszott track állapota nem keveredik.

## Hardware-felderítés

- [ ] A Windows összes COM-portja enumerálva van.
- [ ] FriendlyName, PNP Device ID és VID/PID rögzítve van, ahol elérhető.
- [ ] Csak releváns USB-soros jelölteken fut a meglévő `@S`/`@Bxxxxxxxx` handshake.
- [ ] Port csak érvényes protokollválasz után minősül Tajpán hardware-nek.
- [ ] A port nincs hardcode-olva az alkalmazásba vagy a tesztbe.

## Valódi Remote acceptance

- [ ] Connect után a Remote stabilan Connected.
- [ ] Polling megközelítőleg 50 Hz; Last RX és RTT normális, Errors nem nő.
- [ ] PLAY/PAUSE, STOP, PREVIOUS és NEXT fizikai gombél beérkezik.
- [ ] Track number, track name, playback time és PLAY/PAUSED/STOP LCD-frissítés működik.
- [ ] Egy új, az adott indítás óta még nem játszott track első indítása nem szakítja meg a Remote-ot.
- [ ] Pause/Resume, Stop, Previous/Next, seek és volume közben a polling nem áll meg.
- [ ] Kontrollált disconnect után gyors Disconnected állapot és automatikus reconnect következik.
- [ ] Reconnect után a display state újraszinkronizálódik, app restart nem kell.
