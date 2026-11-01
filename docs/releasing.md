# Releasing

Repository: [root50643/Windows-BLE-Advertising-Studio](https://github.com/root50643/Windows-BLE-Advertising-Studio).
Check [Releases](https://github.com/root50643/Windows-BLE-Advertising-Studio/releases)
and [Actions](https://github.com/root50643/Windows-BLE-Advertising-Studio/actions)
for actual publication and workflow results. Never claim an upload before verifying it.

1. Create or choose an authorized GitHub repository and push this tested source.
2. Enable repository Actions. The workflow uses a Windows runner and .NET 10.
3. Run `scripts/build.ps1 -Version X.Y.Z` and `scripts/test-ui.ps1` locally.
4. Inspect the ZIP, version, EXE size and SHA256SUMS; optionally sign the EXE
   before regenerating checksums and the ZIP if signing infrastructure is available.
5. Push the matching `vX.Y.Z` tag. `release.yml` rebuilds/tests/publishes, verifies
   the bundled WPF GUI using mock BLE, then creates the GitHub Release with the
   standalone EXE, ZIP, checksum file and build manifest using the
   repository token with contents:write. The token is only passed via environment.
6. Verify the actual published Release and asset URLs before updating Download.

`build.ps1` writes the single-file self-contained win-x64 WinExe and all required
sidecars/documents under `artifacts/release/Windows-BLE-Advertising-Studio-win-x64`.
The ZIP includes that complete directory. Keep all published sidecars; the
application has trimming disabled. `build-info.json` records version, bytes and
EXE SHA256; `SHA256SUMS.txt` uses the downloaded filenames `BLEStudio.exe` and the
versioned ZIP so assets can be checked in one folder. Both READMEs are bundled.
Generated outputs are ignored
by Git and uploaded as Actions/Release artifacts instead of committed.

The local build is unsigned. Signing and SmartScreen reputation are not claimed.
GitHub workflow syntax is included, but a successful GitHub-hosted run can only
be claimed after the repository actually runs it.
