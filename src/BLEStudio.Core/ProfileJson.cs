using System.Text.Json;
using System.Text.Json.Serialization;

namespace BLEStudio.Core;

public static class ProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Serialize(AdvertisementProfile profile) => JsonSerializer.Serialize(profile, Options);
    public static AdvertisementProfile Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<AdvertisementProfile>(json, Options) ?? throw new FormatException("Profile is empty.");
        if (p.SchemaVersion != 1) throw new FormatException($"Profile schema {p.SchemaVersion} is unsupported; expected 1.");
        if (p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name)) throw new FormatException("Profile requires a nonempty ID and name.");
        if (p.PayloadHex == null || p.LocalName == null || p.AddressType == null || p.RequestedAddress == null || p.ScanResponseHex == null || p.Notes == null) throw new FormatException("Profile string fields cannot be null.");
        if (!Enum.IsDefined(p.Protocol) || !Enum.IsDefined(p.PrimaryPhy) || !Enum.IsDefined(p.SecondaryPhy)) throw new FormatException("Profile contains an unknown enum value.");
        AdCodec.Decode(Hex.Parse(p.PayloadHex));
        return p;
    }
    public static AdvertisementProfile Clone(AdvertisementProfile p) => Deserialize(Serialize(p));
}
