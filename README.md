# Tajpán Show Controller

Windows WPF show-control alkalmazás helyi audiolejátszáshoz és a végleges v1.0 ASCII RS485 protokollt használó REMOTE vezérlőhöz. A PC a master; a vezérlő nélkül is használható minden helyi playlist- és playback-funkció.

## Technológia és szerkezet

- C#, WPF, .NET 10 LTS (`net10.0-windows`)
- CommunityToolkit.Mvvm, NAudio, System.IO.Ports
- `TajpanShowController.csproj`: WPF felület és view modellek
- `src/TajpanShowController.Core`: modellek, interfészek, codec, streaming parser, élérzékelés
- `src/TajpanShowController.Infrastructure`: NAudio playback, JSON persistence, valódi és szimulált transport, remote scheduler
- `tests/TajpanShowController.Tests`: hardware- és audiofüggetlen xUnit tesztek
- `assets`: változatlan protokoll- és UI-referenciák

## Build, teszt és futtatás

```powershell
dotnet restore TajpanShowController.slnx
dotnet build TajpanShowController.slnx
dotnet test TajpanShowController.slnx
dotnet run --project TajpanShowController.csproj
```

## Audio

A biztos elsődleges formátum a Windows/NAudio által olvasható PCM WAV. Az alkalmazás fájlválasztója MP3, WMA, AAC és M4A fájlokat is enged, de ezek dekódolhatósága a gépen elérhető Windows Media Foundation codec-ektől függ; ezek a formátumok ebben a verzióban nincsenek minden gépre garantálva. Hibás, hiányzó vagy nem dekódolható fájl nem állítja le az alkalmazást.

A `Settings / Audio` oldalon a NAudio által használt Windows kimeneti eszköz választható. A választás tartósan bekerül a beállításfájlba, és az újonnan megnyitott lejátszások ezt az eszközt használják.

Previous, Next és a szám vége csak kijelöli a másik számot; automatikus lejátszás nincs. Indítás kizárólag Play/Start művelettel történik.

## COM-port és Pro Micro

A fejléc `Settings` oldalán frissítsd a portlistát, válaszd ki a portot, majd kattints a `Connect` gombra. A beállítás fix: `192000 8N1`, ASCII, CRLF, flow control nélkül. Nincs automatikus portválasztás vagy automatikus csatlakozás.

Közvetlen Pro Micro USB Serial teszthez ugyanígy a Pro Micro COM-portját válaszd ki. A későbbi transzparens FTDI–RS485 adapterhez ugyanaz a beállítás használható; a PC nem vezérel DE/RE GPIO-t.

## Szimuláció

A `Settings` oldalon jelöld be a `Simulation` opciót, majd kapcsolódj. A szimulátor nem nyit valódi COM-portot, és ugyanazt a codec/parser/scheduler réteget használja. START/STOP/PAUSE/PREV/NEXT, hibás sor, NACK és kapcsolatvesztés a felületről is előidézhető.

## Helyi fájlok

Normál futáskor a beállítás és a playlist itt tárolódik:

`%LocalAppData%\TajpanShowController\settings.json`

Az automatikus tesztek kizárólag egyedi ideiglenes könyvtárat használnak.

## Ismert korlátozások

- Valódi audio-kimeneten és minden opcionális tömörített formátummal külön manuális ellenőrzés szükséges.
- A 100 Hz célperiódus Windows alatt best effort, nem valós idejű garancia.
- A v1.0 nem tartalmaz queue-t, firmware-t, installert, automatikus frissítést, telemetriát vagy internetet igénylő runtime funkciót.
- A protokoll elsődleges forrása az `assets` könyvtár két v1.0 DOCX dokumentuma; ez a README nem helyettesíti azokat.
