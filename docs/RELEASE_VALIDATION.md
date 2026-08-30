# Tajpán Show Controller V1.0.0 release validation

Validation date: 2026-08-30
Release configuration: `Release`, `net10.0-windows`, `win-x64`, self-contained, multi-file, untrimmed

## Build and automated regression

- Release solution build: 0 warnings, 0 errors.
- Automated suite without hardware opt-in: 101 passed, 0 failed, 6 hardware tests skipped as designed.
- NuGet advisory audit: no known vulnerable direct or transitive packages.
- Standalone launch: `TajpanShowController.exe` created a responsive WPF window and loaded the persisted three-track playlist.
- Standalone UI Automation: Play, Pause, Resume, 25% seek, volume, Stop, Next and Previous passed while the real Remote stayed connected.
- UI diagnostics throughout playback: COM14, 49.9–50.0 Hz, 0 errors, synchronized Remote display.
- Clean application shutdown: passed through the application's own Close control.

## Hardware discovery

Windows enumerated COM1, COM2, COM14 and COM15. COM1/COM2 were HHD Software bridged virtual ports and were not probed. COM14/COM15 reported:

- FriendlyName: Arduino Leonardo
- PNP ID: `USB\VID_2341&PID_8036&MI_00\...`
- status: OK

Only the existing production `@S` state poll was used for identification. Both Arduino candidates returned a valid `@B00000000` frame and accepted the normal ACK; no random probe data was sent. The application's persisted active FOH/Remote endpoint and the complete release acceptance target was COM14.

**Detected Tajpán hardware: COM14**

## COM14 hardware acceptance

The production handshake, display synchronization, rapid controlled disconnect/reconnect recovery, first-use NAudio playback and 50-cycle combined display/polling stress all passed (5/5 automated hardware tests).

| Metric | Result |
|---|---:|
| Connection state | Connected |
| Stress cycles | 50 |
| Poll count | 532 |
| Poll frequency | 49.983 Hz |
| Poll interval average / maximum | 20.007 / 34.209 ms |
| Poll RTT average / median / p95 / maximum | 9.189 / 9.002 / 10.652 / 12.583 ms |
| Valid RX gap average / maximum | 20.005 / 33.928 ms |
| Last RX age at capture | 0.408 ms |
| Display transactions | 114 |
| Display RTT average / maximum | 8.675 / 11.829 ms |
| Display snapshot settling maximum | 107.153 ms |
| Timeout count | 0 |
| False disconnects | 0 |

The first-use hardware test created two previously unloaded silent WAV tracks and exercised Play, Pause, Resume, seek, volume, Stop, Next and Previous while the physical Remote was polled and its LCD was updated. No timeout or disconnect occurred.

## Physical Remote controls

The final Remote has four physical controls: combined PLAY/PAUSE, STOP, PREV and NEXT. The acceptance test exercised the combined control in both STOP and PLAYING display states.

- observed protocol edges: Start, Stop, Pause, Previous, Next
- button dispatch latency average / maximum: 0.806 / 2.409 ms
- connection loss during button test: none

## Communication compatibility

No protocol/timeout/watchdog behavior changes.

The production values remain 115200 8N1, ~50 Hz polling and a 50 ms disconnect timeout. Framing, ACK/NACK, display coalescing, reconnect and playback state handling are unchanged. Cleanup removed only a private, unreachable legacy read/poll implementation that had no call sites.
