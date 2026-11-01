using BLEStudio.Core;

namespace BLEStudio.Protocols;

public sealed class TrackerSession(Func<ResearchProtocol, IBluetoothBackend> factory, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBluetoothBackend? _backend;
    private CancellationTokenSource? _rotation;
    private TrackerIdentity? _identity;
    private byte[]? _secret;
    private bool _extended, _suspended;
    public bool RequestedActive { get; private set; }
    public bool Running { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public AdvertisementProfile? CurrentProfile { get; private set; }
    public TrackingCapability Capability { get; private set; } = new();
    public string Status { get; private set; } = "已停止 / Stopped";
    public event EventHandler? Changed;
    private void Notify(string text) { Status = text; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task StartAsync(TrackerIdentity identity, byte[] secret, bool extended = false, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await StopRadioAsync();
            var copy = secret.ToArray(); if (_secret != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
            _identity = identity; _secret = copy; _extended = extended; RequestedActive = true; _suspended = false; StartedAt = _clock.GetUtcNow(); Capability = new();
            try { await StartRadioAsync(token); } catch { RequestedActive = false; throw; }
            if (identity.Protocol == ResearchProtocol.GoogleFindHub) { _rotation = new(); _ = RotateAsync(_rotation.Token); }
        }
        finally { _gate.Release(); }
    }
    private async Task StartRadioAsync(CancellationToken token)
    {
        Capability = new(); Notify("正在重新檢查原生廣播；本次證據尚未驗證 / Checking native advertising; new evidence unverified");
        CurrentProfile = TrackerCryptography.BuildProfile(_identity!, _secret!, _clock.GetUtcNow(), _extended);
        StartedAt = _clock.GetUtcNow();
        _backend = factory(_identity!.Protocol); _backend.AdvertisingStatusChanged += OnStatus;
        try
        {
            var caps = await _backend.DetectAsync(token);
            if (caps.IsMock) throw new BluetoothOperationException("SimulationOnly", "Tracking requires the real Windows radio; simulation cannot verify tracking.");
            await _backend.StartAdvertisingAsync(CurrentProfile, token); Running = true;
            Capability = new(ApiAccepted: true); Notify("API 已接受；空中封包與定位回報尚待驗證 / API accepted; RF and network unverified");
        }
        catch (Exception ex) { await StopRadioAsync(false); Capability = Capability with { ApiAccepted = false, Detail = ex.Message }; Notify(ex.Message); throw; }
    }
    private void OnStatus(object? sender, BackendStatus status)
    {
        if (status.State == OperationState.Aborted) { Running = false; Capability = Capability with { ApiAccepted = false, Detail = status.Message }; Notify(status.Message); }
    }
    private async Task RotateAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var elapsed = (long)(_clock.GetUtcNow() - _identity!.PairDate).TotalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, 1024 - elapsed % 1024)), _clock, token);
                await _gate.WaitAsync(token);
                try { if (RequestedActive && !_suspended) { await StopRadioAsync(false); await StartRadioAsync(token); } }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Running = false; Notify("EID 更新失敗，廣播已停止 / EID update failed: " + ex.Message); }
    }
    private async Task StopRadioAsync(bool cancelRotation = true)
    {
        if (cancelRotation) { _rotation?.Cancel(); _rotation?.Dispose(); _rotation = null; }
        var backend = _backend; _backend = null; Running = false;
        if (backend != null)
        {
            backend.AdvertisingStatusChanged -= OnStatus;
            try { try { await backend.StopAdvertisingAsync(); } finally { await backend.DisposeAsync(); } }
            catch { RequestedActive = false; Capability = Capability with { ApiAccepted = false }; Notify("停止要求失敗；原生無線狀態未知 / Stop failed; native radio state unknown"); throw; }
        }
    }
    public async Task StopAsync() { await _gate.WaitAsync(); try { RequestedActive = false; await StopRadioAsync(); Notify("已停止；退出、休眠及關機時不會廣播 / Stopped"); } finally { _gate.Release(); } }
    public async Task SuspendAsync() { await _gate.WaitAsync(); try { _suspended = true; await StopRadioAsync(); Notify("休眠／睡眠中不會廣播；喚醒後重新檢查 / Suspended"); } finally { _gate.Release(); } }
    public Task ResumeAsync() => RequestedActive && _identity != null && _secret != null ? StartAsync(_identity, _secret, _extended) : Task.CompletedTask;
    public void ImportEvidence(PacketEvidence evidence)
    {
        if (!Running || CurrentProfile == null || _identity == null) throw new InvalidOperationException("Start a real session before verifying a capture.");
        Capability = TrackerCryptography.VerifyPacket(Capability, _identity, CurrentProfile, evidence, StartedAt, _clock.GetUtcNow()); Notify(Capability.Detail);
    }
    public void ResetEvidence()
    {
        if (RequestedActive || Running) throw new InvalidOperationException("Stop before changing identity.");
        if (_secret != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
        _secret = null; _identity = null; CurrentProfile = null; Capability = new(); Notify("已停止 / Stopped");
    }
    public void ApplyReport(LocationReport report) { if (_identity == null) return; var eid = _identity.Protocol == ResearchProtocol.GoogleFindHub && CurrentProfile != null ? Convert.ToHexString(AdCodec.Decode(Hex.Parse(CurrentProfile.PayloadHex)).Single(x => x.Type == 0x16).Data.AsSpan(3, 20)) : null; Capability = TrackerCryptography.VerifyLocation(Capability, _identity, report, StartedAt, eid); Changed?.Invoke(this, EventArgs.Empty); }
    public async ValueTask DisposeAsync() { try { await StopAsync(); } finally { if (_secret != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret); _gate.Dispose(); } }
}
