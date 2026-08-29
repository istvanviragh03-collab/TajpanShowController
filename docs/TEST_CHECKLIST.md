# Tajpán Show Controller – manuális tesztlista

## Indítás és elrendezés

- [ ] Az alkalmazás controller nélkül, kezeletlen kivétel nélkül elindul.
- [ ] 1920×1080 / 100% és 125% skálázás mellett nincs panelátfedés vagy horizontális görgetés.
- [ ] A fejléc, fő tartalom, playback sáv és státuszsáv nem fedik egymást.
- [ ] Az 58 px-es fejlécben Playback, Settings és Remote Debug tab található.
- [ ] A playlist a Playback oldal domináns bal oldali panelje; a jobb oldali player kompakt.
- [ ] A player alatt külön Drummer Display, Audio és Remote kártya található.
- [ ] Nincs Queue panel vagy queue művelet.
- [ ] Az eredeti Tajpán-logó torzítás és átszínezés nélkül látható.
- [ ] A főképernyőn csak kompakt remote állapot látható; diagnosztika csak a Beállítások ablakban van.

## Playlist és playback

- [ ] Több WAV fájl hozzáadható fájlválasztóval és drag-and-droppal.
- [ ] Dupla kattintás és Play elindítja a kijelölt számot.
- [ ] Play/Pause/Resume, Stop és hangerő működik.
- [ ] Previous/Next nem indít automatikus playbacket.
- [ ] A szám vége a következőt csak kijelöli.
- [ ] Fel/le mozgatás, eltávolítás, JSON export/import és megerősített listatörlés működik.
- [ ] Hiányzó, sérült vagy nem támogatott fájl kezelhető hibaüzenetet ad.
- [ ] Újraindítás után a playlist és hangerő visszatöltődik.
- [ ] PLAYING/PAUSED állapotban a Now Playing és az LCD a PlayingTrack adatait tartja meg másik sor kijelölésekor is.
- [ ] A játszott sor PLAYING alatt villogó, PAUSED alatt folyamatos Playing jelzést kap.
- [ ] A Settings oldalon kiválasztott Windows audioeszköz újraindítás után visszatöltődik.

## Remote és szimuláció

- [ ] Portfrissítés után COM-port választható; nincs automatikus csatlakozás.
- [ ] A felület `115200 8N1` értéket mutat, korábbi baud rate sehol nem jelenik meg.
- [ ] Szimuláció nem foglal valódi COM-portot.
- [ ] START, STOP, PAUSE, PREV és NEXT működik; nyomva tartás csak egy eseményt okoz.
- [ ] Hibás sor, NACK és timeout után nincs összeomlás; maximum retry után FAULT jelenik meg.
- [ ] Kapcsolat helyreállásakor trackszám, név, state és timecode újraszinkronizálódik.
- [ ] Disconnect és alkalmazásbezárás felszabadítja a portot.

## COM12 hardware smoke teszt

- [ ] COM12 megnyitható 115200 8N1 beállítással.
- [ ] `@S\r\n` kérésre pontos `@Bxxxxxxxx\r\n` válasz érkezik.
- [ ] A PC `@A\r\n` választ küld az érvényes gombállapotra.
- [ ] `@N`, `@K`, `@P` és `@T` display parancsokra `@A` érkezik.
- [ ] Gombnyomások a megfelelő alkalmazásműveletet váltják ki.
