using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BLEStudio.Bluetooth;
using BLEStudio.Core;
using BLEStudio.Infrastructure;
using BLEStudio.Protocols;
using Microsoft.Win32;

namespace BLEStudio.App;

public sealed class TrackerViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly KeyVault _vault;
    private readonly DesktopCredentialStore _credentials;
    private readonly TrackerSession _session;
    private readonly Func<Func<Task>, Task> _startExclusive;
    private CancellationTokenSource? _startCancellation;
    private readonly Action<Exception> _report;
    private readonly DispatcherTimer _poll;
    private TrackingClient? _client;
    private readonly List<TrackerIdentity> _identities = [];
    private TrackerIdentity? _identity;
    private ResearchProtocol _protocol = ResearchProtocol.GoogleFindHub;
    private string _serviceUrl = "", _name = "Windows PC", _status = "尚未啟動 / Not started", _locationStatus = "尚無定位回報 / No location reports";
    private bool _extended, _consent, _busy;
    public ObservableCollection<LocationReport> Reports { get; } = [];
    public ObservableCollection<TrackerIdentity> SavedIdentities { get; } = [];
    public TrackerIdentity? SelectedIdentity
    {
        get => _identity;
        set
        {
            if (value == _identity) return;
            if (RequestedActive) { _report(new InvalidOperationException("Stop before selecting another identity.")); return; }
            _identity = value; _session.ResetEvidence(); TrackerName = value?.Name ?? "Windows PC";
            Reports.Clear(); LocationStatus = "尚無定位回報 / No location reports"; Notify(); Notify(nameof(IdentityStatus));
        }
    }
    public IReadOnlyList<ResearchProtocol> Protocols { get; } = [ResearchProtocol.GoogleFindHub, ResearchProtocol.AppleFindMy];
    public ResearchProtocol Protocol { get => _protocol; set { if (_session.RequestedActive) { _report(new InvalidOperationException("Stop the current protocol before switching.")); return; } if (Set(ref _protocol, value)) SelectIdentity(); } }
    public string TrackerName { get => _name; set => Set(ref _name, value); }
    public string ServiceUrl { get => _serviceUrl; set => Set(ref _serviceUrl, value); }
    public bool Extended { get => _extended; set => Set(ref _extended, value); }
    public bool Consent { get => _consent; set => Set(ref _consent, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LocationStatus { get => _locationStatus; private set => Set(ref _locationStatus, value); }
    public string IdentityStatus => _identity == null ? "尚無身分；請建立或匯入您擁有的金鑰 / Create or import an owned identity" : $"{_identity.Id:D}\n{_identity.Protocol} · 配對時間 { _identity.PairDate:O}\n" + (_identity.Protocol == ResearchProtocol.AppleFindMy ? "所需靜態隨機位址 / Required address: " + AppleFindMyResearch.Preview(Hex.Parse(_identity.PublicKeyHex)).RequiredAddress : "EIK 已使用 DPAPI 加密；每 1024 秒更新 EID / EIK protected by DPAPI; EID rotates every 1024s");
    public string Payload => _session.CurrentProfile?.PayloadHex ?? "尚未提交 / No submission";
    public string Capabilities => $"API 接受 / API accepted: {_session.Capability.ApiAccepted}\n外部封包 / External packet: {_session.Capability.PacketVerified}\n可解密定位回報 / Decrypted report: {_session.Capability.LocationVerified}\n位址相容性 / Address compatibility: {_session.Capability.AddressCompatible}\n" + (_session.Capability.RealTrackingVerified ? "真實追蹤已驗證 / Real tracking verified" : "尚未驗證完整網路追蹤 / Full network tracking NOT verified") + "\n" + _session.Capability.Detail;
    public bool RequestedActive => _session.RequestedActive;
    public ICommand GenerateCommand { get; }
    public ICommand ImportKeyCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RegisterCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ImportEvidenceCommand { get; }
    public ICommand OpenMapCommand { get; }
    public TrackerViewModel(string directory, Func<bool> isMock, Func<Func<Task>, Task> startExclusive, Action<Exception> report)
    {
        _path = Path.Combine(directory, "tracker-identities.json"); _vault = new(Path.Combine(directory, "vault")); _credentials = new(Path.Combine(directory, "tracking-access.dpapi"));
        _startExclusive = startExclusive; _report = report;
        _session = new(p => isMock() ? new MockBluetoothBackend() : p == ResearchProtocol.GoogleFindHub ? new WindowsGattBackend() : new WindowsBluetoothBackend());
        _session.Changed += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => { Status = _session.Status; Notify(nameof(Payload)); Notify(nameof(Capabilities)); Notify(nameof(RequestedActive)); });
        UiCommand Cmd(Func<Task> action) => new(async () => { if (_busy) return; _busy = true; try { await action(); } finally { _busy = false; } }, report);
        GenerateCommand = Cmd(() => { CreateIdentity(null); return Task.CompletedTask; });
        ImportKeyCommand = Cmd(() => { var dialog = new OpenFileDialog { Filter = "Owned binary private key (*.bin)|*.bin", Title = "Import owned Apple P-224 private key (28 bytes) or Google EIK (32 bytes)" }; if (dialog.ShowDialog() == true) CreateIdentity(File.ReadAllBytes(dialog.FileName)); return Task.CompletedTask; });
        StartCommand = Cmd(StartAsync); StopCommand = new UiCommand(StopAsync, report);
        ConnectCommand = Cmd(() => { Connect(); return Task.CompletedTask; });
        DisconnectCommand = Cmd(() => { _client?.Dispose(); _client = null; _credentials.Delete(); Status = "桌面憑證已從此電腦移除；可在網頁撤銷 / Credential removed locally; revoke it on the server"; return Task.CompletedTask; });
        RegisterCommand = Cmd(RegisterAsync); RefreshCommand = Cmd(() => RefreshAsync(true));
        ImportEvidenceCommand = Cmd(() => { var dialog = new OpenFileDialog { Filter = "Packet evidence (*.json)|*.json" }; if (dialog.ShowDialog() == true) _session.ImportEvidence(JsonSerializer.Deserialize<PacketEvidence>(File.ReadAllText(dialog.FileName), Json) ?? throw new FormatException("Empty capture.")); return Task.CompletedTask; });
        OpenMapCommand = Cmd(() => { if (_client == null || _identity == null) throw new InvalidOperationException("Connect the HTTPS location service first."); Process.Start(new ProcessStartInfo(new Uri(_client.BaseUri, "#tracker=" + _identity.Id).AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; });
        _poll = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, async (_, _) => { if (_busy || _client == null || _identity == null) return; try { await RefreshAsync(true); } catch (Exception ex) { LocationStatus = "位置服務無法連線；保留最後回報 / Service unavailable; retained reports"; _report(ex); } }, Dispatcher.CurrentDispatcher);
    }
    public void Initialize()
    {
        try
        {
            if (File.Exists(_path)) _identities.AddRange(JsonSerializer.Deserialize<List<TrackerIdentity>>(File.ReadAllText(_path), Json) ?? []);
            SelectIdentity();
            if (_credentials.Load() is { } c) { ServiceUrl = c.Url; _client = new(c.Url, c.Token); }
        }
        catch (Exception ex) { _report(ex); }
    }
    private void SelectIdentity() { SavedIdentities.Clear(); foreach (var id in _identities.Where(x => x.Protocol == Protocol)) SavedIdentities.Add(id); SelectedIdentity = SavedIdentities.LastOrDefault(); TrackerName = _identity?.Name ?? "Windows PC"; Notify(nameof(IdentityStatus)); }
    private void Save() { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_identities, Json)); Notify(nameof(IdentityStatus)); }
    private void CreateIdentity(byte[]? imported)
    {
        if (_session.RequestedActive) throw new InvalidOperationException("Stop advertising before changing an identity.");
        byte[] secret, pub = [];
        if (Protocol == ResearchProtocol.AppleFindMy)
        {
            if (imported == null) (secret, pub) = TrackerCryptography.CreateAppleIdentity();
            else { secret = imported; pub = TrackerCryptography.ApplePublicKey(secret); }
        }
        else secret = imported ?? RandomNumberGenerator.GetBytes(32);
        try
        {
            var id = new TrackerIdentity { Name = TrackerName, Protocol = Protocol, PublicKeyHex = Hex.Format(pub) };
            id = id with { SecretReference = _vault.Store(id.Id, Protocol == ResearchProtocol.AppleFindMy ? "apple-private" : "google-eik", secret) };
            _identities.Add(id); SavedIdentities.Add(id); SelectedIdentity = id; Save(); Reports.Clear(); LocationStatus = "尚無定位回報 / No location reports";
            Status = "新身分已保護儲存；舊身分仍保留供歷史資料使用 / New protected identity created; previous identities retained";
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private async Task StartAsync()
    {
        if (!Consent) throw new InvalidOperationException("Confirm ownership and consent before broadcasting tracker identifiers.");
        if (_identity == null) throw new InvalidOperationException("Create or import an identity first.");
        _startCancellation?.Dispose(); _startCancellation = new(); var cancellation = _startCancellation.Token;
        await _startExclusive(async () => { cancellation.ThrowIfCancellationRequested(); var secret = _vault.Load(_identity.SecretReference); try { await _session.StartAsync(_identity, secret, Extended, cancellation); } finally { CryptographicOperations.ZeroMemory(secret); } });
    }
    public Task StopAsync() { _startCancellation?.Cancel(); return _session.StopAsync(); }
    public Task SuspendAsync() => _session.SuspendAsync();
    public Task ResumeAsync() => RequestedActive ? _startExclusive(() => _session.ResumeAsync()) : Task.CompletedTask;
    public void Connect()
    {
        var dialog = new OpenFileDialog { Filter = "Desktop credential (*.json)|*.json", Title = "Import a revocable desktop credential downloaded from your HTTPS service" };
        if (dialog.ShowDialog() != true) return;
        using var json = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
        var url = json.RootElement.GetProperty("url").GetString()!; var token = json.RootElement.GetProperty("token").GetString()!;
        var client = new TrackingClient(url, token); _credentials.Save(url, token); _client?.Dispose(); _client = client; ServiceUrl = url;
        Status = "HTTPS 憑證已以 DPAPI 儲存；請安全刪除下載的明文憑證 / HTTPS credential protected by DPAPI; delete the downloaded plaintext credential securely";
    }
    private async Task RegisterAsync()
    {
        if (_identity == null || _client == null) throw new InvalidOperationException("Create an identity and connect your HTTPS service first.");
        if (!Consent) throw new InvalidOperationException("Confirm ownership before uploading this identity to your service.");
        var secret = _vault.Load(_identity.SecretReference);
        try { var updated = await _client.RegisterAsync(_identity, secret); _identities.Remove(_identity); _identity = updated; _identities.Add(updated); Save(); Status = "服務註冊完成；仍須驗證封包與真實定位 / Registered; RF and location verification still required"; }
        finally { CryptographicOperations.ZeroMemory(secret); }
        await RefreshAsync(false);
    }
    private async Task RefreshAsync(bool query)
    {
        if (_client == null || _identity == null) throw new InvalidOperationException("Connect and register an identity first.");
        var identity = _identity;
        if (query) await _client.RefreshAsync(identity.Id);
        var result = await _client.GetLocationsAsync(identity.Id);
        if (_identity?.Id != identity.Id) return;
        Reports.Clear(); foreach (var report in result.Reports.OrderByDescending(x => x.ObservedAt)) { Reports.Add(report); _session.ApplyReport(report); }
        LocationStatus = Reports.Count == 0 ? "尚無定位回報 / No location reports · " + result.Status : $"{Reports.Count} 筆回報 / reports · {Reports[0].Source} · {Reports[0].ObservedAt:O}\n{result.Status}";
    }
    public async ValueTask DisposeAsync() { _poll.Stop(); _startCancellation?.Cancel(); await _session.DisposeAsync(); _startCancellation?.Dispose(); _client?.Dispose(); }
}
