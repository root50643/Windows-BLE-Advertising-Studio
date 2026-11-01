using BLEStudio.Core;

namespace BLEStudio.Bluetooth;

public sealed class MockBluetoothBackend : IBluetoothBackend
{
    private CancellationTokenSource? _scanning;
    private Task? _scanTask;
    private CancellationTokenSource? _duration;
    public bool AdapterAvailable { get; set; } = true;
    public bool FailStart { get; set; }
    public OperationState AdvertisingState { get; private set; }
    public string Name => "Mock BLE (simulation only)";
    public event EventHandler<BackendStatus>? AdvertisingStatusChanged;
    public event EventHandler<BackendStatus>? ScanningStatusChanged;
    public event EventHandler<ScanPacket>? PacketReceived;
    public Task<AdapterCapabilities> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AdapterCapabilities
    {
        Name = AdapterAvailable ? "SIMULATED Bluetooth adapter" : "No simulated adapter", IsMock = true,
        Available = AdapterAvailable, CanAdvertise = AdapterAvailable, CanScan = AdapterAvailable, RadioState = AdapterAvailable ? "Simulated on" : "Unavailable",
        ExtendedAdvertising = true, MaxExtendedLength = 254,
        Features = [new("Header TX power", SupportStatus.RequiresVerification, "Mock"), new("Preferred TX power", SupportStatus.RequiresVerification, "Mock"),
            new("Anonymous", SupportStatus.RequiresVerification, "Mock"), new("PHY selection", SupportStatus.RequiresVerification, "Mock"),
            new("LE Coded PHY", SupportStatus.Supported, "Mock"), new("LE 2M PHY", SupportStatus.Supported, "Mock")]
    });
    public async Task StartAdvertisingAsync(AdvertisementProfile p, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AdvertisingState == OperationState.Started) throw new BluetoothOperationException("AlreadyActive", "Mock publisher is active.");
        var issues = WindowsPayloadPolicy.Validate(p, await DetectAsync(cancellationToken)).Where(x => x.Severity == ValidationSeverity.Error).ToArray();
        if (issues.Length > 0) throw new BluetoothOperationException("ValidationFailed", string.Join("; ", issues.Select(x => x.Message)));
        SetState(OperationState.Starting);
        if (FailStart) { SetState(OperationState.Aborted); throw new BluetoothOperationException("MockFailure", "Simulated publisher error."); }
        SetState(OperationState.Started, Hex.Format(Hex.Parse(p.PayloadHex)));
        if (p.DurationSeconds > 0)
        {
            _duration = new();
            _ = StopLater(p.DurationSeconds, _duration.Token);
        }
    }
    private async Task StopLater(int seconds, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); await StopAdvertisingAsync(); } catch (OperationCanceledException) { }
    }
    private void SetState(OperationState state, string? accepted = null)
    {
        AdvertisingState = state;
        AdvertisingStatusChanged?.Invoke(this, new(state, $"SIMULATION: {state}. No radio transmission.", StartedAt: state == OperationState.Started ? DateTimeOffset.UtcNow : null, AcceptedPayloadHex: accepted));
    }
    public Task StopAdvertisingAsync()
    {
        _duration?.Cancel(); _duration?.Dispose(); _duration = null;
        if (AdvertisingState != OperationState.Stopped) { SetState(OperationState.Stopping); SetState(OperationState.Stopped); }
        return Task.CompletedTask;
    }
    public Task StartScanningAsync(bool active = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AdapterAvailable) throw new BluetoothOperationException("AdapterUnavailable", "No simulated adapter.");
        if (_scanning != null) throw new BluetoothOperationException("AlreadyActive", "Mock scanner is active.");
        _scanning = new(); _scanTask = GenerateAsync(_scanning.Token);
        ScanningStatusChanged?.Invoke(this, new(OperationState.Started, "SIMULATION: scanner started."));
        return Task.CompletedTask;
    }
    private async Task GenerateAsync(CancellationToken token)
    {
        try
        {
            var sequence = 0;
            while (!token.IsCancellationRequested)
            {
                for (var i = 0; i < 4; i++) PacketReceived?.Invoke(this, new ScanPacket
                {
                    Address = $"DE:AD:00:00:00:{i:X2}", AddressType = "Simulated", Name = $"SIMULATED Beacon {i + 1}",
                    IsMock = true, Rssi = (short)(-48 - i * 11 + (sequence % 9) - 4), AdvertisementType = "Simulated non-connectable",
                    PayloadHex = $"07 FF FF FF {i:X2} 02 03 04"
                });
                sequence++; await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task StopScanningAsync()
    {
        if (_scanning == null) return;
        _scanning.Cancel(); if (_scanTask != null) await _scanTask;
        _scanning.Dispose(); _scanning = null;
        ScanningStatusChanged?.Invoke(this, new(OperationState.Stopped, "SIMULATION: scanner stopped."));
    }
    public async ValueTask DisposeAsync() { await StopAdvertisingAsync(); await StopScanningAsync(); }
}
