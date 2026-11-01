using BLEStudio.Core;

namespace BLEStudio.UnitTests;

public class PayloadTests
{
    [Theory]
    [InlineData("00 1a FF", "00 1A FF")]
    [InlineData("0xAA, 0xbb:01-02\r\n03", "AA BB 01 02 03")]
    [InlineData("", "")]
    [InlineData("aabbcc", "AA BB CC")]
    public void HexAcceptsSupportedFormats(string input, string expected) => Assert.Equal(expected, Hex.Format(Hex.Parse(input)));

    [Theory]
    [InlineData("ABC")][InlineData("GG")][InlineData("00;FF")][InlineData("00 / 01")]
    public void HexRejectsMalformedInput(string input) => Assert.Throws<FormatException>(() => Hex.Parse(input));

    [Fact] public void HexInputIsBounded() => Assert.Throws<FormatException>(() => Hex.Parse(new string('A', 65_537 * 2)));
    [Fact] public void HexDumpShowsOffsetAndAscii() => Assert.Contains("0000  41 00 7E", Hex.Dump([0x41, 0x00, 0x7E]));
    [Fact] public void DumpReplacesControlBytes() => Assert.Contains("A.~", Hex.Dump([0x41, 0x00, 0x7E]));

    [Fact]
    public void AdRoundTripPreservesMultipleStructures()
    {
        var input = Hex.Parse("02 01 06 06 FF 4C 00 01 02 03 04 30 AA BB CC");
        var ads = AdCodec.Decode(input);
        Assert.Equal(3, ads.Count); Assert.Equal(3, ads[1].Offset);
        Assert.Equal(input, AdCodec.Encode(ads));
    }
    [Theory][InlineData("03 FF 00")][InlineData("01")][InlineData("00 01")][InlineData("01 00")]
    public void TruncatedOrInvalidStructuresRejected(string input) => Assert.Throws<FormatException>(() => AdCodec.Decode(Hex.Parse(input)));
    [Fact] public void ZeroPaddingAcceptedByParser() => Assert.Single(AdCodec.Decode(Hex.Parse("02 30 01 00 00")));
    [Fact] public void StructureDataLimitEnforced() => Assert.Throws<FormatException>(() => AdCodec.Encode([new(0x30, new byte[255])]));
    [Fact]
    public void ManufacturerCompanyIdIsLittleEndian()
    {
        var ad = AdCodec.Manufacturer(0x004C, [0x12, 0x19]);
        Assert.Equal("05 FF 4C 00 12 19", Hex.Format(AdCodec.Encode([ad])));
        Assert.Contains("Apple", ad.Description);
    }
    [Theory]
    [InlineData("FEAA", "AA FE")]
    [InlineData("12345678", "78 56 34 12")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "FF EE DD CC BB AA 99 88 77 66 55 44 33 22 11 00")]
    public void UuidsUseBluetoothByteOrder(string uuid, string expected) => Assert.Equal(expected, Hex.Format(AdCodec.UuidBytes(uuid)));
    [Theory][InlineData("FEAA")][InlineData("12345678")][InlineData("00112233-4455-6677-8899-aabbccddeeff")]
    public void ServiceDataUuidsDecodeAtAllWidths(string uuid)
    {
        var ad = AdCodec.ServiceData(uuid, [0xAA, 0xBB]);
        Assert.Equal(uuid.ToLowerInvariant(), Assert.Single(AdCodec.ServiceUuids(ad)).ToLowerInvariant());
    }
    [Fact]
    public void AllAdByteLengthsRoundTrip()
    {
        var random = new Random(621);
        for (var i = 0; i <= 254; i++)
        {
            var bytes = new byte[i]; random.NextBytes(bytes);
            var parsed = Assert.Single(AdCodec.Decode(AdCodec.Encode([new(0x30, bytes)])));
            Assert.Equal(bytes, parsed.Data);
        }
    }
}
