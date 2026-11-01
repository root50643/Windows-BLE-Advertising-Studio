using System.Runtime.InteropServices.WindowsRuntime;
using BLEStudio.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation.Metadata;

namespace BLEStudio.Bluetooth;

public sealed class WindowsGattBackend : IBluetoothBackend
{
    private readonly WindowsBluetoothBackend _scanner = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GattServiceProvider? _provider;
    private TaskCompletionSource<string>? _start, _stop;
    private CancellationTokenSource? _duration;
    private string? _requested;
    private bool _starting;
    public string Name => "Windows native GATT ServiceData";
    public event EventHandler<BackendStatus>? AdvertisingStatusChanged;
    public event EventHandler<BackendStatus>? ScanningStatusChanged;
    public event EventHandler<ScanPacket>? PacketReceived;
    public WindowsGattBackend()
    {
        _scanner.ScanningStatusChanged += (_, e) => ScanningStatusChanged?.Invoke(this, e);
        _scanner.PacketReceived += (_, e) => PacketReceived?.Invoke(this, e);
    }
    public IReadOnlyList<ValidationIssue> ValidateProfile(AdvertisementProfile p, AdapterCapabilities adapter) => GattPayloadPolicy.Validate(p, adapter);
    public async Task<AdapterCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        var a = await _scanner.DetectAsync(cancellationToken);
        var api = ApiInformation.IsPropertyPresent("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters", "ServiceData");
        return a with { CanAdvertise = a.CanAdvertise && api, Features = [
            new("GATT ServiceData API", api ? SupportStatus.RequiresVerification : SupportStatus.Unsupported, "API presence is not payload acceptance; GATT may truncate/omit data."),
            new("16/32/128-bit service data", SupportStatus.RequiresVerification, "The service UUID is supplied to GattServiceProvider. On-air UUID width and AD placement require external capture."),
            new("Requested flags", SupportStatus.Unsupported, "Windows generates flags. Input flags are preview requirements, not raw API fields."),
            new("Manufacturer/custom data", SupportStatus.Unsupported, "Use Windows Publisher instead."),
            new("MAC / address rotation", SupportStatus.Unsupported, "Managed by Windows; EID/address synchronization is not guaranteed."),
            new("GATT extended request", a.ExtendedAdvertising ? SupportStatus.RequiresVerification : SupportStatus.Unsupported, "Requests secondary 1M PHY when API exists; actual transport requires external verification.") ] };
    }
    public async Task StartAdvertisingAsync(AdvertisementProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_provider != null) throw new BluetoothOperationException("AlreadyActive", "Stop GATT before starting another request.");
            var p = ProfileJson.Clone(profile); var caps = await DetectAsync(cancellationToken);
            var errors = ValidateProfile(p, caps).Where(x => x.Severity == ValidationSeverity.Error).ToArray();
            if (errors.Length > 0) throw new BluetoothOperationException("ValidationFailed", string.Join("\n", errors.Select(x => x.Message)));
            var submission = GattPayloadPolicy.Build(p);
            var result = await GattServiceProvider.CreateAsync(submission.ServiceUuid);
            if (result.Error != BluetoothError.Success) throw new BluetoothOperationException(result.Error.ToString(), "GATT CreateAsync failed: " + result.Error);
            _provider = result.ServiceProvider; _requested = submission.RequestedAdHex; _starting = true;
            _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _provider.AdvertisementStatusChanged += StatusChanged;
            var parameters = new GattServiceProviderAdvertisingParameters { IsConnectable = p.Connectable ?? false, IsDiscoverable = p.Scannable ?? true, ServiceData = submission.ServiceData.AsBuffer() };
            if (p.UseExtendedAdvertising)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || !ApiInformation.IsPropertyPresent("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters", "UseLowEnergyUncoded1MPhyAsSecondaryPhy"))
                    throw new BluetoothOperationException("Unsupported", "GATT secondary PHY request is unavailable on this Windows version.");
                parameters.UseLowEnergyUncoded1MPhyAsSecondaryPhy = true;
            }
            AdvertisingStatusChanged?.Invoke(this, new(OperationState.Starting, $"GATT request: service {submission.ServiceUuid}, {submission.ServiceData.Length} service-data bytes. Windows generates UUID encoding and flags."));
            _provider.StartAdvertising(parameters);
            var status = await _start.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            _starting = false;
            GattPayloadPolicy.RequireCompleteStatus(status);
            if (p.DurationSeconds > 0) { _duration = new(); _ = StopLaterAsync(p.DurationSeconds, _duration.Token); }
        }
        catch (Exception ex)
        {
            if (ex is BluetoothOperationException { Code: "AlreadyActive" }) throw;
            Release(); AdvertisingStatusChanged?.Invoke(this, new(OperationState.Aborted, ex.Message, ex is BluetoothOperationException b ? b.Code : $"0x{ex.HResult:X8}")); throw;
        }
        finally { _gate.Release(); }
    }
    private void StatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _provider)) return;
        // Some Windows builds deliver an initial Aborted/Success notification before the actual start result.
        // Wait for a conclusive result, while never treating the intermediate notification as acceptance.
        if (_starting && e.Status == GattServiceProviderAdvertisementStatus.Aborted && e.Error == BluetoothError.Success)
        { AdvertisingStatusChanged?.Invoke(this, new(OperationState.Waiting, "GATT intermediate Aborted/Success; awaiting a complete start result.")); return; }
        var text = e.Status.ToString();
        if (e.Status is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData or GattServiceProviderAdvertisementStatus.Aborted)
            _start?.TrySetResult(text);
        if (e.Status is GattServiceProviderAdvertisementStatus.Stopped or GattServiceProviderAdvertisementStatus.Aborted) _stop?.TrySetResult(text);
        var complete = e.Status == GattServiceProviderAdvertisementStatus.Started;
        AdvertisingStatusChanged?.Invoke(this, new(complete ? OperationState.Started : e.Status == GattServiceProviderAdvertisementStatus.Stopped ? OperationState.Stopped : OperationState.Aborted,
            $"GATT {text}: {e.Error}. UUID encoding, primary/scan-response placement and RF bytes remain unverified.",
            e.Status == GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData ? "IncompleteAdvertisement" : e.Error == BluetoothError.Success ? null : e.Error.ToString(),
            complete ? DateTimeOffset.UtcNow : null, complete ? _requested : null));
    }
    public async Task StopAdvertisingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _duration?.Cancel(); _duration?.Dispose(); _duration = null;
            if (_provider == null) return;
            _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_provider.AdvertisementStatus is not (GattServiceProviderAdvertisementStatus.Stopped or GattServiceProviderAdvertisementStatus.Aborted))
            {
                _provider.StopAdvertising(); await _stop.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            AdvertisingStatusChanged?.Invoke(this, new(OperationState.Stopped, "GATT stopped."));
        }
        finally { Release(); _gate.Release(); }
    }
    private void Release()
    {
        if (_provider != null) { _provider.AdvertisementStatusChanged -= StatusChanged; try { _provider.StopAdvertising(); } catch (Exception) { } _provider = null; }
        _requested = null;
        _starting = false;
    }
    private async Task StopLaterAsync(int seconds, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); await StopAdvertisingAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AdvertisingStatusChanged?.Invoke(this, new(OperationState.Aborted, ex.Message)); }
    }
    public Task StartScanningAsync(bool active = true, CancellationToken cancellationToken = default) => _scanner.StartScanningAsync(active, cancellationToken);
    public Task StopScanningAsync() => _scanner.StopScanningAsync();
    public async ValueTask DisposeAsync() { try { await StopAdvertisingAsync(); } finally { await _scanner.DisposeAsync(); _gate.Dispose(); } }
}
