# Local validation — 2026-11-01

This report describes checks actually run in the development workspace. It
does not claim a GitHub-hosted workflow, public release, external RF capture or
Apple/Google network report. Build output, TRX and raw local hardware diagnostics
are under ignored `artifacts/` directories and are not committed as personal data.

## Build and automated tests

- .NET SDK 10.0.403; C# / WPF; Windows x64.
- Solution Release build succeeded with zero warnings and zero errors.
- xUnit: **81 passed, 0 failed, 0 skipped** (69 unit + 12 integration).
- Running WPF GUI with explicit mock BLE: **16 smoke checks passed**, covering
  bound commands, payload validation, start/stop, scan filtering, RSSI samples,
  profiles/revisions and Apple/Google research. All seven pages plus light theme
  were rendered. Screenshots in `assets/screenshots` are actual application renders.
- Release scripts generate a self-contained single-file EXE, complete ZIP,
  `build-info.json` and `SHA256SUMS.txt`. See those generated files for exact
  binary size and hash, which change when source or runtime versions change.
- The generated ZIP was extracted to a separate folder and its EXE passed the
  same 16 checks with `DOTNET_ROOT` pointing to a nonexistent runtime directory
  and multilevel lookup disabled. The extracted EXE matched the packaged SHA256.
- Native mouse-driven desktop verification was attempted through the computer-use
  helper, but it returned `GetCursorPos failed: Access denied (0x80070005)` before
  launching the target. No manual click-through result is claimed. WPF executable
  tests and rendering above completed independently of that desktop restriction.

## Actual Bluetooth API tests

Measured on Windows build **26300**, **Intel(R) Wireless Bluetooth(R)**,
driver **22.150.0.6**. Radio On. Adapter reported extended advertising and a
maximum extended-data length of **160 bytes**, with Coded and 2M PHY flags.
These observations apply to this machine only.

| Requested generic test case | Observed API result |
| --- | --- |
| Legacy manufacturer data, 8 AD bytes | Publisher Started and Stopped |
| Extended manufacturer data, 44 AD bytes | Publisher Started and Stopped |
| Extended header TX power | Publisher Started and Stopped |
| Preferred TX power −8 dBm | Rejected: Access denied, 0x80070005 |
| Extended primary 1M / secondary 2M | Publisher Started and Stopped |
| Extended anonymous / Coded PHY | Publisher Started and Stopped |
| Active BLE scanner for six seconds | Watcher Started / Stopped; **150 received records** in the final recorded matrix run |

The preferred-power error is a recorded runtime denial, not evidence that the
request was applied. Other supported operations continue to work after it.
Actual transmit power, PHY on air, anonymity on air, exact byte ordering and
reception of the publisher by a separate device were **not independently measured**.
The scanner count reflects nearby Windows-delivered packets, not proof that it
received this computer's own test beacon. Only reserved/test generic manufacturer
payloads were used; no Apple/Google tracking identity was transmitted.

## Research boundaries

Apple format parsing and public-key/address previews were tested with synthetic
data. A matching key-derived MAC cannot be set through this backend; Find My
accessory behavior and location-report retrieval are not implemented or verified.

Google FHN framing, EID widths, optional/required hashed-flags rules and reserved
AD-type rejection were tested with synthetic data. Secret EIK storage was tested
through DPAPI. EID derivation, registration, provisioning, DULT, official app
visibility and location reporting are not implemented or verified.

## Publication verification

The local measurements above precede publication and do not establish a hosted
CI result. The public repository is
[root50643/Windows-BLE-Advertising-Studio](https://github.com/root50643/Windows-BLE-Advertising-Studio).
See [Actions](https://github.com/root50643/Windows-BLE-Advertising-Studio/actions)
for hosted checks and [Releases](https://github.com/root50643/Windows-BLE-Advertising-Studio/releases)
for published assets and their matching checksums. Existing Git Credential Manager
authorization is used for the initial push; no token is included in source or artifacts.
