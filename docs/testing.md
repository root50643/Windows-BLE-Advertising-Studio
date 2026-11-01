# Testing

Unit and integration suites do not require BLE hardware:

```powershell
dotnet test BLEStudio.sln -c Release --logger trx --results-directory artifacts/test-results
```

They exercise hex/AD boundaries and malformed frames, all AD data lengths,
16/32/128-bit UUID byte order, strict JSON/schema handling, manufacturer encoding,
legacy/extended capacity, unavailable radio/adapter, Windows-reserved parameters,
scanner aggregation/retention, protocol parsing and exact observation comparison.
Integration tests exercise mock Publisher/Watcher transitions, cancellation,
duration stop, simulated failure recovery, atomic profiles/revisions, DPAPI
round-trip/reference validation, CSV formula escaping and capture formats.

After publishing, run `scripts/test-ui.ps1` to execute the actual WPF EXE using
an isolated per-run profile directory and explicit mock backend. It checks
connected UI commands, hex validation, start/stop, packet aggregation, filters,
RSSI data, profiles/revisions, protocol previews and seven-page light/dark renders.
This is a running-application view/model/render smoke test, not a claim that every
mouse/keyboard interaction or accessibility path has been automated. PNGs are
actual WPF visual-tree captures without the native window frame.

Run `scripts/test-hardware.ps1` only in an authorized local test environment. It
briefly publishes the generic test payload `07 FF FF FF 01 02 03 04`, stops it,
then tests extended manufacturer-data requests (TX/1M/2M and anonymous/Coded)
when the controller reports extended support, followed by a six-second scan.
It writes adapter evidence, actual status events,
packet count and any failures without retaining nearby device identities.
This test is separate from CI, uses no Apple/Google identity, and does not verify
its own RF bytes. Full Windows/driver coverage and long-running stress tests
require additional machines and hardware.

Read `validation-results.md` for results recorded in this workspace. Do not
interpret mock assertions or controller flags as measurements of radio content.
