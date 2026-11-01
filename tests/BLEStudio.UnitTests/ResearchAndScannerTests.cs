using BLEStudio.Core;
using BLEStudio.Protocols;

namespace BLEStudio.UnitTests;

public class ResearchAndScannerTests
{
    [Fact]
    public void ApplePreviewMatchesReferenceOffsetsAndAddressSplit()
    {
        var key = Enumerable.Range(0, 28).Select(x => (byte)x).ToArray(); key[0] = 0x82;
        var preview = AppleFindMyResearch.Preview(key); var bytes = Hex.Parse(preview.PayloadHex);
        Assert.Equal(31, bytes.Length); Assert.Equal("C2:01:02:03:04:05", preview.RequiredAddress);
        Assert.Equal(key[6..], bytes[7..29]); Assert.Equal(2, bytes[29]);
        var report = AppleFindMyResearch.Analyze(AdCodec.Decode(bytes), preview.RequiredAddress);
        Assert.True(report.FormatValid); Assert.Equal(Hex.Format(key), report.Fields["Reconstructed public-key x (unverified)"]);
    }
    [Fact] public void AppleWrongKeySizeRejected() => Assert.Throws<FormatException>(() => AppleFindMyResearch.Preview(new byte[32]));
    [Fact] public void AppleWrongSubtypeLengthIsNotValid() => Assert.False(AppleFindMyResearch.Analyze([new(0xFF, [0x4C, 0, 0x12, 0x18])]).FormatValid);
    [Fact] public void OrdinaryAppleManufacturerFrameIsNotFindMy() => Assert.False(AppleFindMyResearch.Analyze([AdCodec.Manufacturer(0x004C, [0x02, 0x15])]).Recognized);
    [Theory][InlineData(20, false)][InlineData(20, true)][InlineData(32, false)][InlineData(32, true)]
    public void GoogleEidWidthsAndOptionalHashedFlagsRoundTrip(int size, bool flags)
    {
        var eid = Enumerable.Range(1, size).Select(x => (byte)x).ToArray();
        var preview = GoogleFindHubResearch.Preview(eid, flags, flags ? (byte)0x55 : null);
        var result = GoogleFindHubResearch.Analyze(AdCodec.Decode(Hex.Parse(preview.PayloadHex)));
        Assert.True(result.FormatValid); Assert.Equal(Hex.Format(eid), result.Fields["EID"]); Assert.Equal(flags ? "0x41" : "0x40", result.Fields["Frame type"]);
        Assert.Equal(flags ? "0x55 (requires r to authenticate)" : "Omitted", result.Fields["Hashed flags"]);
    }
    [Fact] public void GoogleRejectsEddystoneAsFhn() => Assert.False(GoogleFindHubResearch.Analyze([new(0x16, [0xAA, 0xFE, 0x10, 0x00])]).FormatValid);
    [Fact] public void GoogleWrongEidLengthRejected() => Assert.Throws<FormatException>(() => GoogleFindHubResearch.Preview(new byte[28]));
    [Fact]
    public void UnwantedTrackingProtectionCannotOmitHashedFlags()
    {
        Assert.Throws<FormatException>(() => GoogleFindHubResearch.Preview(new byte[20], true));
        var data = new byte[23]; data[0] = 0xAA; data[1] = 0xFE; data[2] = 0x41;
        Assert.False(GoogleFindHubResearch.Analyze([new(0x16, data)]).FormatValid);
    }
    [Fact]
    public void ExternalEvidenceComparesBytesWithoutAssertingNetworkSuccess()
    {
        var capture = new ExternalObservation("Independent BLE sniffer", DateTimeOffset.UtcNow, "AA:BB:CC:DD:EE:FF", "02 30 01");
        var same = ProtocolResearch.Compare("02 30 01", capture); Assert.True(same.PayloadMatches); Assert.Contains("not authenticated", same.Description);
        Assert.False(ProtocolResearch.Compare("02 30 02", capture).PayloadMatches);
    }
    [Fact] public void EmptyEvidenceProvenanceRejected() => Assert.Throws<FormatException>(() => ProtocolResearch.Compare("02 30 01", new("", DateTimeOffset.UtcNow, "A", "02 30 01")));
    [Fact]
    public void ScannerAggregatesCountsRssiAndRetainsNameAcrossResponses()
    {
        var scan = new ScanRepository(); var first = new ScanPacket { Address = "AA", Name = "Beacon", PayloadHex = "04 FF 4C 00 01", Rssi = -50 };
        scan.Add(first); scan.Add(first with { Name = "", Rssi = -60, Timestamp = first.Timestamp.AddSeconds(1) });
        var device = Assert.Single(scan.Devices); Assert.Equal(2, device.Count); Assert.Equal("Beacon", device.Name); Assert.Equal(-60, device.Rssi); Assert.Equal(2, device.RssiHistory.Count); Assert.Single(scan.Packets);
    }
    [Fact]
    public void ScannerDuplicateRetentionAndMemoryAreBounded()
    {
        var scan = new ScanRepository(2, 3) { KeepDuplicates = true };
        for (var i = 0; i < 150; i++) scan.Add(new() { Address = $"{i % 3}", PayloadHex = "02 30 01", Rssi = -30 });
        Assert.Equal(2, scan.Devices.Count); Assert.Equal(3, scan.Packets.Count); Assert.All(scan.Devices, x => Assert.True(x.RssiHistory.Count <= 120));
    }
    [Fact] public void ScannerRejectsMalformedPacketBeforeChangingState() { var scan = new ScanRepository(); Assert.Throws<FormatException>(() => scan.Add(new() { Address = "A", PayloadHex = "04 FF" })); Assert.Empty(scan.Devices); }
    [Fact] public void AddressTypeSeparatesIdenticalAddresses() { var scan = new ScanRepository(); scan.Add(new() { Address = "AA", AddressType = "Public" }); scan.Add(new() { Address = "AA", AddressType = "Random" }); Assert.Equal(2, scan.Devices.Count); }
    [Fact]
    public void OutOfOrderCaptureKeepsFirstLastAndLatestPayloadCorrect()
    {
        var scan = new ScanRepository(); var now = DateTimeOffset.UtcNow;
        scan.Add(new() { Address = "AA", Timestamp = now, PayloadHex = "02 30 02", Rssi = -40 });
        scan.Add(new() { Address = "AA", Timestamp = now.AddMinutes(-1), PayloadHex = "02 30 01", Rssi = -70 });
        var device = Assert.Single(scan.Devices); Assert.Equal(now.AddMinutes(-1), device.FirstSeen); Assert.Equal(now, device.LastSeen);
        Assert.Equal("02 30 02", device.PayloadHex); Assert.Equal(-40, device.Rssi); Assert.Equal(2, device.Count);
    }
}
