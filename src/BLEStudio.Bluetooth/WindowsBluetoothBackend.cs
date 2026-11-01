using System.Runtime.InteropServices.WindowsRuntime;
using BLEStudio.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;
using Microsoft.Win32;

namespace BLEStudio.Bluetooth;

public sealed class WindowsBluetoothBackend : IBluetoothBackend
{
    private const string PublisherClass = "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher";
    private const string AdapterClass = "Windows.Devices.Bluetooth.BluetoothAdapter";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BluetoothLEAdvertisementPublisher? _publisher;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _duration;
    private TaskCompletionSource<bool>? _startCompletion;
    private TaskCompletionSource<bool>? _stopCompletion;
    private TaskCompletionSource<bool>? _scanStopCompletion;
    private string? _submittedHex;
    private DateTimeOffset? _startedAt;
    public string Name => "Windows Runtime BLE";
    public event EventHandler<BackendStatus>? AdvertisingStatusChanged;
    public event EventHandler<BackendStatus>? ScanningStatusChanged;
    public event EventHandler<ScanPacket>? PacketReceived;

    public async Task<AdapterCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return Missing("Windows did not return a default Bluetooth adapter.");
            var radio = await adapter.GetRadioAsync();
            var state = radio?.State.ToString() ?? "Unknown";
            string name = radio?.Name ?? "Bluetooth adapter", pnp = "Unknown", driver = "Unknown";
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId,
                    new[] { "System.Devices.DeviceInstanceId", "System.Devices.DriverVersion" });
                if (!string.IsNullOrWhiteSpace(info.Name)) name = info.Name;
                if (info.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id)) pnp = id?.ToString() ?? pnp;
                if (info.Properties.TryGetValue("System.Devices.DriverVersion", out var version)) driver = version?.ToString() ?? driver;
            }
            catch (Exception) { /* Optional metadata is not a prerequisite for BLE operation. */ }
            // Device interfaces do not always return PnP metadata. Read the matching local
            // device registry entry without enumerating or changing any driver settings.
            if (pnp == "Unknown" && adapter.DeviceId.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                var parts = adapter.DeviceId[4..].Split('#');
                if (parts.Length >= 3) pnp = string.Join(@"\", parts.Take(3));
            }
            try
            {
                using var deviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
                if (deviceKey?.GetValue("FriendlyName") is string friendly) name = friendly;
                if (deviceKey?.GetValue("Driver") is string driverKey)
                {
                    using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                    driver = classKey?.GetValue("DriverVersion") as string ?? driver;
                    if (classKey?.GetValue("DriverDesc") is string description) name = description;
                }
            }
            catch (Exception) { /* Metadata remains Unknown when registry read access is unavailable. */ }
            var ext = ApiInformation.IsPropertyPresent(AdapterClass, "IsExtendedAdvertisingSupported") && adapter.IsExtendedAdvertisingSupported;
            var features = CommonFeatures();
            features.Add(new("BLE advertising", adapter.IsLowEnergySupported && adapter.IsPeripheralRoleSupported ? SupportStatus.Supported : SupportStatus.Unsupported,
                "Based on adapter LE and peripheral-role flags. Publisher Started still requires a runtime check."));
            features.Add(new("BLE scanning", adapter.IsLowEnergySupported ? SupportStatus.Supported : SupportStatus.Unsupported, "LE adapter flag; watcher may still be denied by policy."));
            features.Add(new("Extended advertising", ext ? SupportStatus.Supported : SupportStatus.Unsupported, "Reported by BluetoothAdapter, not inferred from model."));
            AddExtended("Header TX power", "IncludeTransmitPowerLevel");
            AddExtended("Preferred TX power", "PreferredTransmitPowerLevelInDBm");
            AddExtended("Anonymous", "IsAnonymous");
            var phyApi = ApiInformation.IsPropertyPresent(PublisherClass, "PrimaryPhy") && ApiInformation.IsPropertyPresent(PublisherClass, "SecondaryPhy")
                && typeof(BluetoothLEAdvertisementPublisher).GetProperty("PrimaryPhy") != null && typeof(BluetoothLEAdvertisementPublisher).GetProperty("SecondaryPhy") != null;
            features.Add(new("PHY selection", ext && phyApi ? SupportStatus.RequiresVerification : SupportStatus.Unsupported, "Requires extended advertising, runtime PHY properties and controller support."));
            AddPhy("LE Coded PHY", "IsLowEnergyCodedPhySupported");
            AddPhy("LE 2M PHY", "IsLowEnergyUncoded2MPhySupported");
            return new()
            {
                Available = true, Name = name, DeviceId = adapter.DeviceId, PnpDeviceId = pnp, DriverVersion = driver,
                RadioState = state, CanAdvertise = radio?.State == RadioState.On && adapter.IsLowEnergySupported && adapter.IsPeripheralRoleSupported,
                CanScan = radio?.State == RadioState.On && adapter.IsLowEnergySupported, ExtendedAdvertising = ext,
                MaxExtendedLength = ext ? checked((int)adapter.MaxAdvertisementDataLength) : 31, Features = features
            };
            void AddExtended(string feature, string property) => features.Add(new(feature,
                ext && ApiInformation.IsPropertyPresent(PublisherClass, property) ? SupportStatus.RequiresVerification : SupportStatus.Unsupported,
                "API presence + extended adapter flag. Actual header/power requires independent measurement."));
            void AddPhy(string feature, string property)
            {
                var value = typeof(BluetoothAdapter).GetProperty(property)?.GetValue(adapter) as bool?;
                features.Add(new(feature, value switch { true => SupportStatus.Supported, false => SupportStatus.Unsupported, _ => SupportStatus.Unknown }, "Adapter PHY capability property, if available on this Windows build."));
            }
        }
        catch (Exception ex) { return Missing($"Adapter query failed ({Error(ex)}): {ex.Message}"); }
    }
    private static AdapterCapabilities Missing(string reason) => new()
    {
        Features = [.. CommonFeatures(), new("BLE advertising", SupportStatus.Unsupported, reason), new("BLE scanning", SupportStatus.Unsupported, reason)]
    };
    private static List<Capability> CommonFeatures() =>
    [
        new("Manufacturer data", SupportStatus.RequiresVerification, "0xFF is allowed by Publisher. Radio, payload size and policy must still accept it."),
        new("Custom AD types", SupportStatus.RequiresVerification, "Only non-reserved types may be submitted. API acceptance is measured on Start."),
        new("Local name / flags / service UUIDs / service data / solicitation / appearance", SupportStatus.Unsupported, "Reserved AD types in BluetoothLEAdvertisementPublisher. Available for editing, parsing and export."),
        new("Advertising interval", SupportStatus.Unsupported, "Windows advertising policy schedules broadcasts; Publisher has no interval property."),
        new("Connectable / scannable type", SupportStatus.Unsupported, "Publisher does not expose these flags. GATT advertising is a different backend."),
        new("MAC address / address type / rotation", SupportStatus.Unsupported, "Controlled by Windows. Publisher cannot select or synchronize addresses."),
        new("Scan response", SupportStatus.Unsupported, "No public Publisher scan-response configuration API."),
        new("Duration", SupportStatus.Supported, "Application timer requests Stop. This is not a controller timing guarantee."),
        new("Concurrent protocol advertising", SupportStatus.Unknown, "This application uses one publisher and switches profiles sequentially."),
        new("Over-the-air payload equality", SupportStatus.RequiresVerification, "Only an independent capture can establish transmitted bytes.")
    ];

    public async Task StartAdvertisingAsync(AdvertisementProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_publisher != null) throw new BluetoothOperationException("AlreadyActive", "Stop the current publisher before switching profiles.");
            var p = ProfileJson.Clone(profile);
            var adapter = await DetectAsync(cancellationToken);
            var issues = WindowsPayloadPolicy.Validate(p, adapter).Where(x => x.Severity == ValidationSeverity.Error).ToArray();
            if (issues.Length > 0) throw new BluetoothOperationException("ValidationFailed", string.Join(Environment.NewLine, issues.Select(x => x.Message)));
            var advertisement = new BluetoothLEAdvertisement();
            foreach (var ad in AdCodec.Decode(Hex.Parse(p.PayloadHex)))
            {
                if (ad.Type == 0xFF)
                    advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData((ushort)(ad.Data[0] | ad.Data[1] << 8), ad.Data[2..].AsBuffer()));
                else advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(ad.Type, ad.Data.AsBuffer()));
            }
            var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
            _publisher = publisher;
            publisher.UseExtendedAdvertisement = p.UseExtendedAdvertising;
            if (p.IncludeTransmitPower) publisher.IncludeTransmitPowerLevel = true;
            if (p.Anonymous) publisher.IsAnonymous = true;
            if (p.PreferredTransmitPowerDbm != null) publisher.PreferredTransmitPowerLevelInDBm = p.PreferredTransmitPowerDbm;
            SetPhy(publisher, "PrimaryPhy", p.PrimaryPhy);
            SetPhy(publisher, "SecondaryPhy", p.SecondaryPhy);
            _submittedHex = Hex.Format(Hex.Parse(p.PayloadHex));
            _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.StatusChanged += OnPublisherStatus;
            AdvertisingStatusChanged?.Invoke(this, new(OperationState.Starting, "Waiting for Windows Publisher status."));
            publisher.Start();
            await _startCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (p.DurationSeconds > 0)
            {
                _duration = new();
                _ = StopAfterAsync(p.DurationSeconds, _duration.Token);
            }
        }
        catch (Exception ex)
        {
            if (ex is BluetoothOperationException { Code: "AlreadyActive" }) throw;
            ReleasePublisher();
            AdvertisingStatusChanged?.Invoke(this, new(OperationState.Aborted, ex.Message, Error(ex)));
            throw;
        }
        finally { _gate.Release(); }
    }
    private static void SetPhy(BluetoothLEAdvertisementPublisher publisher, string property, PhyPreference phy)
    {
        if (phy == PhyPreference.Default) return;
        if (!ApiInformation.IsPropertyPresent(PublisherClass, property)) throw new BluetoothOperationException("Unsupported", $"{property} is absent on this Windows build.");
        var info = typeof(BluetoothLEAdvertisementPublisher).GetProperty(property) ?? throw new BluetoothOperationException("Unsupported", $"{property} is not present in this SDK projection.");
        // Windows enum values are Unspecified=0, LE1M=1, LE2M=2, LECoded=3.
        info.SetValue(publisher, Enum.ToObject(info.PropertyType, (int)phy));
    }
    private void OnPublisherStatus(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _publisher)) return;
        var state = args.Status switch
        {
            BluetoothLEAdvertisementPublisherStatus.Started => OperationState.Started,
            BluetoothLEAdvertisementPublisherStatus.Stopped => OperationState.Stopped,
            BluetoothLEAdvertisementPublisherStatus.Stopping => OperationState.Stopping,
            BluetoothLEAdvertisementPublisherStatus.Waiting => OperationState.Waiting,
            BluetoothLEAdvertisementPublisherStatus.Aborted => OperationState.Aborted,
            _ => OperationState.Starting
        };
        if (state == OperationState.Started)
        {
            _startedAt = DateTimeOffset.UtcNow;
            _startCompletion?.TrySetResult(true);
        }
        if (state is OperationState.Aborted or OperationState.Stopped)
        {
            _startCompletion?.TrySetException(new BluetoothOperationException(args.Error.ToString(), $"Publisher {state}: {args.Error}."));
            _stopCompletion?.TrySetResult(true);
        }
        AdvertisingStatusChanged?.Invoke(this, new(state, $"Windows Publisher: {args.Status}. This does not verify reception or location reports.", args.Error == BluetoothError.Success ? null : args.Error.ToString(), _startedAt,
            state == OperationState.Started ? _submittedHex : null));
    }
    public async Task StopAdvertisingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _duration?.Cancel(); _duration?.Dispose(); _duration = null;
            if (_publisher == null) return;
            _stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_publisher.Status is not (BluetoothLEAdvertisementPublisherStatus.Stopped or BluetoothLEAdvertisementPublisherStatus.Aborted))
            {
                _publisher.Stop();
                await _stopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            ReleasePublisher();
            AdvertisingStatusChanged?.Invoke(this, new(OperationState.Stopped, "Publisher stopped."));
        }
        finally { _gate.Release(); }
    }
    private void ReleasePublisher()
    {
        if (_publisher == null) return;
        _publisher.StatusChanged -= OnPublisherStatus;
        try { _publisher.Stop(); } catch (Exception) { }
        _publisher = null; _submittedHex = null; _startedAt = null;
    }
    private async Task StopAfterAsync(int seconds, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); await StopAdvertisingAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AdvertisingStatusChanged?.Invoke(this, new(OperationState.Aborted, ex.Message, Error(ex))); }
    }
    public async Task StartScanningAsync(bool active = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_watcher != null) throw new BluetoothOperationException("AlreadyActive", "Scanner is already running.");
            var adapter = await DetectAsync(cancellationToken);
            if (!adapter.CanScan) throw new BluetoothOperationException("AdapterUnavailable", "BLE scanner unavailable. Check the adapter, radio and Windows permissions.");
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive };
            if (ApiInformation.IsPropertyPresent("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher", "AllowExtendedAdvertisements")) _watcher.AllowExtendedAdvertisements = true;
            _watcher.Received += OnReceived; _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();
            // Start dispatches to Windows asynchronously; Status may still be Created on return.
            for (var attempt = 0; attempt < 100 && _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created; attempt++)
                await Task.Delay(50, cancellationToken);
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) throw new BluetoothOperationException("ScannerAborted", $"Watcher did not enter Started: {_watcher.Status}.");
            ScanningStatusChanged?.Invoke(this, new(OperationState.Started, "Windows BLE watcher started. Received records are Windows-delivered AD sections."));
        }
        catch (Exception ex)
        {
            if (ex is BluetoothOperationException { Code: "AlreadyActive" }) throw;
            ReleaseWatcher();
            ScanningStatusChanged?.Invoke(this, new(OperationState.Aborted, ex.Message, Error(ex))); throw;
        }
        finally { _gate.Release(); }
    }
    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            // DataSections is the canonical Windows raw view. Do not also append ManufacturerData / ServiceUuids.
            var ads = args.Advertisement.DataSections.Select(x => new AdStructure(x.DataType, x.Data.ToArray())).ToArray();
            var address = args.BluetoothAddress.ToString("X12");
            address = string.Join(":", Enumerable.Range(0, 6).Select(i => address.Substring(i * 2, 2)));
            PacketReceived?.Invoke(this, new ScanPacket
            {
                Timestamp = args.Timestamp, Address = address, AddressType = args.BluetoothAddressType.ToString(),
                Name = args.Advertisement.LocalName ?? "", Rssi = args.RawSignalStrengthInDBm,
                AdvertisementType = args.AdvertisementType.ToString(), PayloadHex = Hex.Format(AdCodec.Encode(ads))
            });
        }
        catch (Exception ex) { ScanningStatusChanged?.Invoke(this, new(OperationState.Started, $"Discarded malformed received record: {ex.Message}", Error(ex))); }
    }
    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        _scanStopCompletion?.TrySetResult(true);
        ScanningStatusChanged?.Invoke(this, new(args.Error == BluetoothError.Success ? OperationState.Stopped : OperationState.Aborted,
            $"Watcher stopped: {args.Error}", args.Error == BluetoothError.Success ? null : args.Error.ToString()));
    }
    public async Task StopScanningAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_watcher == null) return;
            _scanStopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
                await _scanStopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            ReleaseWatcher();
        }
        finally { _gate.Release(); }
    }
    private void ReleaseWatcher()
    {
        if (_watcher == null) return;
        _watcher.Received -= OnReceived; _watcher.Stopped -= OnWatcherStopped;
        try { _watcher.Stop(); } catch (Exception) { }
        _watcher = null;
    }
    private static string Error(Exception ex) => ex is BluetoothOperationException b ? b.Code : $"0x{ex.HResult:X8}";
    public async ValueTask DisposeAsync()
    {
        try { await StopAdvertisingAsync(); } finally { await StopScanningAsync(); }
        _gate.Dispose();
    }
}
