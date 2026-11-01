# Third-party notices

Application source is MIT licensed. The application uses Microsoft .NET / WPF
and C#/WinRT runtime components (MIT) and Windows SDK .NET reference/runtime
assets under their respective Microsoft package terms. Windows Bluetooth APIs
are operating system services. System.Security.Cryptography.ProtectedData is
MIT licensed. Test-only dependencies are xUnit (Apache-2.0), its Visual Studio
runner (Apache-2.0), and Microsoft.NET.Test.Sdk (MIT). NuGet package license
metadata and bundled runtime notices remain authoritative.

Windows protocol formats are independently implemented from public references.
The candidate also contains the following components/dependencies:

- BouncyCastle.Cryptography 2.7.0, Bouncy Castle license (MIT-based), used for secp160r1/P-224 operations. Full license: `docs/BouncyCastle-LICENSE.md` and packaged runtime notices.
- Leaflet 1.9.4, BSD-2-Clause, vendored under `services/tracking-service/static/vendor/` with its license.
- FastAPI (MIT), Uvicorn (BSD-3-Clause), HTTPX (BSD-3-Clause), argon2-cffi (MIT), cryptography (Apache-2.0/BSD); Python server dependencies installed by the Docker build. Package metadata is authoritative.
- `services/tracking-service/google_adapter.py` is a separate GPL-3.0-or-later component derived from GoogleFindMyTools by Leon Böttger. Its complete source and GPL text (`LICENSE-google-adapter`) are distributed. It communicates with the MIT API through JSON subprocess IPC.
- The Google provider image installs GoogleFindMyTools (GPL-3.0) at commit 660be5395702ff7f1a6174f4ec4c865a57c7b527. The exact source is fetched by `deploy/Dockerfile`; upstream license and source remain in the image. Preserve corresponding source/license when redistributing an image.
- The optional Apple image installs Macless Haystack (AGPL-3.0) at commit 0bda271b8bd0cc37194dfba1845cd48954cba4cc. `deploy/Dockerfile.apple` fetches that source and retains it. Its source/license and network-source obligations remain applicable.
- Optional Anisette and Caddy are separate third-party images with their respective upstream licenses. Review/pin image digests for a deployment's update policy.
- OpenStreetMap map data/tiles require attribution and compliance with the tile usage policy. No map data or location reports are bundled.

Additional format references:

- OpenHaystack by SEEMOO Lab (AGPL-3.0), firmware format reference at
  ea05dadb9e6e71075ef08afb5c2087302fbb7210.
- GoogleFindMyTools by Leon Böttger, reference reviewed at
  660be5395702ff7f1a6174f4ec4c865a57c7b527.
- Google's Find Hub Network Accessory specification v1.3.

Apple, AirTag, Find My, Google, Find Hub, Bluetooth and Windows are trademarks
of their respective owners. This project is independent and not endorsed or
certified by those organizations.
