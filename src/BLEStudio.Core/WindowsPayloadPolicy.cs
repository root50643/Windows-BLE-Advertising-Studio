namespace BLEStudio.Core;

public sealed record ValidationIssue(string Field, string Message, ValidationSeverity Severity = ValidationSeverity.Error);

public static class WindowsPayloadPolicy
{
    private static readonly HashSet<byte> Reserved = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,
        0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x3D];
    public static bool IsReserved(byte type) => Reserved.Contains(type);
    public static int Capacity(AdvertisementProfile profile, AdapterCapabilities adapter) => profile.UseExtendedAdvertising ? adapter.MaxExtendedLength : 31;
    public static IReadOnlyList<ValidationIssue> Validate(AdvertisementProfile p, AdapterCapabilities adapter)
    {
        var issues = new List<ValidationIssue>();
        void Add(string field, string message) => issues.Add(new(field, message));
        if (p.SchemaVersion != 1) Add("SchemaVersion", "Unsupported profile schema version.");
        if (string.IsNullOrWhiteSpace(p.Name)) Add("Name", "A profile name is required.");
        if (!adapter.Available) Add("Adapter", "No Bluetooth adapter is available.");
        else if (!adapter.CanAdvertise) Add("Adapter", "Advertising is unavailable: inspect adapter capabilities and radio state.");
        if (p.UseExtendedAdvertising && !adapter.ExtendedAdvertising) Add("Extended", "Extended advertising is not reported as supported by this adapter.");
        if (p.DurationSeconds is < 0 or > 86400) Add("Duration", "Duration must be 0 (manual stop) or 1–86400 seconds.");
        if (p.AdvertisingIntervalMs != null) Add("Interval", "Windows Publisher does not expose interval control.");
        if (p.Connectable != null || p.Scannable != null) Add("Type", "Publisher controls advertising policy; connectable/scannable flags cannot be selected.");
        if (p.LocalName.Length > 0) Add("LocalName", "Local name is reserved by Windows Publisher.");
        if (p.AddressType != "System" || p.RequestedAddress.Length > 0 || p.AddressRotationSeconds != null) Add("Address", "MAC address, address type and rotation are controlled by Windows.");
        if (p.ScanResponseHex.Length > 0) Add("ScanResponse", "Windows Publisher has no scan response editor.");
        if (!p.UseExtendedAdvertising && (p.IncludeTransmitPower || p.Anonymous || p.PrimaryPhy != PhyPreference.Default || p.SecondaryPhy != PhyPreference.Default)) Add("Extended", "Header TX power, anonymity and PHY selection require extended advertising.");
        if (p.PrimaryPhy == PhyPreference.LE2M) Add("PrimaryPhy", "Primary advertising PHY cannot be LE 2M.");
        if (p.PreferredTransmitPowerDbm is < -127 or > 20) Add("TXPower", "Preferred TX power must be in the BLE range -127 to +20 dBm.");
        Check(p.IncludeTransmitPower, "Header TX power"); Check(p.Anonymous, "Anonymous");
        Check(p.PreferredTransmitPowerDbm != null, "Preferred TX power");
        Check(p.PrimaryPhy != PhyPreference.Default || p.SecondaryPhy != PhyPreference.Default, "PHY selection");
        Check(p.PrimaryPhy == PhyPreference.LECoded || p.SecondaryPhy == PhyPreference.LECoded, "LE Coded PHY");
        Check(p.SecondaryPhy == PhyPreference.LE2M, "LE 2M PHY");
        void Check(bool selected, string feature) { if (selected && adapter.Support(feature) is SupportStatus.Unsupported or SupportStatus.Unknown) Add(feature, $"{feature} is unavailable or unverified at the API level."); }
        try
        {
            var bytes = Hex.Parse(p.PayloadHex);
            if (bytes.Length == 0) Add("Payload", "At least one AD structure is required.");
            if (bytes.Length > Capacity(p, adapter)) Add("Payload", $"Payload is {bytes.Length} bytes; current budget is {Capacity(p, adapter)} bytes including AD headers.");
            else if (!p.UseExtendedAdvertising && bytes.Length > 28) issues.Add(new("Payload", "Above the conservative 28-byte budget. Windows may need room for additional fields; actual API acceptance and external capture must be checked.", ValidationSeverity.Warning));
            var ads = AdCodec.Decode(bytes);
            foreach (var ad in ads)
            {
                if (IsReserved(ad.Type)) Add("Payload", $"AD type 0x{ad.Type:X2} ({ad.Description}) is reserved by Windows Publisher. Retained for analysis; cannot transmit.");
                if (ad.Type == 0xFF && ad.Data.Length < 2) Add("Payload", "Manufacturer data requires a 2-byte company ID.");
            }
            if (!AdCodec.Encode(ads).SequenceEqual(bytes)) Add("Payload", "Trailing padding is not submitted. Remove zero padding to obtain an exact submission preview.");
        }
        catch (FormatException ex) { Add("Payload", ex.Message); }
        return issues;
    }
}
