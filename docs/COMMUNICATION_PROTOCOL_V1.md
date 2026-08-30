# Tajpán Remote communication reference v1.0

Ez a dokumentum a V1.0.0 release-ben ténylegesen használt kommunikáció rövid referenciája. Nem módosítja a protokollt; a production implementáció és a hardware-acceptance által validált értékeket rögzíti.

## Soros kapcsolat

| Paraméter | Érték |
|---|---:|
| Baud rate | 115200 |
| Adat/paritás/stop | 8N1 |
| Kódolás | ASCII |
| Flow control | nincs |
| Frame kezdete | `@` |
| Sorvég | `\r\n` |
| Poll célperiódus | 20 ms (~50 Hz) |
| Disconnect timeout | 50 ms az utolsó érvényes válasz óta |

Az FOH USB/RS-485 interfész transzparens; a PC az alkalmazási master, a Remote önállóan nem kezdeményez forgalmat. A protokoll nem használ CRC-t vagy Reed–Solomon hibajavítást. A hibakezelés kötött framingre, ACK/NACK válaszokra, timeoutokra, latest-value display retry-ra és reconnectre épül.

## Frame-ek

| Irány | Frame | Jelentés |
|---|---|---|
| PC → Remote | `@S` | nyolcbites gombállapot lekérése |
| Remote → PC | `@Bxxxxxxxx` | START, STOP, PAUSE, PREV, NEXT és három reserved bit |
| mindkettő | `@A` | ACK |
| mindkettő | `@X` | NACK |
| PC → Remote | `@TMM:SS.d` | playback idő |
| PC → Remote | `@N<number>` | track sorszám |
| PC → Remote | `@K<name>` | ASCII track név |
| PC → Remote | `@PQ/@PP/@PA/@PS` | queued/playing/paused/stopped állapot |

Minden frame végén CRLF áll. A track névből a vezérlőkarakterek, a nem ASCII karakterek és az `@` eltávolításra kerülnek.

## Ütemezés és kapcsolatfelügyelet

Az alkalmazás monotonic 20 ms-os poll schedule-t használ. Érvényes `@Bxxxxxxxx` után azonnal `@A` választ küld, és a kapcsolat utolsó érvényes RX idejét a UI-, logging- és playback-munka előtt frissíti. A gombesemények 0→1 élből keletkeznek, ezért a nyomva tartás nem ismétli a parancsot.

A kijelző state, track number, track name és time mezői latest-value/coalescing elven frissülnek. Egy időben egy alkalmazási tranzakció aktív; display parancs csak akkor indul, ha a következő poll előtt elegendő idő marad. ACK esetén a mező szinkronizált, NACK vagy timeout esetén a legfrissebb kívánt érték pending marad.

A watchdog 5 ms-onként ellenőrzi az utolsó érvényes Remote-válasz korát. 50 ms elérésekor a kapcsolat Disconnected állapotú lesz; ez az érték nem release-stabilitási kerülőmegoldás, hanem a rögzített V1.0.0 viselkedés. A coordinator automatikus reconnect után a teljes legfrissebb kijelzőállapotot újraszinkronizálja.
