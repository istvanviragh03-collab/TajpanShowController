# Remote display / polling stress report for Codex

## Failure signature

The COM12 hardware is stable with state polling only. False disconnects appear
when several LCD fields are updated during Play/Stop transitions.

Measured before the final scheduling fix:

- normal poll gaps: approximately 31–33 ms maximum on this hardware run;
- terminal last-valid-RX age at disconnect: 53–56 ms;
- worst observed valid-RX gap during combined display stress: 62–64 ms;
- watchdog threshold: 50 ms (do not increase it);
- 20 display-free cycles: zero disconnects;
- combined 50-cycle display stress: up to two false disconnects before fixing
  the TX ordering.

## Affected frames

LCD housekeeping uses these PC-to-Remote frames:

- `@N...` — track number;
- `@K...` — track name;
- `@P...` — playback state (`@PP`, `@PA`, `@PS`);
- `@T...` — timecode.

Each frame type passed when tested independently with spacing. The late response
was reproducible when the fields changed together and were interleaved with
`@S` polling. A representative TX sequence was:

```text
@N0, @KPLAY 04, @PP, @T00:00.0,
@N19, @KPLAY 23, @PS, @T00:03.2
```

The problem is therefore not one malformed frame type. It is scheduling and
serialization pressure caused by the combined display sequence. Delayed display
ACK traffic shares the RX stream with `@B...` poll responses.

## Regression discovered during the first fix

The condition below prevented almost every display frame from being sent:

```csharp
if (lastValidRemoteRx <= currentPollTx) return;
```

It was evaluated immediately after transmitting the current `@S`, before that
poll could normally receive a response. Usually only the first `@N` frame was
sent; `@K`, `@P`, and `@T` starved.

## Final scheduling model

- The monotonic poll scheduler sends `@S` every 20 ms independently.
- A single continuous RX reader parses all serial input.
- A valid `@B...` response updates connection health first, sends its ACK, then
  signals a bounded/coalesced display worker.
- The display worker sends at most one pending LCD frame per signal and observes
  the 40 ms housekeeping interval.
- The four display fields retain only their newest wanted values.
- A short TX semaphore prevents byte-level frame interleaving.
- Debug/UI work is not awaited by polling or RX parsing.

## Required stress procedure

Use `TAJPAN_HARDWARE_COM=COM12` and run the hardware integration tests. Validate:

1. at least 5 seconds idle;
2. isolated `@N`, `@K`, `@P`, and `@T` updates;
3. 50 combined Play/Stop-style display cycles;
4. first-use WAV load and repeated WAV load;
5. pause/resume, stop, next/previous, seek;
6. disconnect/reconnect;
7. debug panel open and closed.

Capture at minimum maximum poll gap, maximum valid-RX gap, RTT average/max, and
false disconnect count. Acceptance requires zero false disconnects and no gap
reaching the 50 ms watchdog because of application scheduling.

## Verification status (2026-08-29)

- Release solution build: passed with zero warnings and zero errors.
- Display transport/logging regression test: passed in five consecutive runs.
- Reconnect/full LCD snapshot regression test: passed in five consecutive runs.
- Verified simulated TX set: `@N`, `@K`, `@P`, and `@T` all leave the transport.
- Full automated run: 99 passed, 3 failed, 4 hardware tests skipped. Two failures
  are pre-existing timing expectations (`FiftyBlockingFirstUseCommands...` and
  `IdlePollingIsCoalesced...`); the LCD reconnect test was timing-sensitive in
  the first full run and was made deterministic by moving its 40 ms wait into
  the independent display worker.
- COM12 hardware stress was attempted, but another running process held the port
  exclusively (`UnauthorizedAccessException: Access to COM12 is denied`). No
  post-fix hardware numbers may be claimed until that process is closed and the
  procedure above is rerun.
