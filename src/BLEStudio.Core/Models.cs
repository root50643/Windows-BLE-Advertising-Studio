using System.Text.Json.Serialization;

namespace BLEStudio.Core;

public enum SupportStatus { Supported, Unsupported, Unknown, RequiresVerification }
public enum OperationState { Stopped, Starting, Started, Stopping, Waiting, Aborted }
public enum ResearchProtocol { Generic, AppleFindMy, GoogleFindHub }
public enum PhyPreference { Default, LE1M, LE2M, LECoded }

public sealed record Capability(string Feature, SupportStatus Status, string Reason);
public sealed record AdapterCapabilities
{
    public string Name { get; init; } = "No Bluetooth adapter";
    public string DeviceId { get; init; } = "Unavailable";
    public string PnpDeviceId { get; init; } = "Unknown";
    public string DriverVersion { get; init; } = "Unknown";
    public string OperatingSystem { get; init; } = Environment.OSVersion.VersionString;
    public string RadioState { get; init; } = "Unknown";
    public bool IsMock { get; init; }
    public bool Available { get; init; }
    public bool CanAdvertise { get; init; }
    public bool CanScan { get; init; }
    public bool ExtendedAdvertising { get; init; }
    public int MaxExtendedLength { get; init; } = 31;
    public IReadOnlyList<Capability> Features { get; init; } = [];
    public SupportStatus Support(string feature) => Features.FirstOrDefault(x => x.Feature == feature)?.Status ?? SupportStatus.Unknown;
}

public sealed class AdvertisementProfile
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Revision { get; set; } = 1;
    public string Name { get; set; } = "Generic BLE Beacon";
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public ResearchProtocol Protocol { get; set; }
    public bool UseExtendedAdvertising { get; set; }
    public bool IncludeTransmitPower { get; set; }
    public short? PreferredTransmitPowerDbm { get; set; }
    public bool Anonymous { get; set; }
    public PhyPreference PrimaryPhy { get; set; }
    public PhyPreference SecondaryPhy { get; set; }
    public int DurationSeconds { get; set; }
    // Desired unsupported settings are preserved for portability and rejected before Start.
    public double? AdvertisingIntervalMs { get; set; }
    public bool? Connectable { get; set; }
    public bool? Scannable { get; set; }
    public string LocalName { get; set; } = "";
    public string AddressType { get; set; } = "System";
    public string RequestedAddress { get; set; } = "";
    public int? AddressRotationSeconds { get; set; }
    public string ScanResponseHex { get; set; } = "";
    public string PayloadHex { get; set; } = "07 FF FF FF 01 02 03 04";
    public string Notes { get; set; } = "";
    // Reference only. Secret material lives in the per-user DPAPI vault.
    public string? KeyReference { get; set; }
}

public sealed record BackendStatus(OperationState State, string Message, string? ErrorCode = null,
    DateTimeOffset? StartedAt = null, string? AcceptedPayloadHex = null);

public sealed record ScanPacket
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Address { get; init; } = "";
    public string AddressType { get; init; } = "Unknown";
    public string Name { get; init; } = "";
    public short Rssi { get; init; }
    public string AdvertisementType { get; init; } = "Unknown";
    public string PayloadHex { get; init; } = "";
    public bool IsMock { get; init; }
    [JsonIgnore] public IReadOnlyList<AdStructure> Structures => AdCodec.Decode(Hex.Parse(PayloadHex));
    [JsonIgnore] public string Manufacturers => string.Join(", ", Structures.Where(x => x.Type == 0xFF && x.Data.Length >= 2)
        .Select(x => $"0x{x.Data[0] | x.Data[1] << 8:X4} ({CompanyNames.Lookup((ushort)(x.Data[0] | x.Data[1] << 8))})"));
    [JsonIgnore] public string Services => string.Join(", ", Structures.SelectMany(AdCodec.ServiceUuids).Distinct());
}

public interface IBluetoothBackend : IAsyncDisposable
{
    string Name { get; }
    event EventHandler<BackendStatus>? AdvertisingStatusChanged;
    event EventHandler<BackendStatus>? ScanningStatusChanged;
    event EventHandler<ScanPacket>? PacketReceived;
    Task<AdapterCapabilities> DetectAsync(CancellationToken cancellationToken = default);
    Task StartAdvertisingAsync(AdvertisementProfile profile, CancellationToken cancellationToken = default);
    Task StopAdvertisingAsync();
    Task StartScanningAsync(bool active = true, CancellationToken cancellationToken = default);
    Task StopScanningAsync();
}

public sealed class BluetoothOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Category, string Code, string Message);
