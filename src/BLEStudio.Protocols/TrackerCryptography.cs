using System.Buffers.Binary;
using System.Security.Cryptography;
using BLEStudio.Core;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Math;

namespace BLEStudio.Protocols;

public static class TrackerCryptography
{
    public static byte[] ApplePublicKey(byte[] privateKey)
    {
        var curve = SecNamedCurves.GetByName("secp224r1"); var d = new BigInteger(1, privateKey);
        if (privateKey.Length != 28 || d.SignValue == 0 || d.CompareTo(curve.N) >= 0) throw new FormatException("Invalid P-224 private key.");
        return curve.G.Multiply(d).Normalize().AffineXCoord.GetEncoded();
    }
    public static (byte[] PrivateKey, byte[] PublicKey) CreateAppleIdentity()
    {
        var curve = SecNamedCurves.GetByName("secp224r1"); byte[] key; BigInteger d;
        do { key = RandomNumberGenerator.GetBytes(28); d = new(1, key); } while (d.SignValue == 0 || d.CompareTo(curve.N) >= 0);
        return (key, curve.G.Multiply(d).Normalize().AffineXCoord.GetEncoded());
    }
    public static byte[] GoogleEid(ReadOnlySpan<byte> eik, uint beaconSeconds)
    {
        if (eik.Length != 32) throw new FormatException("Google EIK must be 32 bytes.");
        Span<byte> input = stackalloc byte[32]; input.Clear(); input[..11].Fill(0xFF); input[11] = input[27] = 10;
        BinaryPrimitives.WriteUInt32BigEndian(input[12..16], beaconSeconds & 0xFFFFFC00);
        BinaryPrimitives.WriteUInt32BigEndian(input[28..], beaconSeconds & 0xFFFFFC00);
        using var aes = Aes.Create(); aes.Key = eik.ToArray();
        var curve = SecNamedCurves.GetByName("secp160r1");
        var r = new BigInteger(1, aes.EncryptEcb(input, PaddingMode.None)).Mod(curve.N);
        if (r.SignValue == 0) throw new CryptographicException("Invalid zero EID scalar; change the identity key.");
        return curve.G.Multiply(r).Normalize().AffineXCoord.GetEncoded();
    }
    public static AdvertisementProfile BuildProfile(TrackerIdentity identity, byte[] secret, DateTimeOffset now, bool extended = false)
    {
        string payload;
        if (identity.Protocol == ResearchProtocol.AppleFindMy) payload = AppleFindMyResearch.Preview(Hex.Parse(identity.PublicKeyHex)).PayloadHex;
        else if (identity.Protocol == ResearchProtocol.GoogleFindHub)
        {
            var seconds = (now - identity.PairDate).TotalSeconds;
            if (seconds < 0 || seconds > uint.MaxValue) throw new FormatException("Invalid pairing date or system clock; EID rotation is paused.");
            payload = GoogleFindHubResearch.Preview(GoogleEid(secret, (uint)seconds)).PayloadHex;
        }
        else throw new FormatException("Select Apple or Google tracking protocol.");
        return new() { Id = identity.Id, Name = identity.Name, Protocol = identity.Protocol, PayloadHex = payload, DurationSeconds = 0, UseExtendedAdvertising = extended, Scannable = identity.Protocol == ResearchProtocol.GoogleFindHub ? true : null };
    }
    public static TrackingCapability VerifyPacket(TrackingCapability previous, TrackerIdentity identity, AdvertisementProfile profile, PacketEvidence evidence, DateTimeOffset sessionStarted, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(evidence.Source) || evidence.CapturedAt < sessionStarted || evidence.CapturedAt > now.AddMinutes(5)) throw new FormatException("Capture must identify its source and belong to this active session.");
        var address = Hex.Parse(evidence.Address);
        if (address.Length != 6 || string.IsNullOrWhiteSpace(evidence.AddressType) || string.IsNullOrWhiteSpace(evidence.AdvertisementType)) throw new FormatException("Capture requires observed address, address type and advertisement type.");
        if (evidence.IsScanResponse || evidence.AdvertisementType.Contains("ScanResponse", StringComparison.OrdinalIgnoreCase)) return previous with { PacketVerified = false, AddressCompatible = false, Detail = "Data is in Scan Response; primary advertisement has not been verified." };
        var expected = AdCodec.Decode(Hex.Parse(profile.PayloadHex)); var actual = AdCodec.Decode(Hex.Parse(evidence.PayloadHex));
        var matches = expected.All(e => actual.Any(a => a.Type == e.Type && a.Data.SequenceEqual(e.Data)));
        var legacy = evidence.AdvertisementType.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var addressMatch = identity.Protocol == ResearchProtocol.AppleFindMy && Hex.Parse(AppleFindMyResearch.Preview(Hex.Parse(identity.PublicKeyHex)).RequiredAddress!).SequenceEqual(address) && evidence.AddressType.Equals("RandomStatic", StringComparison.OrdinalIgnoreCase);
        return previous with { PacketVerified = matches && legacy, AddressCompatible = addressMatch, Detail = !matches ? "AD bytes, UUID width or flags differ from requested primary frame." : !legacy ? "Extended transport; legacy network compatibility unverified." : identity.Protocol == ResearchProtocol.AppleFindMy ? addressMatch ? "Imported capture matches key-derived static address; await a decrypted report." : "Apple public key/address mismatch; not network compatible." : "Imported FEAA primary frame matches. Windows manages address rotation; a real decrypted report is still required." };
    }
    public static TrackingCapability VerifyLocation(TrackingCapability capability, TrackerIdentity identity, LocationReport report, DateTimeOffset started, string? expectedIdentityHex = null)
    {
        var valid = report.IsValid && report.CryptographicallyVerified && report.TrackerId == identity.Id && report.ObservedAt >= started && report.Source == (identity.Protocol == ResearchProtocol.AppleFindMy ? "Apple Find My" : "Google Find Hub") &&
            (identity.Protocol != ResearchProtocol.GoogleFindHub || expectedIdentityHex != null && expectedIdentityHex.Equals(report.AdvertisedIdentityHex, StringComparison.OrdinalIgnoreCase));
        return capability with { LocationVerified = capability.LocationVerified || valid, AddressCompatible = capability.AddressCompatible || identity.Protocol == ResearchProtocol.GoogleFindHub && valid && capability.PacketVerified && capability.ApiAccepted, Detail = valid ? "Authenticated provider decrypted a report during this session. Packet/address gates still apply." : capability.Detail };
    }
}
