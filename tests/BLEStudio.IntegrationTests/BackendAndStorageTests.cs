using BLEStudio.Bluetooth;
using BLEStudio.Core;
using BLEStudio.Infrastructure;

namespace BLEStudio.IntegrationTests;

public sealed class BackendAndStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BLEStudio-test-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task MockPublisherHasObservableStartAndStopStates()
    {
        await using var backend = new MockBluetoothBackend(); var states = new List<OperationState>(); backend.AdvertisingStatusChanged += (_, e) => states.Add(e.State);
        await backend.StartAdvertisingAsync(new()); await backend.StopAdvertisingAsync(); await backend.StopAdvertisingAsync();
        Assert.Equal(new[] { OperationState.Starting, OperationState.Started, OperationState.Stopping, OperationState.Stopped }, states);
    }
    [Fact]
    public async Task MissingAdapterAndUnsupportedParametersDoNotClaimSuccess()
    {
        await using var backend = new MockBluetoothBackend { AdapterAvailable = false };
        await Assert.ThrowsAsync<BluetoothOperationException>(() => backend.StartAdvertisingAsync(new()));
        await Assert.ThrowsAsync<BluetoothOperationException>(() => backend.StartScanningAsync());
        backend.AdapterAvailable = true;
        await Assert.ThrowsAsync<BluetoothOperationException>(() => backend.StartAdvertisingAsync(new() { AdvertisingIntervalMs = 50 }));
        Assert.Equal(OperationState.Stopped, backend.AdvertisingState);
    }
    [Fact]
    public async Task SimulatedStartFailureCanBeRecovered()
    {
        await using var backend = new MockBluetoothBackend { FailStart = true };
        await Assert.ThrowsAsync<BluetoothOperationException>(() => backend.StartAdvertisingAsync(new())); Assert.Equal(OperationState.Aborted, backend.AdvertisingState);
        backend.FailStart = false; await backend.StartAdvertisingAsync(new()); Assert.Equal(OperationState.Started, backend.AdvertisingState);
    }
    [Fact]
    public async Task CancellationDoesNotStartPublisher()
    {
        await using var backend = new MockBluetoothBackend();
        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.StartAdvertisingAsync(new(), new CancellationToken(true))); Assert.Equal(OperationState.Stopped, backend.AdvertisingState);
    }
    [Fact]
    public async Task MockDurationRequestsAutomaticStop()
    {
        await using var backend = new MockBluetoothBackend(); var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.AdvertisingStatusChanged += (_, e) => { if (e.State == OperationState.Stopped) stopped.TrySetResult(); };
        await backend.StartAdvertisingAsync(new() { DurationSeconds = 1 }); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(OperationState.Stopped, backend.AdvertisingState);
    }
    [Fact]
    public async Task MockWatcherEmitsMarkedPacketsAndStopsCleanly()
    {
        await using var backend = new MockBluetoothBackend(); var packet = new TaskCompletionSource<ScanPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.PacketReceived += (_, p) => packet.TrySetResult(p); await backend.StartScanningAsync();
        var received = await packet.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(received.IsMock); Assert.Single(received.Structures);
        await backend.StopScanningAsync(); await backend.StopScanningAsync();
    }
    [Fact]
    public void ProfileStoreWritesRevisionAndLoadsIndependently()
    {
        var store = new ProfileStore(_directory); var p = new AdvertisementProfile(); store.Save(p); p.Name = "Renamed"; store.Save(p);
        Assert.Equal(2, Assert.Single(store.LoadAll()).Revision); Assert.Single(store.Revisions(p.Id));
        Assert.Equal("Generic BLE Beacon", ProfileJson.Deserialize(File.ReadAllText(Assert.Single(store.Revisions(p.Id)))).Name);
        store.Delete(p.Id); Assert.Empty(store.LoadAll());
    }
    [Fact]
    public void CorruptProfileIsReportedAndDoesNotHideOtherProfiles()
    {
        var store = new ProfileStore(_directory); store.Save(new()); File.WriteAllText(Path.Combine(_directory, "bad.json"), "not-json");
        var errors = new List<string>(); Assert.Single(store.LoadAll(errors.Add)); Assert.Single(errors);
    }
    [Fact]
    public void VaultProtectsKeysAndRejectsPathTraversal()
    {
        var vault = new KeyVault(_directory); var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        var reference = vault.Store(Guid.NewGuid(), "google-eik", key); Assert.Equal(key, vault.Load(reference));
        var file = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory))); Assert.False(file.SequenceEqual(key));
        Assert.Throws<FormatException>(() => vault.Load("../escape")); vault.Delete(reference); Assert.Empty(Directory.GetFiles(_directory));
    }
    [Fact] public void VaultRejectsWrongKeyLength() => Assert.Throws<FormatException>(() => new KeyVault(_directory).Store(Guid.NewGuid(), "apple-public", new byte[32]));
    [Fact]
    public void CaptureJsonRoundTripsAndUnknownSchemaFails()
    {
        var json = ScanFiles.ToJson([new() { Address = "AA:BB:CC:DD:EE:FF", PayloadHex = "02 30 01", IsMock = true }], "mock");
        Assert.True(Assert.Single(ScanFiles.FromJson(json)).IsMock);
        Assert.Throws<FormatException>(() => ScanFiles.FromJson(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
    }
    [Fact]
    public void CsvEscapesAdvertisedNamesAndPreventsFormulas()
    {
        var scan = new ScanRepository(); scan.Add(new() { Address = "AA", Name = "=RUN(\"bad\")", PayloadHex = "02 30 01" });
        var csv = ScanFiles.ToCsv(scan.Devices); Assert.Contains("\"'=RUN(\"\"bad\"\")\"", csv); Assert.Contains("Address,AddressType", csv);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
