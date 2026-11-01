using System.Buffers.Binary;
using System.Text;

namespace BLEStudio.Core;

public sealed record AdStructure(byte Type, byte[] Data, int Offset = 0)
{
    public string TypeHex => $"0x{Type:X2}";
    public string HexData => Hex.Format(Data);
    public int Length => Data.Length;
    public string Description => AdCodec.Describe(this);
    public string WindowsSupport => WindowsPayloadPolicy.IsReserved(Type) ? "Unsupported: Windows reserved" : "Requires API / controller verification";
}

public static class AdCodec
{
    public static byte[] Encode(IEnumerable<AdStructure> structures)
    {
        using var stream = new MemoryStream();
        foreach (var ad in structures)
        {
            if (ad.Data.Length > 254) throw new FormatException("An AD structure has a maximum of 254 data bytes.");
            if (ad.Type == 0) throw new FormatException("AD type 0x00 is not valid.");
            stream.WriteByte((byte)(ad.Data.Length + 1));
            stream.WriteByte(ad.Type);
            stream.Write(ad.Data);
        }
        return stream.ToArray();
    }
    public static IReadOnlyList<AdStructure> Decode(ReadOnlySpan<byte> payload)
    {
        var result = new List<AdStructure>();
        for (var offset = 0; offset < payload.Length;)
        {
            var length = payload[offset];
            if (length == 0)
            {
                if (payload[offset..].IndexOfAnyExcept((byte)0) >= 0) throw new FormatException($"Nonzero data after AD terminator at offset {offset}.");
                break;
            }
            if (length + offset + 1 > payload.Length) throw new FormatException($"Truncated AD structure at offset {offset}: declares {length} bytes.");
            var type = payload[offset + 1];
            if (type == 0) throw new FormatException($"Invalid AD type 0x00 at offset {offset}.");
            result.Add(new(type, payload.Slice(offset + 2, length - 1).ToArray(), offset));
            offset += length + 1;
        }
        return result;
    }
    public static AdStructure Manufacturer(ushort company, byte[] data)
    {
        var buffer = new byte[data.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, company);
        data.CopyTo(buffer, 2);
        return new(0xFF, buffer);
    }
    public static byte[] UuidBytes(string uuid)
    {
        var value = uuid.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (value.Length == 4) { var data = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(data, Convert.ToUInt16(value, 16)); return data; }
        if (value.Length == 8) { var data = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, Convert.ToUInt32(value, 16)); return data; }
        var bytes = Convert.FromHexString(Guid.Parse(value).ToString("N"));
        Array.Reverse(bytes); // Bluetooth transmits the complete UUID least-significant octet first.
        return bytes;
    }
    public static AdStructure ServiceData(string uuid, byte[] data)
    {
        var id = UuidBytes(uuid);
        return new(id.Length switch { 2 => (byte)0x16, 4 => (byte)0x20, _ => (byte)0x21 }, [.. id, .. data]);
    }
    public static IEnumerable<string> ServiceUuids(AdStructure ad)
    {
        var width = ad.Type switch { 0x02 or 0x03 or 0x14 or 0x16 => 2, 0x04 or 0x05 or 0x1F or 0x20 => 4, 0x06 or 0x07 or 0x15 or 0x21 => 16, _ => 0 };
        if (width == 0) yield break;
        var count = ad.Type is 0x16 or 0x20 or 0x21 ? Math.Min(width, ad.Data.Length) : ad.Data.Length;
        for (var i = 0; i + width <= count; i += width)
        {
            var bytes = ad.Data.AsSpan(i, width).ToArray();
            Array.Reverse(bytes);
            var text = Convert.ToHexString(bytes);
            yield return width == 16 ? Guid.ParseExact(text, "N").ToString() : text;
        }
    }
    public static string Describe(AdStructure ad) => ad.Type switch
    {
        0x01 => "Flags",
        0x02 or 0x03 => "16-bit service UUID list",
        0x04 or 0x05 => "32-bit service UUID list",
        0x06 or 0x07 => "128-bit service UUID list",
        0x08 or 0x09 => $"Local name: {Encoding.UTF8.GetString(ad.Data)}",
        0x0A => "TX power",
        0x14 or 0x15 or 0x1F => "Service solicitation",
        0x16 or 0x20 or 0x21 => $"Service data ({string.Join(", ", ServiceUuids(ad))})",
        0x19 => "Appearance",
        0xFF when ad.Data.Length >= 2 => $"Manufacturer: {CompanyNames.Lookup(BinaryPrimitives.ReadUInt16LittleEndian(ad.Data))}",
        0xFF => "Malformed manufacturer data (missing company ID)",
        _ => "Custom AD structure"
    };
}

public static class CompanyNames
{
    // Deliberately small, identified subset. Unknown IDs remain numeric.
    public static string Lookup(ushort company) => company switch
    {
        0x004C => "Apple, Inc.", 0x0006 => "Microsoft", 0x00E0 => "Google", 0x0059 => "Nordic Semiconductor",
        0x0075 => "Samsung Electronics", 0xFFFF => "Reserved / test", _ => "Unknown company"
    };
}
