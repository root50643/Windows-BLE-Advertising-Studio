using System.Buffers.Binary;
using BLEStudio.Core;

namespace BLEStudio.Protocols;

public sealed record ProtocolAnalysis(string Protocol, bool Recognized, bool FormatValid,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Findings);
public sealed record ResearchPreview(string PayloadHex, string? RequiredAddress, IReadOnlyList<string> Findings);
public sealed record ExternalObservation(string Source, DateTimeOffset CapturedAt, string Address, string PayloadHex);
public sealed record ObservationComparison(bool PayloadMatches, string Description, string ExpectedHex, string ObservedHex);

public static class AppleFindMyResearch
{
    public const string Source = "https://github.com/seemoo-lab/openhaystack/blob/ea05dadb9e6e71075ef08afb5c2087302fbb7210/Firmware/ESP32/main/openhaystack_main.c";
    public static ResearchPreview Preview(byte[] publicKey)
    {
        if (publicKey.Length != 28) throw new FormatException("OpenHaystack test advertising public key must be 28 bytes (P-224 x coordinate). This checks length, not curve membership or ownership.");
        var data = new byte[29];
        data[0] = 0x4C; data[1] = 0; data[2] = 0x12; data[3] = 0x19; data[4] = 0;
        publicKey.AsSpan(6, 22).CopyTo(data.AsSpan(5));
        data[27] = (byte)(publicKey[0] >> 6);
        var address = publicKey[..6]; address[0] |= 0xC0;
        return new(Hex.Format(AdCodec.Encode([new(0xFF, data)])), string.Join(":", address.Select(x => x.ToString("X2"))),
            ["OpenHaystack-format preview only; imported public key has not been cryptographically certified.",
             "The first 6 key bytes must be encoded in the BLE static-random address. Windows Publisher cannot set that address.",
             "31-byte structure exceeds this backend's conservative 28-byte legacy budget. Extended advertising changes transport semantics.",
             "Publisher Started is not evidence of Apple network participation or a location report."]);
    }
    public static ProtocolAnalysis Analyze(IEnumerable<AdStructure> structures, string? address = null)
    {
        var ad = structures.FirstOrDefault(x => x.Type == 0xFF && x.Data.Length >= 4 && x.Data[0] == 0x4C && x.Data[1] == 0 && x.Data[2] == 0x12);
        if (ad == null) return new("Apple Find My / OpenHaystack", false, false, new Dictionary<string, string>(), ["No Apple 0x004C / 0x12 structure found."]);
        var valid = ad.Data.Length == 29 && ad.Data[3] == 0x19 && ad.Data[27] <= 3;
        var fields = new Dictionary<string, string> { ["Company ID"] = "0x004C", ["Subtype"] = "0x12", ["Declared subtype length"] = $"0x{ad.Data[3]:X2}", ["Manufacturer data bytes"] = ad.Data.Length.ToString() };
        if (valid)
        {
            fields["State"] = $"0x{ad.Data[4]:X2}"; fields["Public-key suffix (22 bytes)"] = Hex.Format(ad.Data.AsSpan(5, 22));
            fields["Key high bits"] = ad.Data[27].ToString(); fields["Hint"] = $"0x{ad.Data[28]:X2}";
            if (!string.IsNullOrWhiteSpace(address))
            {
                var addr = Hex.Parse(address);
                if (addr.Length != 6 || (addr[0] & 0xC0) != 0xC0) throw new FormatException("OpenHaystack reconstruction requires a six-byte static-random address (top bits 11), written in display order.");
                addr[0] = (byte)((addr[0] & 0x3F) | ad.Data[27] << 6);
                fields["Reconstructed public-key x (unverified)"] = Hex.Format([.. addr, .. ad.Data.AsSpan(5, 22).ToArray()]);
            }
        }
        return new("Apple Find My / OpenHaystack", true, valid, fields,
            [valid ? "Matches the reference OpenHaystack offline-finding structure; other Find My frame variants are not certified by this parser." : "Unexpected subtype length, key-high-bit field or payload length.",
             "Windows cannot select the required key-derived address. This is not a complete Find My accessory implementation.", "No Apple account login, report retrieval or location verification is implemented."]);
    }
}

public static class GoogleFindHubResearch
{
    public const string Source = "https://developers.google.com/nearby/fast-pair/specifications/extensions/fmdn";
    public static ResearchPreview Preview(byte[] eid, bool unwantedTrackingProtection = false, byte? hashedFlags = null)
    {
        if (eid.Length is not (20 or 32)) throw new FormatException("An imported FHN EID must contain 20 or 32 bytes. An EID is not the 32-byte secret EIK.");
        if (unwantedTrackingProtection && hashedFlags == null) throw new FormatException("FHN unwanted tracking protection frames require a hashed-flags byte. It cannot be omitted in protection mode.");
        byte[] data = [0xAA, 0xFE, unwantedTrackingProtection ? (byte)0x41 : (byte)0x40, .. eid];
        if (hashedFlags is { } flags) data = [.. data, flags];
        var bytes = AdCodec.Encode([new(0x01, [0x06]), new(0x16, data)]);
        return new(Hex.Format(bytes), null,
            ["FHN v1.3 frame preview from an imported EID. EIK-to-EID cryptography, provisioning and registration are not performed.",
             "Windows Publisher reserves Flags (0x01) and Service Data (0x16), preventing this exact frame from being submitted.",
             "Windows cannot synchronize address rotation with EID rotation. Unregistered frames do not establish official Find Hub visibility.",
             "DULT, ringing, ownership checks and Fast Pair GATT behavior are accessory requirements outside this packet analyzer."]);
    }
    public static ProtocolAnalysis Analyze(IEnumerable<AdStructure> structures)
    {
        var ad = structures.FirstOrDefault(x => x.Type == 0x16 && x.Data.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(x.Data) == 0xFEAA);
        if (ad == null) return new("Google Find Hub / FHN", false, false, new Dictionary<string, string>(), ["No 0xFEAA service-data structure found."]);
        var fields = new Dictionary<string, string> { ["Service UUID"] = "FEAA", ["Service-data bytes"] = ad.Data.Length.ToString() };
        var valid = ad.Data.Length is 23 or 24 or 35 or 36 && ad.Data[2] is 0x40 or 0x41;
        if (valid && ad.Data[2] == 0x41 && ad.Data.Length is 23 or 35) valid = false;
        if (ad.Data.Length >= 3) fields["Frame type"] = $"0x{ad.Data[2]:X2}";
        if (valid)
        {
            var eidLength = ad.Data.Length <= 24 ? 20 : 32;
            fields["EID"] = Hex.Format(ad.Data.AsSpan(3, eidLength));
            fields["Curve / EID size"] = eidLength == 20 ? "secp160r1 / 20 bytes" : "secp256r1 / 32 bytes";
            fields["Unwanted tracking protection frame"] = (ad.Data[2] == 0x41).ToString();
            fields["Hashed flags"] = ad.Data.Length == eidLength + 4 ? $"0x{ad.Data[^1]:X2} (requires r to authenticate)" : "Omitted";
        }
        return new("Google Find Hub / FHN", true, valid, fields,
            [valid ? "Matches FHN v1.3 service-data framing. No cryptographic identity validation has been performed." : "0xFEAA is also used by Eddystone; this is not a valid supported FHN frame.",
             "Reserved service data cannot be published by this Windows backend. Fast Pair provisioning, registration and network reporting are not implemented."]);
    }
}

public static class ProtocolResearch
{
    public static IReadOnlyList<ProtocolAnalysis> Analyze(string hex, string? address = null)
    {
        var ads = AdCodec.Decode(Hex.Parse(hex));
        return [AppleFindMyResearch.Analyze(ads, address), GoogleFindHubResearch.Analyze(ads)];
    }
    public static bool IsResearchPayload(AdvertisementProfile profile) => profile.Protocol != ResearchProtocol.Generic || Analyze(profile.PayloadHex).Any(x => x.Recognized);
    public static ObservationComparison Compare(string expectedHex, ExternalObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.Source) || string.IsNullOrWhiteSpace(observation.Address)) throw new FormatException("External capture requires a source and observed address.");
        if (observation.CapturedAt == default || observation.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new FormatException("External capture requires a valid capture time.");
        var expected = Hex.Parse(expectedHex); var actual = Hex.Parse(observation.PayloadHex);
        AdCodec.Decode(actual);
        var matches = expected.SequenceEqual(actual);
        return new(matches, $"User-imported external evidence ({observation.Source}, {observation.CapturedAt:O}). " +
            (matches ? "AD bytes match this input." : "AD bytes differ from this input; Windows may add or rearrange fields.") +
            " The application has not authenticated the capture provenance. This does not prove network reports or accessory compliance.", Hex.Format(expected), Hex.Format(actual));
    }
}
