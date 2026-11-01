# Security and privacy

Do not include secret keys, tokens or identifying scans in public issues. For a
suspected vulnerability, use the repository's private vulnerability reporting
channel when a maintainer enables it. No private reporting address is invented here.

Research needs authorized equipment and informed participants. The application
does not provide covert monitoring, account login, network report retrieval or
unwanted-tracking protections for a complete accessory. Do not use a research
preview as a certified tracking device. Stop advertising in the GUI at any time.

Keys are CurrentUser DPAPI-protected under `%LOCALAPPDATA%/BLEStudio/vault`.
They are separate from profiles, logs and exports. Key files originally imported
are not deleted or encrypted by the app. Other software under the same user can
unprotect DPAPI material; this is local-at-rest protection, not a secure enclave.
Deleting a profile preserves revision/key files; erase its key before deleting
the profile when desired, or deliberately remove the local application data.

Logs and debug reports omit ordinary payloads and vault contents, but error
messages and adapter diagnostics can contain local identifiers or paths. Research
reports deliberately contain advertised public identifiers and imported observations.
Review and redact exported reports before sharing. CSV escaping prevents advertised
names from becoming spreadsheet formulas. JSON imports are bounded, schema-checked
and cannot import vault access through the normal profile Import action.
