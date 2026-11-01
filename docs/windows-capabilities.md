# Windows backend capability matrix

The backend queries Windows APIs, actual controller flags and radio state.
Supported means a capability was reported, not that an independent RF test passed.
RequiresVerification identifies a supported request whose physical effect is
not established. Unknown means the information was not available. Unsupported
means this backend cannot apply the request. Each GUI entry includes its reason.

| Parameter | Behavior |
| --- | --- |
| Manufacturer data (0xFF) | Encoded as BluetoothLEManufacturerData; runtime Start must succeed |
| Non-reserved custom AD types | DataSections; subject to Windows/controller acceptance |
| Extended advertising | Adapter flag + API availability; uses reported maximum length |
| Header TX power | Extended only; API presence, controller and RF verification |
| Preferred TX power | Nullable dBm request; requires extended-capable radio; never claims measured power |
| Anonymous advertising | Extended only; runtime/controller verification |
| Primary / secondary PHY | Runtime API + SDK reflection guard; primary 2M rejected; controller 2M/Coded flags queried |
| Duration | App timer requests Stop; 0 manual; 1–86400 seconds; research 1–300 |
| Connectable / scannable type | Unsupported; selected by Windows advertising policy |
| Interval | Unsupported; no public Publisher setter |
| MAC, public/random choice, rotation | Unsupported; Windows-owned |
| Scan response | Unsupported in Publisher |
| Local name / flags / UUID lists / service data / solicitation / appearance | Editable and parsable, reserved for transmission |
| PnP ID / driver | DeviceInformation then read-only matching PnP registry metadata; Unknown if inaccessible |
| Air payload equality | Requires independent external capture |
| Concurrent Apple/Google | Unknown at controller level; app intentionally uses one publisher |

Publisher-reserved AD types checked before constructing the WinRT request:
`01 02 03 04 05 06 07 08 09 0A 0D 0E 0F 10 11 12 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F 20 21 3D`.
The application never filters these bytes out and then claims the original
payload was submitted.

Legacy validation warns above the conservative 28-byte threshold and permits controlled 29–31-byte submissions. It rejects over 31 bytes. This separates Windows flag reservations from the actual API acceptance limit. Extended
validation uses `MaxAdvertisementDataLength`; Windows still has final authority.

Publisher progression is asynchronous: Starting → Waiting → Started or Aborted.
Start waits up to 15 seconds for Started; Stop waits up to 10 seconds for a
terminal status. Watcher Start may initially return Created, so the backend
waits for Started (up to five seconds) rather than reporting success immediately.

Primary sources (reviewed 2026-11-01):

The separate `WindowsGattBackend` creates a `GattServiceProvider` with the service UUID and passes only the data after the UUID to `ServiceData`. Requested flags remain a preview requirement; Windows generates actual flags. The backend supports connectable/discoverable controls and optionally requests secondary 1M PHY on Windows 26100+. Publisher's reserved-type list is not applied to GATT. Other unsupported Publisher-specific options are rejected instead of dropped. `StartedWithoutAllAdvertisementData` is an explicit failure. UUID width, complete data, primary/scan-response placement and address require an external capture. See [current native results](native-tracking-validation.md).

- [GATT ServiceData and partial-data status](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovideradvertisingparameters.servicedata)

- [Publisher allowed/reserved AD types and properties](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher)
- [BluetoothAdapter capability properties](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothadapter)
- [Header TX power requires extended advertising](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher.includetransmitpowerlevel)
- [Preferred TX power is a request](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher.preferredtransmitpowerlevelindbm)
- [Microsoft advertising sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-universal-samples/bluetoothadvertisement/)
