namespace BLEStudio.Core;

public sealed record GattSubmission(Guid ServiceUuid, byte[] ServiceData, string RequestedAdHex);

public static class GattPayloadPolicy
{
    public static IReadOnlyList<ValidationIssue> Validate(AdvertisementProfile p, AdapterCapabilities adapter)
    {
        var issues = new List<ValidationIssue>();
        void Error(string field, string message) => issues.Add(new(field, message));
        if (!adapter.Available || !adapter.CanAdvertise) Error("Adapter", "GATT peripheral advertising is unavailable; check the radio and peripheral-role capability.");
        if (p.AdvertisingIntervalMs != null || p.RequestedAddress.Length != 0 || p.AddressType != "System" || p.AddressRotationSeconds != null)
            Error("Policy", "Windows manages the GATT advertising interval and address; these requests cannot be applied.");
        if (p.LocalName.Length != 0 || p.ScanResponseHex.Length != 0 || p.IncludeTransmitPower || p.PreferredTransmitPowerDbm != null || p.Anonymous || p.PrimaryPhy != PhyPreference.Default || p.SecondaryPhy != PhyPreference.Default)
            Error("Policy", "This GATT backend supports ServiceData, connectable/discoverable controls and duration; other requested Publisher settings cannot be applied.");
        if (p.DurationSeconds is < 0 or > 86400) Error("Duration", "Duration must be 0–86400 seconds.");
        if (p.UseExtendedAdvertising && !adapter.ExtendedAdvertising) Error("Extended", "The adapter does not report extended advertising support.");
        try
        {
            var ads = AdCodec.Decode(Hex.Parse(p.PayloadHex));
            var service = ads.Where(x => x.Type is 0x16 or 0x20 or 0x21).ToArray();
            if (service.Length != 1) Error("Payload", "GATT requires exactly one Service Data AD structure (16/32/128-bit UUID).");
            if (ads.Any(x => x.Type is not (0x01 or 0x16 or 0x20 or 0x21))) Error("Payload", "GATT cannot submit arbitrary manufacturer/custom AD structures. Use Windows Publisher for those.");
            if (ads.Count(x => x.Type == 1) > 1) Error("Flags", "Only one requested flags structure is permitted.");
            foreach (var flags in ads.Where(x => x.Type == 1))
            {
                if (flags.Data.Length != 1 || flags.Data[0] is not (0x04 or 0x06)) Error("Flags", "Only 0x04/0x06 are accepted as requested flags; Windows generates the actual flags.");
                else issues.Add(new("Flags", "Requested flags are not passed as raw bytes. Windows generates flags; an external primary-advertisement capture must verify them.", ValidationSeverity.Warning));
            }
            if (service.Length == 1)
            {
                var width = service[0].Type switch { 0x16 => 2, 0x20 => 4, _ => 16 };
                if (service[0].Data.Length < width) Error("Payload", "Service Data is missing UUID bytes.");
                else if (service[0].Data.Length - width > (p.UseExtendedAdvertising ? adapter.MaxExtendedLength : 29)) Error("Payload", "Service Data exceeds the requested transport budget.");
            }
        }
        catch (FormatException ex) { Error("Payload", ex.Message); }
        return issues;
    }
    public static GattSubmission Build(AdvertisementProfile p)
    {
        var ads = AdCodec.Decode(Hex.Parse(p.PayloadHex));
        var ad = ads.Single(x => x.Type is 0x16 or 0x20 or 0x21);
        var uuid = AdCodec.ServiceUuids(ad).Single();
        var width = ad.Type switch { 0x16 => 2, 0x20 => 4, _ => 16 };
        var guid = width == 16 ? Guid.Parse(uuid) : Guid.Parse($"{uuid.PadLeft(8, '0')}-0000-1000-8000-00805f9b34fb");
        return new(guid, ad.Data[width..], Hex.Format(AdCodec.Encode(ads)));
    }
    public static void RequireCompleteStatus(string status)
    {
        if (status == "StartedWithoutAllAdvertisementData") throw new BluetoothOperationException("IncompleteAdvertisement", "Windows started without all requested advertisement data. This is not a complete FHN advertisement and cannot verify tracking.");
        if (status != "Started") throw new BluetoothOperationException("GattAborted", $"GATT advertising did not start completely: {status}.");
    }
}
