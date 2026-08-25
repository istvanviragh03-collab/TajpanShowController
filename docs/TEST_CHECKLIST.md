# Tajpán Show Controller – manuális tesztlista

## Indítás és elrendezés

- [ ] Az alkalmazás controller nélkül, kezeletlen kivétel nélkül elindul.
- [ ] 1920×1080 / 100% és 125% skálázás mellett nincs panelátfedés vagy horizontális görgetés.
- [ ] A fejléc, fő tartalom, playback sáv és státuszsáv nem fedik egymást.
- [ ] A playlist központi és domináns; a Now Playing kompakt.
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

## Remote és szimuláció

- [ ] Portfrissítés után COM-port választható; nincs automatikus csatlakozás.
- [ ] A felület `192000 8N1` értéket mutat, `115200` sehol nem jelenik meg.
- [ ] Szimuláció nem foglal valódi COM-portot.
- [ ] START, STOP, PAUSE, PREV és NEXT működik; nyomva tartás csak egy eseményt okoz.
- [ ] Hibás sor, NACK és timeout után nincs összeomlás; maximum retry után FAULT jelenik meg.
- [ ] Kapcsolat helyreállásakor trackszám, név, state és timecode újraszinkronizálódik.
- [ ] Disconnect és alkalmazásbezárás felszabadítja a portot.

## COM12 hardware smoke teszt

- [ ] COM12 megnyitható 192000 8N1 beállítással.
- [ ] `@S\r\n` kérésre pontos `@Bxxxxxxxx\r\n` válasz érkezik.
- [ ] A PC `@A\r\n` választ küld az érvényes gombállapotra.
- [ ] `@N`, `@K`, `@P` és `@T` display parancsokra `@A` érkezik.
- [ ] Gombnyomások a megfelelő alkalmazásműveletet váltják ki.
