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
- The first COM12 attempt was blocked by another process holding the port
  exclusively. The port was later released and the post-fix results are recorded
  below.

## COM12 post-fix hardware measurements (2026-08-29)

The port was subsequently released and the real hardware tests were rerun.

| Traffic | Cycles | False disconnects | Max poll gap | Max valid-RX gap |
|---|---:|---:|---:|---:|
| No display traffic (control) | 20 | 0 | passed | passed |
| Combined `@N/@K/@P/@T` | 50 | 32 | 32.59 ms | 95.09 ms |
| Track number `@N` | 20 | 1 | 32.63 ms | 85.01 ms |
| Track name `@K` | 20 | 7 | 32.41 ms | 78.59 ms |
| Playback state `@P` | 20 | 1 | 32.58 ms | 59.53 ms |
| Timecode `@T` | 20 | 4 | 32.23 ms | 62.37 ms |

Interpretation: the PC poll scheduler remains below 50 ms, while valid `@B`
responses are delayed beyond the watchdog whenever LCD commands are processed.
The display-free control has zero disconnects. All four display frame families
can trigger the late-response condition; `@K` and `@T` reproduce it most often,
and combined traffic is substantially worse. The next optimization must reduce
LCD command rate/placement or otherwise protect the Remote's opportunity to
answer `@S`; increasing the 50 ms watchdog would only mask the measured problem.

### 115200 baud follow-up

The PC transport was changed from 200000 to 115200 baud and validated against
the real COM12 Remote. The production handshake passed. A subsequent 50-cycle
combined display stress run produced 27 false disconnects, a 33.38 ms maximum
poll gap, and a 93.30 ms maximum valid-RX gap. This is a small improvement over
the 200000-baud run (32 disconnects), but it does not resolve the Remote-side
response delay while LCD commands are being processed.

## Final single-outstanding scheduler (optimized firmware)

The final WPF scheduler uses one continuous RX reader and one application-level
transaction owner. Its absolute monotonic deadline advances by 20 ms per poll.
Each cycle is serialized as follows:

1. send `@S`;
2. receive and parse one valid `@Bxxxxxxxx`;
3. immediately send `@A` and close the poll transaction;
4. start at most one latest-value display transaction only when at least 8 ms
   plus a 2 ms poll guard remains before the next deadline;
5. wait asynchronously for its `@A`/`@X` (maximum 30 ms) before any new
   application transaction is sent.

Display selection priority is playback state, track number, track name, then
timecode. Each field stores only its latest wanted value. Timecode changes below
100 ms are coalesced. No FIFO display backlog exists.

Final COM12 measurement at 115200 8N1 with 50 Play/Stop-style cycles:

| Metric | Result |
|---|---:|
| Poll count | 530 |
| Poll interval average | 20.019 ms |
| Poll interval maximum | 34.884 ms |
| Poll RTT average | 9.132 ms |
| Poll RTT median | 8.828 ms |
| Poll RTT p95 | 10.631 ms |
| Poll RTT maximum | 19.934 ms |
| Valid `@B` gap average | 19.986 ms |
| Valid `@B` gap maximum | 35.027 ms |
| Display transactions acknowledged | 116 |
| Display RTT average | 8.404 ms |
| Display RTT maximum | 10.782 ms |
| Full display snapshot maximum settling | 84.786 ms |
| False disconnects | 0 |

The run exercised 646 completed bus transactions (530 polls plus 116 display
transactions). The complete four-test COM12 hardware integration suite passed,
including production handshake, LCD synchronization, 50-cycle display stress,
and repeated cancel/disconnect/reconnect recovery.

The Windows audio regression suite also completed 50 first-use silent WAV loads
followed by 50 repeated loads without a communication timeout, using the real
NAudio initialization path and simulated Remote transport. A physical button
latency capture was offered as a separate opt-in COM12 test, but no button edge
arrived during the 15-second measurement window; no fabricated latency value is
reported. Debug logging remained enabled during the hardware stress, but a
manual WPF run with the Debug page visibly open was not automated in this run.
