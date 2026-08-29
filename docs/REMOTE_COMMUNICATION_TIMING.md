# Remote communication timing

## Previous behavior

HighDelay used a 10 ms poll period, but each poll waited for its response and
then synchronously processed display housekeeping. A lost connection was
declared after three poll failures, so the transition depended on response
timeouts and retry progress rather than on the age of the last valid frame.

## Current behavior

- Polling frequency: **50 Hz**
- Polling period: **20 ms**
- Remote disconnect timeout: **50 ms**

The poll loop uses a monotonic `Stopwatch` schedule. The next poll deadline is
advanced by exactly one period, so response processing does not add another
20 ms delay. Display updates are write-only housekeeping on this loop; they do
not synchronously wait for an ACK and therefore cannot hold up the next state
poll. ACK/NACK frames remain protocol-compatible and are consumed by the
streaming parser.

Timecode formatting is unchanged. Display timecode updates are coalesced at
the existing 100 ms position-difference threshold; at 50 Hz this intentionally
does not require a separate transmission for every exact tenth of a second.

RX parsing runs on the communication worker. Every valid `@B` frame updates
`lastValidRemoteRx` with a monotonic timestamp before logging, button dispatch,
or UI work. A 5 ms `PeriodicTimer` watchdog compares that timestamp against the
50 ms timeout and sets only the Remote state to `Disconnected`; it does not
close the COM transport. The next valid frame changes the state to `Connected`
immediately, without debounce.

The initial connection timestamp is used as the watchdog baseline until the
first valid response, so a silent/open COM port is reported disconnected within
the same 50 ms budget.

## Measurements

The repository contains simulation and optional hardware integration tests. The
hardware tests are enabled by setting `TAJPAN_HARDWARE_COM` to the real COM
port; they are skipped when that variable is not present. Record measured
values here when the FOH/RS485 hardware run is available:

| Metric | Result |
|---|---:|
| Polling average / min / max / jitter | pending hardware run |
| Response average / median / p95 / max | pending hardware run |
| Disconnect latency | pending hardware run |
| Reconnect latency | pending hardware run |
| False disconnect count | pending hardware run |
| Test duration | pending hardware run |
