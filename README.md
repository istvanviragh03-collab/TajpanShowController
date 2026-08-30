# Tajpán Show Controller

**Verzió: 1.0.0**

A Tajpán Show Controller Windowsos WPF alkalmazás helyi show-playbackhez és a Tajpán RS-485 Remote vezérlőhöz. A program kezeli a playlistet, az audiolejátszást, a Remote fizikai gombjait és LCD-jének szinkronizálását, valamint megjeleníti a kapcsolat legfontosabb diagnosztikáját.

## Rendszerkövetelmények

- 64 bites Windows 10 vagy Windows 11
- audio-kimenet
- a Remote használatához Tajpán FOH/RS-485 interfész és elérhető Windows COM-port

A kiadás többfájlos, self-contained `win-x64` build. Külön .NET runtime telepítése nem szükséges, és nincs szükség installerre.

## Indítás

1. Másold át a teljes `build/TajpanShowController-v1.0.0-win-x64` mappát a célgépre.
2. Indítsd el a `TajpanShowController.exe` fájlt.
3. A **Settings** oldalon válaszd ki az audio-kimenetet.
4. Válaszd ki a Tajpán hardware COM-portját, majd kattints a **Connect** gombra.

A mappa fájljait együtt kell tartani; a kiadás szándékosan nem single-file, mert ez a WPF- és NAudio-függőségek legmegbízhatóbb csomagolása.

## Hardware és Remote

A soros beállítás rögzített: **115200 baud, 8N1, ASCII, flow control nélkül**. A PC a busz mastere, a Remote pedig az alkalmazás state-poll kéréseire válaszol. A program nem hardcode-ol COM-portot: a **Refresh** frissíti a Windows által elérhető portokat, az utolsó választás felhasználónként mentésre kerül.

Az **Auto Connect** a mentett porttal próbál csatlakozni induláskor, az **Auto Reconnect** pedig kapcsolatvesztés után újracsatlakozik. A Remote négy fizikai kezelőszerve a kombinált **PLAY/PAUSE**, valamint a **STOP**, **PREVIOUS** és **NEXT** gomb; ezek vezérlik a playbacket. A track sorszáma, neve, ideje és PLAY/PAUSED/STOP állapota az LCD-re szinkronizálódik.

A főképernyőn és a Settings oldalon látható a PORT, POLL, LAST RX, ERRORS és RTT diagnosztika. A **Remote Debug** lap a célzott kommunikációs naplót mutatja; ez a felhasználói hibakeresés része.

## Audio és playback

A garantált alapformátum PCM WAV. Az MP3, WMA, AAC és M4A támogatása a célgépen elérhető Windows Media Foundation codec-ektől függ. A Settings oldalon kiválasztott NAudio/Windows kimenet az újonnan megnyitott lejátszásokra érvényes.

Támogatott műveletek: Play, Pause, Resume, Stop, Previous, Next, seek, hangerő és `MM:SS.d` időformátum. A Previous, Next és a track vége csak kiválasztja a következő elemet; automatikus lejátszás kizárólag Play/Start műveletre indul.

## Playlist és mentés

Audiofájlok fájlválasztóval vagy drag-and-droppal adhatók hozzá. A lista átrendezhető, elemei törölhetők, és a playlist JSON fájlba menthető vagy onnan megnyitható. Az alkalmazás az aktuális állapotot automatikusan is menti ide:

`%LocalAppData%\TajpanShowController\settings.json`

## Repository

- alkalmazás és library source: `src/`
- solution: `src/TajpanShowController.slnx`
- automatizált tesztek: `tests/`
- release és hardware dokumentáció: `docs/`
- végfelhasználói artifact: `build/TajpanShowController-v1.0.0-win-x64/`

Fejlesztői build:

```powershell
dotnet restore .\src\TajpanShowController.slnx
dotnet build .\src\TajpanShowController.slnx --configuration Release
dotnet test .\src\TajpanShowController.slnx --configuration Release
```

A kommunikáció rögzített v1.0 viselkedését a [kommunikációs referencia](docs/COMMUNICATION_PROTOCOL_V1.md) foglalja össze.
