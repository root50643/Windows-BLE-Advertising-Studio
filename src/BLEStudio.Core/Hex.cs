using System.Globalization;
using System.Text;

namespace BLEStudio.Core;

public static class Hex
{
    public const int MaximumBytes = 65_536;
    public static byte[] Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        if (input.Length > MaximumBytes * 8) throw new FormatException("Hex input is too large (maximum 64 KiB).");
        var clean = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c) || c is ':' or '-' or ',') continue;
            if (c == '0' && i + 1 < input.Length && input[i + 1] is 'x' or 'X') { i++; continue; }
            if (!Uri.IsHexDigit(c)) throw new FormatException($"Invalid hex character '{c}' at character {i + 1}.");
            clean.Append(c);
        }
        if (clean.Length % 2 != 0) throw new FormatException("Hex input must contain complete byte pairs.");
        if (clean.Length / 2 > MaximumBytes) throw new FormatException("Hex input exceeds 64 KiB.");
        return Convert.FromHexString(clean.ToString());
    }
    public static string Format(ReadOnlySpan<byte> data) => string.Join(" ", data.ToArray().Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
    public static string Dump(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var row = data.Slice(offset, Math.Min(16, data.Length - offset));
            result.Append($"{offset:X4}  {Format(row),-47}  ");
            foreach (var b in row) result.Append(b is >= 32 and <= 126 ? (char)b : '.');
            result.AppendLine();
        }
        return result.ToString();
    }
}
