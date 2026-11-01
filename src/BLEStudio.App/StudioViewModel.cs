using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BLEStudio.Bluetooth;
using BLEStudio.Core;
using BLEStudio.Infrastructure;
using BLEStudio.Protocols;
using Microsoft.Win32;

namespace BLEStudio.App;

public sealed class StudioViewModel : ObservableObject, IAsyncDisposable
{
    private IBluetoothBackend _backend;
    private bool _gatt;
    private readonly SemaphoreSlim _radioOwner = new(1, 1);
    public TrackerViewModel Tracker { get; }
    public ICommand SwitchNativeBackendCommand { get; }
    private readonly ProfileStore _profiles;
    private readonly KeyVault _vault;
    private readonly ScanRepository _scan = new();
    private readonly ConcurrentQueue<ScanPacket> _incoming = new();
    private int _queued, _dropped;
    private readonly DispatcherTimer _timer;
    private AdvertisementProfile _draft = new();
    private AdvertisementProfile? _selectedProfile;
    private ScanDevice? _selectedDevice;
    private AdStructure? _selectedStructure;
    private AdapterCapabilities _adapter = new();
    private string _notice = "Detecting Bluetooth adapter…", _advertising = "Stopped", _scanning = "Stopped";
    private string _payloadStatus = "", _dump = "", _validation = "", _submitted = "No submission yet.", _accepted = "No Started confirmation yet.", _observed = "No external capture imported.";
    private string _query = "", _rssi = "-127", _manufacturerFilter = "", _uuidFilter = "";
    private string _researchReport = "Analyze a payload or import authorized test data.", _researchInput = "", _keyStatus = "No key imported for this profile.";
    private string _companyId = "0xFFFF", _sectionData = "01 02 03 04", _serviceUuid = "FEAA", _customType = "0x30", _sectionKind = "Manufacturer";
    private string _duration = "0", _txPower = "", _profileJsonText = "";
    private bool _mock, _consent, _activeScan = true;
    private DateTimeOffset? _advertisingStarted;
    private AdvertisementProfile? _lastManualProfile, _resumeManualProfile;
    private DateTimeOffset? _manualDeadline;
    private IReadOnlyList<string> _profileRevisions = [];
    private string? _selectedRevision;
    private ExternalObservation? _externalObservation;
    public string DataDirectory { get; }
    public ObservableCollection<AdvertisementProfile> Profiles { get; } = [];
    public ObservableCollection<AdStructure> Structures { get; } = [];
    public ObservableCollection<ScanDevice> Devices { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ICollectionView DeviceView { get; }
    public IReadOnlyList<string> SectionKinds { get; } = ["Manufacturer", "Service data", "Service UUID", "Solicitation UUID", "Local name", "Flags", "Appearance", "Custom"];
    public IReadOnlyList<PhyPreference> Phys { get; } = Enum.GetValues<PhyPreference>();
    public IReadOnlyList<ResearchProtocol> Protocols { get; } = Enum.GetValues<ResearchProtocol>();
    public AdapterCapabilities Adapter { get => _adapter; private set { _adapter = value; Notify(); Notify(nameof(ExtendedSupported)); Notify(nameof(TxSupported)); Notify(nameof(PhySupported)); Notify(nameof(AnonymousSupported)); } }
    public bool ExtendedSupported => Adapter.ExtendedAdvertising;
    public bool TxSupported => Adapter.Support("Preferred TX power") is SupportStatus.Supported or SupportStatus.RequiresVerification;
    public bool PhySupported => Adapter.Support("PHY selection") is SupportStatus.Supported or SupportStatus.RequiresVerification;
    public bool AnonymousSupported => Adapter.Support("Anonymous") is SupportStatus.Supported or SupportStatus.RequiresVerification;
    public string BackendName => _backend.Name;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string AdvertisingState { get => _advertising; private set => Set(ref _advertising, value); }
    public string ScanningState { get => _scanning; private set => Set(ref _scanning, value); }
    public string Uptime => _advertisingStarted is { } started && AdvertisingState == "Started" ? (DateTimeOffset.UtcNow - started).ToString(@"hh\:mm\:ss") : "—";
    public string AdvertisingStartTime => _advertisingStarted?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";
    public int DeviceCount => _scan.Devices.Count;
    public string CaptureStatistics => $"{DeviceCount} devices · {_scan.Packets.Count} retained records · {_dropped} queue drops (bounded capture)";
    public string ProfileName { get => _draft.Name; set { _draft.Name = value; Notify(); } }
    public string Notes { get => _draft.Notes; set { _draft.Notes = value; Notify(); } }
    public string ProfileVersion => $"Schema {_draft.SchemaVersion} · Revision {_draft.Revision} · {_draft.Id:D}";
    public ResearchProtocol Protocol { get => _draft.Protocol; set { _draft.Protocol = value; Notify(); } }
    public bool Extended { get => _draft.UseExtendedAdvertising; set { _draft.UseExtendedAdvertising = value; Notify(); RefreshPayload(); } }
    public bool IncludeTxPower { get => _draft.IncludeTransmitPower; set { _draft.IncludeTransmitPower = value; Notify(); RefreshPayload(); } }
    public bool Anonymous { get => _draft.Anonymous; set { _draft.Anonymous = value; Notify(); RefreshPayload(); } }
    public PhyPreference PrimaryPhy { get => _draft.PrimaryPhy; set { _draft.PrimaryPhy = value; Notify(); RefreshPayload(); } }
    public PhyPreference SecondaryPhy { get => _draft.SecondaryPhy; set { _draft.SecondaryPhy = value; Notify(); RefreshPayload(); } }
    public string Duration { get => _duration; set { Set(ref _duration, value); RefreshPayload(); } }
    public string TxPower { get => _txPower; set { Set(ref _txPower, value); RefreshPayload(); } }
    public string InputHex { get => _draft.PayloadHex; set { _draft.PayloadHex = value; Notify(); RefreshPayload(); } }
    public string PayloadStatus { get => _payloadStatus; private set => Set(ref _payloadStatus, value); }
    public string HexDump { get => _dump; private set => Set(ref _dump, value); }
    public string Validation { get => _validation; private set => Set(ref _validation, value); }
    public string SubmittedHex { get => _submitted; private set => Set(ref _submitted, value); }
    public string AcceptedHex { get => _accepted; private set => Set(ref _accepted, value); }
    public string ObservedHex { get => _observed; private set => Set(ref _observed, value); }
    public string CompanyId { get => _companyId; set { Set(ref _companyId, value); Notify(nameof(CompanyDisplay)); } }
    public string CompanyDisplay { get { try { var id = ParseNumber(CompanyId); return $"0x{id:X4} / {id} · {CompanyNames.Lookup(id)}"; } catch (Exception) { return "Enter a company ID as 0xHEX or decimal (0–65535)."; } } }
    public string SectionData { get => _sectionData; set => Set(ref _sectionData, value); }
    public string ServiceUuid { get => _serviceUuid; set => Set(ref _serviceUuid, value); }
    public string CustomType { get => _customType; set => Set(ref _customType, value); }
    public string SectionKind { get => _sectionKind; set => Set(ref _sectionKind, value); }
    public AdStructure? SelectedStructure { get => _selectedStructure; set => Set(ref _selectedStructure, value); }
    public AdvertisementProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value) && value != null) LoadDraft(value); }
    }
    public string ProfileJsonText { get => _profileJsonText; set => Set(ref _profileJsonText, value); }
    public IReadOnlyList<string> ProfileRevisions { get => _profileRevisions; private set => Set(ref _profileRevisions, value); }
    public string? SelectedRevision { get => _selectedRevision; set => Set(ref _selectedRevision, value); }
    public string Search { get => _query; set { Set(ref _query, value); DeviceView.Refresh(); } }
    public string RssiThreshold { get => _rssi; set { Set(ref _rssi, value); DeviceView.Refresh(); } }
    public string ManufacturerFilter { get => _manufacturerFilter; set { Set(ref _manufacturerFilter, value); DeviceView.Refresh(); } }
    public string UuidFilter { get => _uuidFilter; set { Set(ref _uuidFilter, value); DeviceView.Refresh(); } }
    public bool KeepDuplicates { get => _scan.KeepDuplicates; set { _scan.KeepDuplicates = value; Notify(); } }
    public bool ActiveScan { get => _activeScan; set => Set(ref _activeScan, value); }
    public ScanDevice? SelectedDevice { get => _selectedDevice; set { Set(ref _selectedDevice, value); RefreshSelected(); } }
    public string DeviceDetails => SelectedDevice == null ? "Select a device to inspect received AD sections and RSSI history." :
        $"{SelectedDevice.Name}\n{SelectedDevice.Address} ({SelectedDevice.AddressType}) · {SelectedDevice.AdvertisementType}\nFirst: {SelectedDevice.FirstSeen:O}\nLast: {SelectedDevice.LastSeen:O}\nReceived: {SelectedDevice.Count} · RSSI: {SelectedDevice.Rssi} dBm\n{SelectedDevice.Manufacturers}\nServices: {SelectedDevice.Services}\n\n{string.Join("\n", AdCodec.Decode(Hex.Parse(SelectedDevice.PayloadHex)).Select(x => $"{x.Offset:X4} {x.TypeHex} {x.Description} | {x.HexData}"))}\n\n{Hex.Dump(Hex.Parse(SelectedDevice.PayloadHex))}";
    public PointCollection RssiPoints => SelectedDevice == null ? [] : new PointCollection(SelectedDevice.RssiHistory.Select((x, i) => new Point(i * 580.0 / Math.Max(1, SelectedDevice.RssiHistory.Count - 1), Math.Clamp(-x, 0, 127) * 140.0 / 127)));
    public string ResearchInput { get => _researchInput; set => Set(ref _researchInput, value); }
    public string ResearchReport { get => _researchReport; private set => Set(ref _researchReport, value); }
    public string KeyStatus { get => _keyStatus; private set => Set(ref _keyStatus, value); }
    public bool ResearchConsent { get => _consent; set => Set(ref _consent, value); }
    public bool MockMode { get => _mock; private set { Set(ref _mock, value); Notify(nameof(ModeLabel)); } }
    public string ModeLabel => MockMode ? "SIMULATION · no radio transmission" : "WINDOWS BLE · real adapter";

    public ICommand RefreshAdapterCommand { get; }
    public ICommand StartAdvertisingCommand { get; }
    public ICommand StopAdvertisingCommand { get; }
    public ICommand StartScanningCommand { get; }
    public ICommand StopScanningCommand { get; }
    public ICommand SwitchBackendCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand AddSectionCommand { get; }
    public ICommand ReplaceSectionCommand { get; }
    public ICommand RemoveSectionCommand { get; }
    public ICommand CopyHexCommand { get; }
    public ICommand PasteHexCommand { get; }
    public ICommand ImportPayloadCommand { get; }
    public ICommand ExportPayloadCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ImportProfileCommand { get; }
    public ICommand ExportProfileCommand { get; }
    public ICommand ApplyProfileJsonCommand { get; }
    public ICommand RefreshProfileJsonCommand { get; }
    public ICommand RestoreRevisionCommand { get; }
    public ICommand ExportScanCommand { get; }
    public ICommand ImportScanCommand { get; }
    public ICommand CopyDeviceCommand { get; }
    public ICommand ClearScanCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand ApplePreviewCommand { get; }
    public ICommand GooglePreviewCommand { get; }
    public ICommand ImportKeyCommand { get; }
    public ICommand DeleteKeyCommand { get; }
    public ICommand ImportObservationCommand { get; }
    public ICommand ExportResearchCommand { get; }
    public ICommand CompareProfilesCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand ClearLogsCommand { get; }

    public StudioViewModel(bool mock = false, string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BLEStudio");
        _profiles = new(Path.Combine(DataDirectory, "profiles")); _vault = new(Path.Combine(DataDirectory, "vault"));
        MockMode = mock; _backend = mock ? new MockBluetoothBackend() : new WindowsBluetoothBackend(); AttachBackend();
        Tracker = new(DataDirectory, () => MockMode, start => RunRadioOperationAsync(async () => { await _backend.StopAdvertisingAsync(); await start(); }), ReportError);
        SwitchNativeBackendCommand = Cmd(() => RunRadioOperationAsync(async () => { await Tracker.StopAsync(); await _backend.StopAdvertisingAsync(); await _backend.StopScanningAsync(); DetachBackend(); await _backend.DisposeAsync(); _gatt = !_gatt; MockMode = false; _backend = _gatt ? new WindowsGattBackend() : new WindowsBluetoothBackend(); AttachBackend(); Notify(nameof(BackendName)); await RefreshAdapterAsync(); }));
        DeviceView = CollectionViewSource.GetDefaultView(Devices); DeviceView.Filter = MatchesFilter;
        RefreshAdapterCommand = Cmd(RefreshAdapterAsync);
        StartAdvertisingCommand = Cmd(() => RunRadioOperationAsync(StartAdvertisingAsync)); StopAdvertisingCommand = Cmd(() => _backend.StopAdvertisingAsync());
        StartScanningCommand = Cmd(() => _backend.StartScanningAsync(ActiveScan)); StopScanningCommand = Cmd(() => _backend.StopScanningAsync());
        SwitchBackendCommand = Cmd(() => RunRadioOperationAsync(SwitchBackendAsync)); ToggleThemeCommand = Action(ToggleTheme);
        AddSectionCommand = Action(() => EditSection(false)); ReplaceSectionCommand = Action(() => EditSection(true));
        RemoveSectionCommand = Action(() => { if (SelectedStructure == null) throw new FormatException("Select a structure first."); InputHex = Hex.Format(AdCodec.Encode(Structures.Where(x => !ReferenceEquals(x, SelectedStructure)).ToArray())); });
        CopyHexCommand = Action(() => Clipboard.SetText(InputHex)); PasteHexCommand = Action(() => InputHex = Clipboard.GetText());
        ImportPayloadCommand = Action(ImportPayload); ExportPayloadCommand = Action(ExportPayload);
        NewProfileCommand = Action(() => { _selectedProfile = null; Notify(nameof(SelectedProfile)); LoadDraft(new()); });
        SaveProfileCommand = Action(SaveProfile); DuplicateProfileCommand = Action(DuplicateProfile); DeleteProfileCommand = Action(DeleteProfile);
        ImportProfileCommand = Action(ImportProfile); ExportProfileCommand = Action(ExportProfile);
        ApplyProfileJsonCommand = Action(() => LoadDraft(ProfileJson.Deserialize(ProfileJsonText)));
        RefreshProfileJsonCommand = Action(() => ProfileJsonText = ProfileJson.Serialize(BuildProfile()));
        RestoreRevisionCommand = Action(() => { if (SelectedRevision == null) throw new FormatException("Select a stored revision."); LoadDraft(ProfileJson.Deserialize(File.ReadAllText(SelectedRevision))); Notice = "Revision loaded into editor. Save to create a new revision."; });
        ExportScanCommand = Action(ExportScan); ImportScanCommand = Action(ImportScan);
        CopyDeviceCommand = Action(() => { if (SelectedDevice != null) Clipboard.SetText(SelectedDevice.PayloadHex); });
        ClearScanCommand = Action(() => { _scan.Clear(); Devices.Clear(); SelectedDevice = null; Notify(nameof(DeviceCount)); Notify(nameof(CaptureStatistics)); });
        AnalyzeCommand = Action(Analyze); ApplePreviewCommand = Action(ApplePreview); GooglePreviewCommand = Action(GooglePreview);
        ImportKeyCommand = Action(ImportKey); DeleteKeyCommand = Action(DeleteKey); ImportObservationCommand = Action(ImportObservation);
        ExportResearchCommand = Action(ExportResearch); CompareProfilesCommand = Action(CompareProfiles);
        ExportDiagnosticsCommand = Action(ExportDiagnostics); ClearLogsCommand = Action(Logs.Clear);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => DrainPackets(), Dispatcher.CurrentDispatcher);
    }
    private UiCommand Cmd(Func<Task> execute) => new(execute, ReportError);
    private async Task RunRadioOperationAsync(Func<Task> action)
    {
        await _radioOwner.WaitAsync();
        try { await action(); } finally { _radioOwner.Release(); }
    }
    private UiCommand Action(Action action) => Cmd(() => { action(); return Task.CompletedTask; });
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        Tracker.Initialize();
        // Seed only once, so a deliberately deleted template stays deleted.
        var marker = Path.Combine(DataDirectory, "profiles-initialized-v1");
        if (!File.Exists(marker))
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!; using var reader = new StreamReader(stream);
                var profile = ProfileJson.Deserialize(reader.ReadToEnd());
                if (!File.Exists(Path.Combine(_profiles.DirectoryPath, $"{profile.Id:D}.json"))) _profiles.Save(profile);
            }
            File.WriteAllText(marker, "1");
        }
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Protocol == ResearchProtocol.Generic);
        if (SelectedProfile == null) LoadDraft(new());
        await RefreshAdapterAsync();
        Log("Application", "Startup", $"BLE Studio {Assembly.GetExecutingAssembly().GetName().Version}; {_backend.Name}.");
    }
    private void AttachBackend()
    {
        _backend.AdvertisingStatusChanged += AdvertisingChanged; _backend.ScanningStatusChanged += ScanningChanged; _backend.PacketReceived += PacketReceived;
    }
    private void DetachBackend()
    {
        _backend.AdvertisingStatusChanged -= AdvertisingChanged; _backend.ScanningStatusChanged -= ScanningChanged; _backend.PacketReceived -= PacketReceived;
    }
    private void AdvertisingChanged(object? sender, BackendStatus e) => Dispatch(() =>
    {
        AdvertisingState = e.State.ToString(); if (e.StartedAt != null) _advertisingStarted = e.StartedAt;
        if (e.AcceptedPayloadHex != null) AcceptedHex = $"{DateTimeOffset.Now:O} · {BackendName}\n{e.AcceptedPayloadHex}\nNative backend reported complete Started for this request preview. Windows exposes no RF payload readback; GATT flags and UUID encoding are system generated.";
        Notice = e.Message; Notify(nameof(Uptime)); Notify(nameof(AdvertisingStartTime)); Log("Advertising", e.ErrorCode ?? e.State.ToString(), e.Message);
    });
    private void ScanningChanged(object? sender, BackendStatus e) => Dispatch(() => { ScanningState = e.State.ToString(); Notice = e.Message; Log("Scanner", e.ErrorCode ?? e.State.ToString(), e.Message); });
    private void PacketReceived(object? sender, ScanPacket e)
    {
        if (Interlocked.Increment(ref _queued) > 5000) { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _dropped); return; }
        _incoming.Enqueue(e);
    }
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action(); else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
    public void Log(string category, string code, string message)
    {
        Logs.Insert(0, new(DateTimeOffset.Now, category, code, message));
        while (Logs.Count > 2000) Logs.RemoveAt(Logs.Count - 1);
    }
    public void ReportError(Exception ex)
    {
        Notice = ex.Message;
        Log("Error", ex is BluetoothOperationException b ? b.Code : $"0x{ex.HResult:X8}", ex.Message);
    }
    public async Task RefreshAdapterAsync()
    {
        Adapter = await _backend.DetectAsync(); RefreshPayload();
        Notice = Adapter.Available ? $"{Adapter.Name} · Radio: {Adapter.RadioState}. Capabilities are shown below." : "Bluetooth adapter unavailable. Editing, profiles and research remain available; simulation is available in the toolbar.";
        Log("Adapter", Adapter.Available ? "Detected" : "Unavailable", Notice);
    }
    private async Task SwitchBackendAsync()
    {
        await Tracker.StopAsync();
        await _backend.StopAdvertisingAsync(); await _backend.StopScanningAsync(); DetachBackend(); await _backend.DisposeAsync();
        MockMode = !MockMode; _backend = MockMode ? new MockBluetoothBackend() : _gatt ? new WindowsGattBackend() : new WindowsBluetoothBackend(); AttachBackend();
        _scan.Clear(); Devices.Clear(); SelectedDevice = null;
        while (_incoming.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
        AdvertisingState = ScanningState = "Stopped"; SubmittedHex = "No submission on this backend."; AcceptedHex = "No Started confirmation on this backend.";
        Notify(nameof(BackendName)); Notify(nameof(DeviceCount)); await RefreshAdapterAsync();
    }
    private AdvertisementProfile BuildProfile()
    {
        if (!int.TryParse(Duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration)) throw new FormatException("Duration must be an integer number of seconds.");
        short? power = null;
        if (!string.IsNullOrWhiteSpace(TxPower)) power = short.TryParse(TxPower, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new FormatException("TX power must be an integer dBm value or empty for system default.");
        _draft.DurationSeconds = duration; _draft.PreferredTransmitPowerDbm = power;
        return ProfileJson.Clone(_draft);
    }
    private void LoadDraft(AdvertisementProfile profile)
    {
        _draft = ProfileJson.Clone(profile); _duration = profile.DurationSeconds.ToString(CultureInfo.InvariantCulture); _txPower = profile.PreferredTransmitPowerDbm?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (var name in new[] { nameof(ProfileName), nameof(Notes), nameof(Protocol), nameof(Extended), nameof(IncludeTxPower), nameof(Anonymous), nameof(PrimaryPhy), nameof(SecondaryPhy), nameof(Duration), nameof(TxPower), nameof(InputHex), nameof(ProfileVersion) }) Notify(name);
        ProfileJsonText = ProfileJson.Serialize(_draft); ProfileRevisions = _profiles.Revisions(_draft.Id);
        KeyStatus = _draft.KeyReference == null ? "No key imported for this profile." : "Per-user encrypted key reference exists. No secret is exported with profiles.";
        RefreshPayload();
    }
    private void RefreshPayload()
    {
        try
        {
            var bytes = Hex.Parse(InputHex); var ads = AdCodec.Decode(bytes); Structures.Clear(); foreach (var ad in ads) Structures.Add(ad);
            HexDump = Hex.Dump(bytes);
            PayloadStatus = $"{bytes.Length} bytes · {ads.Count} AD structures · {(_draft.UseExtendedAdvertising ? "extended" : "legacy")} budget {WindowsPayloadPolicy.Capacity(_draft, Adapter)} bytes";
            var issues = _backend.ValidateProfile(BuildProfile(), Adapter);
            Validation = issues.Count == 0 ? "Ready to submit. Windows acceptance and RF reception are separate checks." : string.Join("\n", issues.Select(x => $"• {x.Field}: {x.Message}"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException)
        {
            Structures.Clear(); HexDump = ""; PayloadStatus = "Invalid payload or parameter"; Validation = ex.Message;
        }
        if (_externalObservation != null)
        {
            try { ObservedHex = ProtocolResearch.Compare(InputHex, _externalObservation).Description + "\n" + _externalObservation.PayloadHex; }
            catch (FormatException) { ObservedHex = "Imported capture retained; fix current payload to compare bytes."; }
        }
    }
    private async Task StartAdvertisingAsync()
    {
        await Tracker.StopAsync();
        var profile = BuildProfile();
        var issues = _backend.ValidateProfile(profile, Adapter).Where(x => x.Severity == ValidationSeverity.Error).ToArray();
        if (issues.Length > 0) throw new BluetoothOperationException("ValidationFailed", string.Join("\n", issues.Select(x => x.Message)));
        if (ProtocolResearch.IsResearchPayload(profile))
        {
            if (!ResearchConsent) throw new BluetoothOperationException("ResearchConsentRequired", "Confirm ownership and informed tester consent in Protocol Research before transmitting research frames.");
            if (profile.DurationSeconds is <= 0 or > 300) throw new BluetoothOperationException("ResearchDuration", "Research transmissions require a duration from 1 to 300 seconds. Reassess before each session.");
        }
        var plan = _gatt && !MockMode ? new[] { $"GattServiceProvider UUID: {GattPayloadPolicy.Build(profile).ServiceUuid}; ServiceData: {Hex.Format(GattPayloadPolicy.Build(profile).ServiceData)}; IsConnectable: {profile.Connectable ?? false}; IsDiscoverable: {profile.Scannable ?? true}; flags are generated by Windows." } : AdCodec.Decode(Hex.Parse(profile.PayloadHex)).Select(ad => ad.Type == 0xFF
            ? $"ManufacturerData: company=0x{ad.Data[0] | ad.Data[1] << 8:X4}, data=[{Hex.Format(ad.Data.AsSpan(2))}]"
            : $"DataSections: type=0x{ad.Type:X2}, data=[{ad.HexData}]");
        SubmittedHex = $"{DateTimeOffset.Now:O} · {BackendName} · {profile.Name}\n{Hex.Format(Hex.Parse(profile.PayloadHex))}\nAPI request fields:\n{string.Join("\n", plan)}\nWindows controls on-air section ordering and additional fields.";
        AcceptedHex = "Awaiting complete native backend Started confirmation for this submission.";
        await _backend.StartAdvertisingAsync(profile);
        _lastManualProfile = ProfileJson.Clone(profile); _manualDeadline = profile.DurationSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(profile.DurationSeconds) : null;
    }
    public async Task SuspendAsync()
    {
        _resumeManualProfile = AdvertisingState == "Started" && _lastManualProfile != null ? ProfileJson.Clone(_lastManualProfile) : null;
        await Tracker.SuspendAsync(); await _backend.StopAdvertisingAsync();
        Notice = "Suspended: no Bluetooth advertising during sleep/hibernation. Previous active mode will be checked on resume.";
    }
    public async Task ResumeAsync()
    {
        if (Tracker.RequestedActive) { await Tracker.ResumeAsync(); return; }
        var resume = _resumeManualProfile; _resumeManualProfile = null;
        if (resume == null) return;
        await RunRadioOperationAsync(async () =>
        {
            if (_manualDeadline is { } deadline)
            {
                var remaining = (int)(deadline - DateTimeOffset.UtcNow).TotalSeconds;
                if (remaining <= 0) { Notice = "Previous timed advertising session expired during sleep."; return; }
                resume.DurationSeconds = remaining;
            }
            await RefreshAdapterAsync(); await _backend.StartAdvertisingAsync(resume);
            Notice = "Previous active advertising mode was rechecked and resumed.";
        });
    }
    private static ushort ParseNumber(string value) => value.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt16(value.Trim()[2..], 16) : ushort.Parse(value, CultureInfo.InvariantCulture);
    private AdStructure MakeSection()
    {
        var bytes = SectionKind == "Local name" ? System.Text.Encoding.UTF8.GetBytes(SectionData) : Hex.Parse(SectionData);
        return SectionKind switch
        {
            "Manufacturer" => AdCodec.Manufacturer(ParseNumber(CompanyId), bytes),
            "Service data" => AdCodec.ServiceData(ServiceUuid, bytes),
            "Service UUID" => UuidSection(false), "Solicitation UUID" => UuidSection(true),
            "Local name" => new(0x09, bytes), "Flags" => bytes.Length == 1 ? new(0x01, bytes) : throw new FormatException("Flags require one data byte."),
            "Appearance" => bytes.Length == 2 ? new(0x19, bytes) : throw new FormatException("Appearance requires two little-endian data bytes."),
            _ => new(checked((byte)ParseNumber(CustomType)), bytes)
        };
        AdStructure UuidSection(bool solicitation)
        {
            var id = AdCodec.UuidBytes(ServiceUuid);
            return new((solicitation, id.Length) switch { (false, 2) => 0x03, (false, 4) => 0x05, (false, _) => 0x07, (true, 2) => 0x14, (true, 4) => 0x1F, _ => 0x15 }, id);
        }
    }
    private void EditSection(bool replace)
    {
        var ads = AdCodec.Decode(Hex.Parse(InputHex)).ToList(); var section = MakeSection();
        if (replace)
        {
            if (SelectedStructure == null) throw new FormatException("Select a structure to replace.");
            var index = Structures.IndexOf(SelectedStructure); if (index < 0) throw new FormatException("Selection is stale."); ads[index] = section;
        }
        else ads.Add(section);
        InputHex = Hex.Format(AdCodec.Encode(ads));
    }
    private static string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return null;
        if (new FileInfo(dialog.FileName).Length > 32 * 1024 * 1024) throw new FormatException("File exceeds the 32 MiB import limit.");
        return dialog.FileName;
    }
    private static string? SaveFile(string filter, string name)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private void ImportPayload()
    {
        var path = OpenFile("Payload (*.bin;*.hex;*.txt)|*.bin;*.hex;*.txt"); if (path == null) return;
        InputHex = Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? Hex.Format(File.ReadAllBytes(path)) : File.ReadAllText(path);
    }
    private void ExportPayload()
    {
        var bytes = Hex.Parse(InputHex); var path = SaveFile("Hex (*.hex)|*.hex|Binary (*.bin)|*.bin", "payload.hex"); if (path == null) return;
        if (Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(path, bytes); else AtomicFile.WriteText(path, Hex.Format(bytes));
        Notice = "Payload exported.";
    }
    private void ReloadProfiles()
    {
        Profiles.Clear(); foreach (var p in _profiles.LoadAll(msg => Log("Profiles", "ImportError", msg))) Profiles.Add(p);
    }
    private void SaveProfile()
    {
        var p = BuildProfile(); _profiles.Save(p); ReloadProfiles(); SelectedProfile = Profiles.First(x => x.Id == p.Id); Notice = "Profile saved; previous revision retained.";
    }
    private void DuplicateProfile()
    {
        var p = BuildProfile(); p.Id = Guid.NewGuid(); p.Revision = 1; p.Name += " (copy)"; p.KeyReference = null;
        _profiles.Save(p); ReloadProfiles(); SelectedProfile = Profiles.First(x => x.Id == p.Id); Notice = "New independent profile saved. Encrypted keys are not duplicated.";
    }
    private void DeleteProfile()
    {
        _profiles.Delete(_draft.Id); ReloadProfiles(); SelectedProfile = Profiles.FirstOrDefault();
        if (SelectedProfile == null) LoadDraft(new()); Notice = "Profile removed. Stored revisions and separately managed keys are retained.";
    }
    private void ImportProfile()
    {
        var path = OpenFile("JSON profile (*.json)|*.json"); if (path == null) return;
        var p = ProfileJson.Deserialize(File.ReadAllText(path));
        p.Id = Guid.NewGuid(); p.Revision = 1; p.KeyReference = null; // Untrusted imports cannot access another local profile's vault.
        _profiles.Save(p); ReloadProfiles(); SelectedProfile = Profiles.First(x => x.Id == p.Id); Notice = "Profile imported under a new ID; local key access was not imported.";
    }
    private void ExportProfile()
    {
        var p = BuildProfile(); p.KeyReference = null; var path = SaveFile("JSON profile (*.json)|*.json", "profile.json"); if (path != null) AtomicFile.WriteText(path, ProfileJson.Serialize(p));
    }
    private bool MatchesFilter(object item)
    {
        if (item is not ScanDevice d) return false;
        if (short.TryParse(RssiThreshold, out var rssi) && d.Rssi < rssi) return false;
        return $"{d.Name} {d.Address} {d.Manufacturers} {d.Services} {d.PayloadHex}".Contains(Search, StringComparison.OrdinalIgnoreCase)
            && d.Manufacturers.Contains(ManufacturerFilter, StringComparison.OrdinalIgnoreCase) && d.Services.Contains(UuidFilter, StringComparison.OrdinalIgnoreCase);
    }
    private void DrainPackets()
    {
        var changed = false;
        for (var i = 0; i < 1500 && _incoming.TryDequeue(out var packet); i++)
        {
            Interlocked.Decrement(ref _queued);
            try { _scan.Add(packet); changed = true; } catch (FormatException ex) { Log("Scanner", "MalformedPacket", ex.Message); }
        }
        if (changed)
        {
            var current = _scan.Devices.ToHashSet();
            for (var index = Devices.Count - 1; index >= 0; index--) if (!current.Contains(Devices[index])) Devices.RemoveAt(index);
            var known = Devices.ToHashSet();
            foreach (var device in current) if (known.Add(device)) Devices.Insert(0, device);
            DeviceView.Refresh(); RefreshSelected(); Notify(nameof(DeviceCount)); Notify(nameof(CaptureStatistics));
        }
        Notify(nameof(Uptime));
    }
    private void RefreshSelected() { Notify(nameof(DeviceDetails)); Notify(nameof(RssiPoints)); }
    private void ExportScan()
    {
        DrainPackets(); var path = SaveFile("JSON capture (*.json)|*.json|CSV device summary (*.csv)|*.csv", "ble-capture.json"); if (path == null) return;
        AtomicFile.WriteText(path, Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ScanFiles.ToCsv(_scan.Devices) : ScanFiles.ToJson(_scan.Packets, BackendName));
        Notice = "Capture exported. JSON stores retained records; CSV stores current device summaries.";
    }
    private void ImportScan()
    {
        var path = OpenFile("JSON capture (*.json)|*.json"); if (path == null) return;
        var packets = ScanFiles.FromJson(File.ReadAllText(path)); foreach (var packet in packets) _scan.Add(packet);
        Devices.Clear(); foreach (var d in _scan.Devices) Devices.Add(d); DeviceView.Refresh(); Notify(nameof(DeviceCount)); Notify(nameof(CaptureStatistics));
        Notice = "Stored records loaded. Counters reflect loaded retained records, not packets discarded before export.";
    }
    private void Analyze()
    {
        var reports = ProtocolResearch.Analyze(InputHex);
        ResearchReport = string.Join("\n\n", reports.Select(x => $"{x.Protocol}\nRecognized: {x.Recognized} · Supported format valid: {x.FormatValid}\n" + string.Join("\n", x.Fields.Select(f => $"{f.Key}: {f.Value}")) + "\n" + string.Join("\n", x.Findings))) + "\n\nWindows validation:\n" + Validation;
    }
    private void RequireConsent() { if (!ResearchConsent) throw new FormatException("Confirm ownership of test data and informed tester consent first."); }
    private void ApplePreview()
    {
        RequireConsent(); byte[] key;
        if (!string.IsNullOrWhiteSpace(ResearchInput)) key = Hex.Parse(ResearchInput);
        else if (_draft.KeyReference?.EndsWith("-apple-public", StringComparison.Ordinal) == true) key = _vault.Load(_draft.KeyReference);
        else throw new FormatException("Enter a 28-byte authorized public key or import it to this profile's encrypted vault.");
        try
        {
            var preview = AppleFindMyResearch.Preview(key); Protocol = ResearchProtocol.AppleFindMy; Duration = "120"; InputHex = preview.PayloadHex;
            ResearchReport = $"APPLE RESEARCH PREVIEW\nRequired static-random address: {preview.RequiredAddress}\n\n{string.Join("\n", preview.Findings)}\n\n{Validation}";
        }
        finally { CryptographicOperations.ZeroMemory(key); ResearchInput = ""; }
    }
    private void GooglePreview()
    {
        RequireConsent(); var preview = GoogleFindHubResearch.Preview(Hex.Parse(ResearchInput)); Protocol = ResearchProtocol.GoogleFindHub; Duration = "120"; InputHex = preview.PayloadHex;
        ResearchReport = "GOOGLE RESEARCH PREVIEW\n" + string.Join("\n", preview.Findings) + "\n\n" + Validation; ResearchInput = "";
    }
    private void ImportKey()
    {
        RequireConsent();
        if (Protocol == ResearchProtocol.Generic) throw new FormatException("Select AppleFindMy or GoogleFindHub for this profile first.");
        var path = OpenFile("Key bytes (*.bin;*.hex;*.txt)|*.bin;*.hex;*.txt"); if (path == null) return;
        var bytes = Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(path) : Hex.Parse(File.ReadAllText(path));
        try
        {
            _draft.KeyReference = _vault.Store(_draft.Id, Protocol == ResearchProtocol.AppleFindMy ? "apple-public" : "google-eik", bytes);
            _profiles.Save(BuildProfile()); KeyStatus = Protocol == ResearchProtocol.AppleFindMy ? "28-byte test public key encrypted with Windows DPAPI for this user." : "32-byte secret EIK encrypted with Windows DPAPI. EID derivation is outside this analyzer; import an authorized precomputed EID for preview.";
            ProfileJsonText = ProfileJson.Serialize(_draft); Log("Research", "KeyImported", "Authorized test material stored in the per-user encrypted vault. No key bytes logged.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void DeleteKey()
    {
        if (_draft.KeyReference != null) _vault.Delete(_draft.KeyReference);
        _draft.KeyReference = null; _profiles.Save(BuildProfile()); KeyStatus = "Key removed from local vault."; ProfileJsonText = ProfileJson.Serialize(_draft);
    }
    private void ImportObservation()
    {
        var path = OpenFile("External observation JSON (*.json)|*.json"); if (path == null) return;
        var observation = JsonSerializer.Deserialize<ExternalObservation>(File.ReadAllText(path), ProfileJson.Options) ?? throw new FormatException("Observation is empty.");
        var result = ProtocolResearch.Compare(InputHex, observation); _externalObservation = observation;
        ObservedHex = result.Description + "\n" + result.ObservedHex; ResearchReport = result.Description;
    }
    private void CompareProfiles()
    {
        var apple = Profiles.FirstOrDefault(x => x.Protocol == ResearchProtocol.AppleFindMy); var google = Profiles.FirstOrDefault(x => x.Protocol == ResearchProtocol.GoogleFindHub);
        ResearchReport = "DUAL PROTOCOL COMPARISON\nOne publisher is used. Stop, select the other profile and Start to switch. Concurrent dual advertising is not claimed.\n\n" +
            string.Join("\n\n", new[] { apple, google }.Where(x => x != null).Select(x => $"{x!.Name} · {Hex.Parse(x.PayloadHex).Length} bytes\n{x.PayloadHex}\n{string.Join("\n", WindowsPayloadPolicy.Validate(x, Adapter).Select(i => i.Message))}")) +
            "\n\nApple requires key-derived address control. Google requires reserved service data and synchronized private addresses. Neither pipeline implements network registration or report retrieval. Keys remain separate per profile.";
    }
    private void ExportResearch()
    {
        var path = SaveFile("Research report (*.json)|*.json", "research-report.json"); if (path == null) return;
        AtomicFile.WriteText(path, JsonSerializer.Serialize(new { schemaVersion = 1, generatedAt = DateTimeOffset.Now, backend = BackendName, inputHex = InputHex,
            submitted = SubmittedHex, windowsStarted = AcceptedHex, observation = _externalObservation, analysis = ProtocolResearch.Analyze(InputHex), report = ResearchReport,
            limitations = Validation, sources = new[] { AppleFindMyResearch.Source, GoogleFindHubResearch.Source } }, ProfileJson.Options)); Notice = "Research report exported. It contains advertised identifiers; no secret vault data is included.";
    }
    private void ExportDiagnostics()
    {
        var path = SaveFile("Debug report (*.json)|*.json", "blestudio-diagnostics.json"); if (path == null) return;
        AtomicFile.WriteText(path, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.Now, version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(), adapter = Adapter,
            backend = BackendName, advertising = AdvertisingState, scanning = ScanningState, capture = CaptureStatistics, logs = Logs.ToArray() }, ProfileJson.Options)); Notice = "Diagnostics exported without profile payloads, captures or vault key material.";
    }
    private bool _light;
    private void ToggleTheme()
    {
        _light = !_light; Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{(_light ? "Light" : "Dark")}.xaml", UriKind.Relative) };
    }
    public async ValueTask DisposeAsync() { _timer.Stop(); await Tracker.DisposeAsync(); await _backend.DisposeAsync(); DetachBackend(); }
}
