using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Controllarr.Core.Networking;

/// <summary>Immutable, merged ranges with logarithmic lookup; no per-peer parsing.</summary>
public sealed class IpBlocklist
{
    private readonly (UInt128 Start, UInt128 End)[] _ranges;
    public static IpBlocklist Empty { get; } = new(Array.Empty<string>());
    public int Count => _ranges.Length;

    public IpBlocklist(IEnumerable<string> lines)
    {
        var ranges = new List<(UInt128 Start, UInt128 End)>();
        foreach (string raw in lines)
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (ranges.Count >= 100000) throw new ArgumentException("Blocklists are limited to 100000 IP/CIDR entries.");
            var parts = line.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip))
                throw new ArgumentException($"Invalid blocklist IP/CIDR: {line}");
            bool ipv4 = ip.AddressFamily == AddressFamily.InterNetwork;
            int bits = ipv4 ? 32 : 128;
            int prefix = bits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits))
                throw new ArgumentException($"Invalid blocklist prefix: {line}");
            if (ipv4) prefix += 96;
            UInt128 mask = prefix == 0 ? 0 : UInt128.MaxValue << (128 - prefix);
            UInt128 start = Value(ip) & mask;
            ranges.Add((start, start | ~mask));
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(UInt128 Start, UInt128 End)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, UInt128.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        _ranges = merged.ToArray();
    }

    public static IpBlocklist Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 5 * 1024 * 1024)
            throw new ArgumentException("Choose an existing absolute IP/CIDR text file, at most 5 MiB.");
        return new(File.ReadLines(path));
    }

    public bool Contains(IPAddress ip)
    {
        UInt128 value = Value(ip);
        int low = 0, high = _ranges.Length - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            var range = _ranges[mid];
            if (value < range.Start) high = mid - 1;
            else if (value > range.End) low = mid + 1;
            else return true;
        }
        return false;
    }

    private static UInt128 Value(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        ip.MapToIPv6().TryWriteBytes(bytes, out _);
        return ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes) << 64) | BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }
}
