using BLEStudio.Core;
using BLEStudio.Protocols;
using Microsoft.Extensions.Time.Testing;

namespace BLEStudio.UnitTests;

public class TrackingTests
{
    private static readonly AdapterCapabilities Ready = new() { Available = true, CanAdvertise = true, ExtendedAdvertising = true, MaxExtendedLength = 160 };
    [Theory]
    [InlineData(0u, "e6cec9ca5505f86e82781bcbe75984acb3ce5e03")]
    [InlineData(1023u, "e6cec9ca5505f86e82781bcbe75984acb3ce5e03")]
    [InlineData(1024u, "3a19ac7db9a3a9140c0faceae210ec57a127fb31")]
    [InlineData(2048u, "8a1b3ed0f1665e25085983a92e4e6302bce5264e")]
    [InlineData(uint.MaxValue, "d0875fc34ce1d99baf8e3d4ae56c043641a8c667")]
    public void GoogleEidMatchesPinnedPythonReference(uint seconds, string expected) => Assert.Equal(expected, Convert.ToHexString(TrackerCryptography.GoogleEid(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray(), seconds)).ToLowerInvariant());
    [Fact] public void AppleGeneratedPrivateKeyReproducesPublicKey() { var (secret, pub) = TrackerCryptography.CreateAppleIdentity(); Assert.Equal(pub, TrackerCryptography.ApplePublicKey(secret)); Assert.Equal(31, Hex.Parse(AppleFindMyResearch.Preview(pub).PayloadHex).Length); }
    [Fact] public void InvalidApplePrivateScalarRejected() => Assert.Throws<FormatException>(() => TrackerCryptography.ApplePublicKey(new byte[28]));
    [Fact] public void CompleteFhnUsesGattPolicyAndCorrectUuidEncoding()
    {
        var p = new AdvertisementProfile { PayloadHex = GoogleFindHubResearch.Preview(new byte[20]).PayloadHex };
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready), x => x.Severity == ValidationSeverity.Error && x.Message.Contains("reserved"));
        Assert.DoesNotContain(GattPayloadPolicy.Validate(p, Ready), x => x.Severity == ValidationSeverity.Error);
        var submission = GattPayloadPolicy.Build(p);
        Assert.Equal(Guid.Parse("0000feaa-0000-1000-8000-00805f9b34fb"), submission.ServiceUuid);
        Assert.Equal(21, submission.ServiceData.Length); Assert.Equal(0x40, submission.ServiceData[0]);
    }
    [Fact] public void GattDoesNotSilentlyDropManufacturerData() => Assert.Contains(GattPayloadPolicy.Validate(new(), Ready), x => x.Severity == ValidationSeverity.Error);
    [Theory][InlineData("StartedWithoutAllAdvertisementData")][InlineData("Aborted")][InlineData("Stopped")]
    public void GattIncompleteOrRejectedStatusNeverCountsAsSuccess(string status) => Assert.Throws<BluetoothOperationException>(() => GattPayloadPolicy.RequireCompleteStatus(status));
    [Fact] public void Legacy31IsControlledWarningAnd32IsError()
    {
        var p = new AdvertisementProfile { PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[27])])) };
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready), x => x.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(WindowsPayloadPolicy.Validate(p, Ready), x => x.Severity == ValidationSeverity.Error);
        p.PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[28])]));
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready), x => x.Severity == ValidationSeverity.Error);
    }
    [Fact] public void AppleAddressMismatchPreventsTrackingEvenAfterARealReport()
    {
        var id = AppleIdentity(); var now = DateTimeOffset.UtcNow; var profile = TrackerCryptography.BuildProfile(id, [], now);
        var evidence = new PacketEvidence("Phone BLE scanner", now, "C0:00:00:00:00:00", "RandomStatic", "LegacyNonConnectable", profile.PayloadHex);
        var capability = TrackerCryptography.VerifyPacket(new(ApiAccepted: true), id, profile, evidence, now.AddSeconds(-1), now);
        capability = TrackerCryptography.VerifyLocation(capability, id, Report(id, now), now.AddSeconds(-1));
        Assert.True(capability.PacketVerified); Assert.True(capability.LocationVerified); Assert.False(capability.AddressCompatible); Assert.False(capability.RealTrackingVerified);
    }
    [Fact] public void ScanResponseAndExtendedTransportDoNotPassPrimaryLegacyGate()
    {
        var id = AppleIdentity(); var now = DateTimeOffset.UtcNow; var profile = TrackerCryptography.BuildProfile(id, [], now);
        foreach (var type in new[] { "LegacyScanResponse", "ExtendedNonConnectable" })
            Assert.False(TrackerCryptography.VerifyPacket(new(ApiAccepted: true), id, profile, new("Phone", now, "C0:00:00:00:00:00", "RandomStatic", type, profile.PayloadHex), now.AddSeconds(-1), now).PacketVerified);
    }
    [Fact] public void GoogleReportMustMatchCurrentAdvertisedEid()
    {
        var id = new TrackerIdentity(); var now = DateTimeOffset.UtcNow; var report = Report(id, now) with { Source = "Google Find Hub", AdvertisedIdentityHex = "abcd" };
        Assert.False(TrackerCryptography.VerifyLocation(new(true, true), id, report, now.AddSeconds(-1), "ffff").RealTrackingVerified);
        Assert.True(TrackerCryptography.VerifyLocation(new(true, true), id, report, now.AddSeconds(-1), "ABCD").RealTrackingVerified);
        Assert.False(TrackerCryptography.VerifyLocation(new(true, true), id, report with { CryptographicallyVerified = false }, now.AddSeconds(-1), "abcd").RealTrackingVerified);
    }
    [Fact] public async Task ApiRejectionStopsAndClearsRequestedActive()
    {
        var radio = new TestRadio { Reject = true }; await using var session = new TrackerSession(_ => radio);
        await Assert.ThrowsAsync<BluetoothOperationException>(() => session.StartAsync(AppleIdentity(), []));
        Assert.False(session.Running); Assert.False(session.RequestedActive); Assert.False(session.Capability.ApiAccepted); Assert.True(radio.Stops > 0);
    }
    [Fact] public async Task SuspendResumeRedetectsAndRestoresOnlyPreviouslyActiveMode()
    {
        var radios = new List<TestRadio>(); await using var session = new TrackerSession(_ => { var r = new TestRadio(); radios.Add(r); return r; });
        await session.StartAsync(AppleIdentity(), []); await session.SuspendAsync(); Assert.False(session.Running); Assert.True(session.RequestedActive);
        await session.ResumeAsync(); Assert.True(session.Running); Assert.Equal(2, radios.Count); Assert.All(radios, r => Assert.Equal(1, r.Detections));
        await session.StopAsync(); await session.ResumeAsync(); Assert.False(session.Running); Assert.Equal(2, radios.Count);
    }
    [Fact] public async Task GoogleRotationRestartsRadioWithNewEidAndResetsEvidence()
    {
        var now = DateTimeOffset.UtcNow; var clock = new FakeTimeProvider(now); var radios = new List<TestRadio>();
        await using var session = new TrackerSession(_ => { var r = new TestRadio(); radios.Add(r); return r; }, clock);
        await session.StartAsync(new() { PairDate = now }, new byte[32]); var original = session.CurrentProfile!.PayloadHex;
        clock.Advance(TimeSpan.FromSeconds(1024));
        for (var i = 0; i < 100 && radios.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(2, radios.Count); Assert.NotEqual(original, session.CurrentProfile!.PayloadHex); Assert.False(session.Capability.PacketVerified); Assert.False(session.Capability.LocationVerified);
    }
    private static TrackerIdentity AppleIdentity() => new() { Protocol = ResearchProtocol.AppleFindMy, PublicKeyHex = Convert.ToHexString(TrackerCryptography.CreateAppleIdentity().PublicKey) };
    [Fact] public async Task RotationInvalidatesPriorProofBeforeTheNextApiRequestCompletes()
    {
        var now = DateTimeOffset.UtcNow; var clock = new FakeTimeProvider(now); var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        await using var session = new TrackerSession(_ => new TestRadio { PendingStart = ++count == 2 ? pending.Task : Task.CompletedTask }, clock);
        var identity = new TrackerIdentity { PairDate = now }; await session.StartAsync(identity, new byte[32]);
        session.ImportEvidence(new("Offline unit fixture", now, "C0:00:00:00:00:00", "RandomResolvable", "LegacyNonConnectable", session.CurrentProfile!.PayloadHex));
        var eid = Convert.ToHexString(AdCodec.Decode(Hex.Parse(session.CurrentProfile.PayloadHex)).Single(x => x.Type == 0x16).Data.AsSpan(3, 20));
        session.ApplyReport(Report(identity, now) with { Source = "Google Find Hub", AdvertisedIdentityHex = eid }); Assert.True(session.Capability.RealTrackingVerified);
        clock.Advance(TimeSpan.FromSeconds(1024)); for (var i = 0; i < 100 && count < 2; i++) await Task.Delay(10);
        try { Assert.Equal(2, count); Assert.False(session.Capability.ApiAccepted); Assert.False(session.Capability.PacketVerified); Assert.False(session.Capability.LocationVerified); }
        finally { pending.TrySetResult(); }
    }
    private static LocationReport Report(TrackerIdentity identity, DateTimeOffset now) => new(identity.Id, now, 0, 0, 10, "Apple Find My", true, "unit-test-fixture-not-a-network-report");
    private sealed class TestRadio : IBluetoothBackend
    {
        public bool Reject { get; init; }
        public Task PendingStart { get; init; } = Task.CompletedTask;
        public int Stops, Detections;
        public string Name => "Unit-test radio double; no hardware";
        public event EventHandler<BackendStatus>? AdvertisingStatusChanged { add { } remove { } }
        public event EventHandler<BackendStatus>? ScanningStatusChanged { add { } remove { } }
        public event EventHandler<ScanPacket>? PacketReceived { add { } remove { } }
        public Task<AdapterCapabilities> DetectAsync(CancellationToken cancellationToken = default) { Detections++; return Task.FromResult(Ready); }
        public Task StartAdvertisingAsync(AdvertisementProfile profile, CancellationToken cancellationToken = default) => Reject ? Task.FromException(new BluetoothOperationException("ApiRejected", "Fixture rejection")) : PendingStart;
        public Task StopAdvertisingAsync() { Stops++; return Task.CompletedTask; }
        public Task StartScanningAsync(bool active = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopScanningAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
