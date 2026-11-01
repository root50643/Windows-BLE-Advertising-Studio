using System.Text.Json;
using BLEStudio.Core;

namespace BLEStudio.UnitTests;

public class ValidationAndProfileTests
{
    private static AdapterCapabilities Ready => new() { Available = true, CanAdvertise = true, CanScan = true, ExtendedAdvertising = true, MaxExtendedLength = 254,
        Features = [new("Preferred TX power", SupportStatus.RequiresVerification, "test"), new("Header TX power", SupportStatus.RequiresVerification, "test"), new("Anonymous", SupportStatus.RequiresVerification, "test")] };
    [Fact] public void GenericManufacturerPayloadValidates() => Assert.Empty(WindowsPayloadPolicy.Validate(new(), Ready));
    [Fact] public void MissingAdapterFailsExplicitly() => Assert.Contains(WindowsPayloadPolicy.Validate(new(), new()), x => x.Field == "Adapter");
    [Fact] public void RadioOffFailsWithoutCrashing() => Assert.Contains(WindowsPayloadPolicy.Validate(new(), Ready with { CanAdvertise = false }), x => x.Field == "Adapter");
    [Theory][InlineData(0x01)][InlineData(0x09)][InlineData(0x16)][InlineData(0x20)][InlineData(0x21)][InlineData(0x19)][InlineData(0x03)][InlineData(0x14)]
    public void ReservedAdTypesCannotBePublished(byte type)
    {
        var p = new AdvertisementProfile { PayloadHex = Hex.Format(AdCodec.Encode([new(type, [0x01, 0x02])])) };
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready), x => x.Message.Contains("reserved"));
    }
    [Fact]
    public void LegacyCapacityIncludesAdHeadersAndWindowsReserve()
    {
        var p = new AdvertisementProfile { PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[24])])) };
        Assert.Empty(WindowsPayloadPolicy.Validate(p, Ready));
        p.PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[25])]));
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready), x => x.Message.Contains("budget"));
    }
    [Fact]
    public void ExtendedUsesReportedControllerLimit()
    {
        var p = new AdvertisementProfile { UseExtendedAdvertising = true, PayloadHex = Hex.Format(AdCodec.Encode([new(0x30, new byte[60])])) };
        Assert.Empty(WindowsPayloadPolicy.Validate(p, Ready));
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready with { MaxExtendedLength = 50 }), x => x.Message.Contains("budget"));
        Assert.Contains(WindowsPayloadPolicy.Validate(p, Ready with { ExtendedAdvertising = false }), x => x.Field == "Extended");
    }
    [Fact]
    public void UnsupportedParametersAreNotSilentlyIgnored()
    {
        var p = new AdvertisementProfile { LocalName = "name", AdvertisingIntervalMs = 100, Connectable = false, Scannable = true,
            RequestedAddress = "C0:00:00:00:00:01", AddressRotationSeconds = 60, ScanResponseHex = "01" };
        var issues = WindowsPayloadPolicy.Validate(p, Ready);
        foreach (var field in new[] { "LocalName", "Interval", "Type", "Address", "ScanResponse" }) Assert.Contains(issues, x => x.Field == field);
    }
    [Fact] public void ExtendedHeaderOptionsCannotPretendToWorkOnLegacy() => Assert.Contains(WindowsPayloadPolicy.Validate(new() { Anonymous = true, IncludeTransmitPower = true }, Ready), x => x.Field == "Extended");
    [Fact] public void UnknownPhyCapabilityBlocksSelection() => Assert.Contains(WindowsPayloadPolicy.Validate(new() { UseExtendedAdvertising = true, PrimaryPhy = PhyPreference.LECoded }, Ready), x => x.Field == "PHY selection");
    [Fact] public void LegacyZeroPaddingCannotBeSilentlyDropped() => Assert.Contains(WindowsPayloadPolicy.Validate(new() { PayloadHex = "02 30 01 00" }, Ready), x => x.Message.Contains("padding"));
    [Theory][InlineData(-1)][InlineData(86401)] public void DurationMustBeBounded(int duration) => Assert.Contains(WindowsPayloadPolicy.Validate(new() { DurationSeconds = duration }, Ready), x => x.Field == "Duration");
    [Fact]
    public void ProfileJsonRoundTripsDesiredUnsupportedSettings()
    {
        var p = new AdvertisementProfile { Name = "測試 profile", AdvertisingIntervalMs = 200, RequestedAddress = "C0:00:00:00:00:01", Protocol = ResearchProtocol.AppleFindMy, PreferredTransmitPowerDbm = -12 };
        var copy = ProfileJson.Deserialize(ProfileJson.Serialize(p));
        Assert.Equal(p.Id, copy.Id); Assert.Equal(p.Name, copy.Name); Assert.Equal(200, copy.AdvertisingIntervalMs); Assert.Equal((short)-12, copy.PreferredTransmitPowerDbm); Assert.Equal(p.RequestedAddress, copy.RequestedAddress);
    }
    [Fact] public void FutureSchemaRejected() => Assert.Throws<FormatException>(() => ProfileJson.Deserialize(ProfileJson.Serialize(new() { SchemaVersion = 42 })));
    [Fact] public void UnknownFieldsRejected() => Assert.Throws<JsonException>(() => ProfileJson.Deserialize("{\"obsoleteUnknownField\":true}"));
    [Fact] public void NullStringsRejected() => Assert.Throws<FormatException>(() => ProfileJson.Deserialize("{\"payloadHex\":null}"));
    [Fact] public void NumericUnknownEnumsRejected() => Assert.Throws<FormatException>(() => ProfileJson.Deserialize("{\"protocol\":999}"));
    [Fact] public void CloneIsIndependent() { var p = new AdvertisementProfile(); var copy = ProfileJson.Clone(p); copy.Name = "copy"; Assert.NotEqual(p.Name, copy.Name); }
}
