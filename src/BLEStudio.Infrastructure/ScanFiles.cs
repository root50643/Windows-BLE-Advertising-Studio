using System.Globalization;
using System.Text;
using System.Text.Json;
using BLEStudio.Core;

namespace BLEStudio.Infrastructure;

public sealed record ScanCapture(int SchemaVersion, string Source, DateTimeOffset SavedAt, IReadOnlyList<ScanPacket> Packets);
public static class ScanFiles
{
    public static string ToJson(IEnumerable<ScanPacket> packets, string source) => JsonSerializer.Serialize(new ScanCapture(1, source, DateTimeOffset.UtcNow, packets.ToArray()), ProfileJson.Options);
    public static IReadOnlyList<ScanPacket> FromJson(string json)
    {
        var capture = JsonSerializer.Deserialize<ScanCapture>(json, ProfileJson.Options) ?? throw new FormatException("Capture is empty.");
        if (capture.SchemaVersion != 1 || capture.Packets == null || capture.Packets.Count > 20000) throw new FormatException("Unsupported or oversized capture (maximum 20,000 packets).");
        foreach (var packet in capture.Packets)
        {
            if (packet == null || string.IsNullOrWhiteSpace(packet.Address) || packet.PayloadHex == null || packet.Name == null) throw new FormatException("Capture contains an invalid record.");
            _ = packet.Structures;
        }
        return capture.Packets;
    }
    public static string ToCsv(IEnumerable<ScanDevice> devices)
    {
        var output = new StringBuilder("Address,AddressType,Name,RSSI,Manufacturer,Services,AdvertisementType,FirstSeen,LastSeen,Count,PayloadHex,IsMock\r\n");
        foreach (var d in devices)
            output.AppendLine(string.Join(",", new[] { d.Address, d.AddressType, d.Name, d.Rssi.ToString(CultureInfo.InvariantCulture), d.Manufacturers, d.Services, d.AdvertisementType, d.FirstSeen.ToString("O"), d.LastSeen.ToString("O"), d.Count.ToString(CultureInfo.InvariantCulture), d.PayloadHex, d.IsMock.ToString() }.Select(Csv)));
        return output.ToString();
    }
    private static string Csv(string value)
    {
        // Prevent spreadsheet formula execution in untrusted advertised names.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
