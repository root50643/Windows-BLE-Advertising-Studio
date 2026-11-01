using System.ComponentModel;

namespace BLEStudio.Core;

public sealed class ScanDevice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Address { get; init; } = "";
    public string AddressType { get; init; } = "";
    public string Name { get; private set; } = "";
    public short Rssi { get; private set; }
    public string AdvertisementType { get; private set; } = "";
    public string Manufacturers { get; private set; } = "";
    public string Services { get; private set; } = "";
    public string PayloadHex { get; private set; } = "";
    public DateTimeOffset FirstSeen { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public string FirstSeenDisplay => FirstSeen.ToLocalTime().ToString("HH:mm:ss");
    public string LastSeenDisplay => LastSeen.ToLocalTime().ToString("HH:mm:ss");
    public int Count { get; private set; }
    public bool IsMock { get; private set; }
    public List<short> RssiHistory { get; } = [];
    public void Update(ScanPacket packet)
    {
        if (Count == 0) { FirstSeen = packet.Timestamp; LastSeen = packet.Timestamp; }
        if (packet.Timestamp < FirstSeen) FirstSeen = packet.Timestamp;
        if (packet.Timestamp >= LastSeen)
        {
            LastSeen = packet.Timestamp;
            if (packet.Name.Length > 0) Name = packet.Name;
            Rssi = packet.Rssi; AdvertisementType = packet.AdvertisementType;
            PayloadHex = packet.PayloadHex; IsMock = packet.IsMock;
        }
        // Preserve service/manufacturer identity across separate scan responses.
        if (packet.Manufacturers.Length > 0) Manufacturers = packet.Manufacturers;
        if (packet.Services.Length > 0) Services = packet.Services;
        Count++; RssiHistory.Add(packet.Rssi);
        if (RssiHistory.Count > 120) RssiHistory.RemoveAt(0);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class ScanRepository(int maxDevices = 5000, int maxPackets = 20000)
{
    private readonly Dictionary<string, ScanDevice> _devices = [];
    private readonly Queue<ScanPacket> _packets = [];
    public IReadOnlyCollection<ScanDevice> Devices => _devices.Values;
    public IReadOnlyCollection<ScanPacket> Packets => _packets;
    public bool KeepDuplicates { get; set; }
    public void Add(ScanPacket packet)
    {
        // Decode before modifying aggregate state; corrupt imports cannot create half a device.
        _ = packet.Structures;
        var key = $"{packet.Address}/{packet.AddressType}";
        if (!_devices.TryGetValue(key, out var device))
        {
            if (_devices.Count >= maxDevices) _devices.Remove(_devices.MinBy(x => x.Value.LastSeen).Key);
            device = new ScanDevice { Address = packet.Address, AddressType = packet.AddressType };
            _devices.Add(key, device);
        }
        var changed = device.PayloadHex != packet.PayloadHex;
        device.Update(packet);
        if (KeepDuplicates || changed)
        {
            _packets.Enqueue(packet);
            while (_packets.Count > maxPackets) _packets.Dequeue();
        }
    }
    public void Clear() { _devices.Clear(); _packets.Clear(); }
}
