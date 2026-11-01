namespace BLEStudio.Core;

public sealed record TrackerIdentity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Windows PC";
    public ResearchProtocol Protocol { get; init; } = ResearchProtocol.GoogleFindHub;
    public DateTimeOffset PairDate { get; init; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public string PublicKeyHex { get; init; } = "";
    public string SecretReference { get; init; } = "";
    public string? CanonicId { get; init; }
}
public sealed record LocationReport(Guid TrackerId, DateTimeOffset ObservedAt, double Latitude, double Longitude,
    double AccuracyMeters, string Source, bool CryptographicallyVerified, string ReportId, string? AdvertisedIdentityHex = null)
{
    public bool IsValid => TrackerId != Guid.Empty && double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180 && double.IsFinite(AccuracyMeters) && AccuracyMeters >= 0 &&
        ObservedAt != default && ObservedAt <= DateTimeOffset.UtcNow.AddMinutes(5) && Source is "Apple Find My" or "Google Find Hub" && ReportId.Length > 0;
}
public sealed record TrackingResult(IReadOnlyList<LocationReport> Reports, string Status, DateTimeOffset? LastQueryAt = null);
public interface ITrackingProvider
{
    Task<TrackingResult> GetLocationsAsync(Guid trackerId, CancellationToken cancellationToken = default);
}
public sealed record PacketEvidence(string Source, DateTimeOffset CapturedAt, string Address, string AddressType,
    string AdvertisementType, string PayloadHex, bool IsScanResponse = false);
public sealed record TrackingCapability(bool ApiAccepted = false, bool PacketVerified = false, bool LocationVerified = false,
    bool AddressCompatible = false, string Detail = "尚未測試 / Not tested")
{
    public bool RealTrackingVerified => ApiAccepted && PacketVerified && LocationVerified && AddressCompatible;
}
