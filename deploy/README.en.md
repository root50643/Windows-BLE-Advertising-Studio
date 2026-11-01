# Self-hosted location service

[繁體中文](README.md)

The service supplies owner login, revocable desktop credentials, SQLite history, Windows/mobile browser maps and Apple/Google report providers. **Only the Windows PC advertises Bluetooth.** The Linux/VPS server needs no Bluetooth device or firmware.

## Deploy

Use Linux, Docker Engine, Compose v2 and Python 3. Point a public DNS hostname to the VPS and open TCP 80/443 (UDP 443 optional).

```sh
cd deploy
python3 setup.py
docker compose up -d --build
docker compose ps
```

Setup asks for the hostname and an owner password of at least 16 characters. It creates private `secrets/`, `data/`, `apple-data/` and `.env`; existing configuration is never overwritten. Sign into `https://your-hostname` as `owner`. Caddy obtains a public certificate after DNS and firewall prerequisites succeed.

Download a desktop credential from Owner settings and import it on the Windows Tracker page. Remove the plaintext download after import; Windows stores it using CurrentUser DPAPI. Desktop credentials expire after 90 days and can be revoked immediately. They cannot authorize provider accounts or issue tokens.

## Authorize your Google account

Complete initial authorization on your own computer with Chrome, Python and your own Google/Android account. Enter account credentials and owner-key prompts locally; never send them through chat or an Issue.

```sh
git clone https://github.com/leonboe1/GoogleFindMyTools.git
git -C GoogleFindMyTools checkout 660be5395702ff7f1a6174f4ec4c865a57c7b527
python -m venv google-auth-env
# Windows: google-auth-env\Scripts\activate
# Linux/macOS: . google-auth-env/bin/activate
python -m pip install -r GoogleFindMyTools/requirements.txt
python scripts/google-authorize.py --tools GoogleFindMyTools --output google-authorization.json
```

Upload that JSON through your HTTPS owner page, then delete the plaintext export. The helper only obtains authorization; it never registers hardware or generates firmware. Account policy, Chrome changes and unofficial API changes can prevent authorization.

Create a Google identity in Windows and click Register. The server registers that EIK/pair-date and announces approximately four days of changing EIDs in 1024-second periods. It refreshes announcements and queries reports every five minutes. Windows derives the same rotating EIDs. Authentication errors and timeouts remain explicit; coordinates are never fabricated.

The GPL adapter retains upstream's development model ID/custom encrypted-key format. This is **not a Fast Pair certified accessory**. The official Android Find Hub app is not guaranteed to display/decrypt this custom format; use the HTTPS mobile page for the same data shown on Windows. Ringing, DULT and full accessory GATT behavior are unimplemented. Registration does not resolve Windows-managed address limitations or prove network tracking.

## Authorize your Apple account

The Apple provider queries a private Macless Haystack endpoint and verifies/decrypts reports with your own P-224 private key. Neither endpoint port 6176 nor Anisette port 6969 is published.

```sh
cd deploy
docker compose --profile apple build
docker compose --profile apple up -d anisette
docker compose --profile apple run --rm apple
```

Complete Apple ID/SMS 2FA yourself in the upstream interactive prompts. After authorization and entry into serving mode, stop the interactive container with Ctrl+C and run:

```sh
docker compose --profile apple up -d
```

Enter `endpoint_user`/`endpoint_pass` from `apple-data/config.ini` on the HTTPS owner page. These are endpoint credentials, **not your Apple ID password**. Protect `apple-data/auth.json`. Upstream account failures, including Apple 503 responses, remain failures; there is no account-protection bypass.

Create/import an owned Apple identity in Windows and register it with your server. A public-key/actual-address mismatch still prevents native Windows network compatibility. Report decryption cannot repair an incompatible Bluetooth address.

## Data and protection

- No reports means “No location reports”; maps load only after valid decrypted coordinates exist. Every report retains time, provider and accuracy. Signed Apple coordinates and GCM integrity are checked.
- History retention is 30 days; provider polling is five minutes, web cache polling one minute. Network failures retain prior reports and their original timestamps.
- Identity secrets, Google authorization and endpoint credentials use purpose-bound AES-256-GCM envelopes in SQLite. The master key is a separate read-only Docker secret. Setup creates private directories/files.
- Third-party Macless/Anisette account files rely on private host directories/volumes, not the API's AES-GCM database. Use encrypted VPS storage/backups and restrict administrator access.
- Worker/API logs exclude account/key/request contents. Per-worker account directories are private and removed afterwards; timed-out workers are terminated.
- Owner login uses Argon2, rate limits, Secure/HttpOnly/SameSite=Strict cookies and a CSRF header. Only token hashes are stored. Desktop tokens are revocable.
- Leaflet is vendored with its license. OSM attribution remains visible; viewing maps requests OSM tiles. No bulk/offline prefetch is performed.

Back up `data/`, `secrets/`, `.env`, `apple-data/` and the Anisette volume using encrypted, access-controlled backups. Losing the master key prevents decryption of stored identities. Never distribute a configured deployment directory, account exports or backups.

Offline API/crypto tests and Compose configuration checks are available. Public VPS/HTTPS issuance, real account authorization and location reports still require verification in your environment. Full native FHN advertising failed on the tested PC; see [native results](../docs/native-tracking-validation.md).
