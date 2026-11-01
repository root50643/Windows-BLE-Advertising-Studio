# Contributing

Use a Windows x64 machine and .NET 10 SDK. Build with `scripts/build.ps1`, run
the xUnit suites and `scripts/test-ui.ps1` before submitting changes. Hardware
checks are optional, explicit and reported separately; CI must run with mock BLE.

Keep platform code behind IBluetoothBackend and WPF independent of WinRT types.
Preserve strict schemas and byte-order tests. Add tests for meaningful codec,
policy, state or persistence changes. Do not represent an unsupported request
as applied. Explain the API/controller evidence behind new capability states.

Use the English localization dictionary for new UI strings. Never commit keys,
tokens, account cookies, unredacted scan exports or local DPAPI files. Submit
protocol changes with a primary reference and a public synthetic format fixture.
Respect reference code licenses; do not copy AGPL code into MIT source.

PRs should state the user-visible change, relevant limitations, automatic checks
run, and whether any real radio or external capture validation was performed.
