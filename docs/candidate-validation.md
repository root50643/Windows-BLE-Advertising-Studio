# Candidate checks · 1.1.0-rc.1

2026-11-01. These checks validate software behavior; they do not establish real-account network tracking.

| Check | Result |
| --- | --- |
| .NET unit/integration regression | 106 passed (including the existing 81-test baseline); rotation clears old evidence before the next API request completes; imported Google keys adopt the canonical pairing date |
| Published Windows EXE / WPF commands | 19 passed; all eight pages and light/dark themes rendered |
| Python API / authenticated report cryptography | 15 passed with isolated offline fixtures |
| Chromium desktop / 390px mobile UI | 7 passed: owner login, empty history, explicit provider failure, token download/revocation, mobile overflow, language switch, no JS errors |
| Windows native adapter | Seven bounded cases; full Apple request accepted, full FHN requests incomplete. [Details](native-tracking-validation.md) |
| Linux API image | Built; pinned Google provider imports, protobuf and rotating-EID self-test passed without account credentials |
| Deployment archive | SHA256 checked; generated credentials, databases and account exports excluded |

Native tests use unregistered fixtures. Browser tests use a private local fixture service and never claim a real location fix. Phone RF capture, actual advertising address compatibility, public VPS/HTTPS issuance, owned-account authorization and decrypted network reports remain unverified. Sleep/resume logic is tested without putting the user's computer to sleep.

CI definitions: [Windows](../.github/workflows/build.yml), [Linux service/containers](../.github/workflows/tracking-service.yml). The latter also checks the optional Apple image and Caddy configuration. Source commits and dirty-state metadata are recorded in the candidate's `build-info.json`.
