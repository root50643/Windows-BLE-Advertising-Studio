# Architecture

`BLEStudio.sln` contains five production projects and two xUnit test projects.

| Project | Responsibility |
| --- | --- |
| BLEStudio.Core | Backend interface, capability/profile/packet models, hex and AD codecs, Windows preflight policy, bounded aggregation |
| BLEStudio.Bluetooth | Windows Runtime publisher/watcher and explicit mock backend |
| BLEStudio.Protocols | Apple offline-finding and Google FHN parsing, authorized preview construction, imported evidence comparison |
| BLEStudio.Infrastructure | Atomic profile JSON persistence, revisions, CurrentUser DPAPI vault, scan JSON/CSV |
| BLEStudio.App | WPF views, MVVM commands/state, Dispatcher batching, themes, GUI and hardware diagnostic entry points |

Core/Protocols target portable .NET 10; Windows-dependent projects target
`net10.0-windows10.0.26100.0` with minimum OS build 19041. The stable SDK
projection package is pinned separately to expose newer PHY metadata while
runtime guards preserve compatibility with older supported OS versions.

`IBluetoothBackend` defines capability discovery, async Start/Stop, state events,
packet events and async disposal. New nRF52840, ESP32 or BlueZ implementations
can implement this interface without referencing WPF. Replace/extend backend
registration in StudioViewModel; supply their own truthful validation policy
when extending beyond the current Windows contract. The mock intentionally
enforces Windows payload restrictions so tests do not promise impossible payloads.

The Windows implementation serializes control operations with a semaphore and
awaits Publisher state events. Packet callbacks enqueue immutable records; a
400 ms WPF Dispatcher timer drains at most 1,500 records per tick. Queues,
retention, log count and RSSI samples are bounded. Oldest device aggregates are
evicted on the configured limit. Raw DataSections are encoded once; convenience
ManufacturerData/ServiceUuids are not appended again and cannot duplicate bytes.

Profiles use strict schema 1 JSON and stable GUIDs. Import assigns a new ID;
save snapshots prior revisions and atomically replaces the current file. Keys
have references only; DPAPI ciphertext is stored in a separate per-user vault.
All radio, export, protocol and storage command exceptions surface in the UI.
Optional metadata failures do not make the app unusable.

UI text is held in an English resource dictionary. Add translated dictionaries
with the same keys and select them through LocalizationService; dynamic text
produced by parsers/view models can be moved to the same resource layer as
translation coverage expands. No external localization framework is required.

Protocol code is a packet-analysis module. It does not contain account credentials,
registration clients, private-key generation, EID derivation or location services.
Imports and exports are explicit user actions; no background network traffic is
required by the application.
