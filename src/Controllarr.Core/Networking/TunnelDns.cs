using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Controllarr.Core.Networking;

internal static class TunnelDns
{
    public static async Task<IPAddress[]> ResolveAsync(TorrentNetworkPolicy policy, IPAddress server, string host, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ushort id = (ushort)RandomNumberGenerator.GetInt32(65536);
        using var packet = new MemoryStream();
        packet.Write(new byte[] { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (string label in new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is < 1 or > 63) throw new IOException("Invalid DNS name.");
            packet.WriteByte((byte)bytes.Length); packet.Write(bytes);
        }
        packet.Write(new byte[] { 0, 0, 1, 0, 1 }); // A / IN; protected mode intentionally excludes IPv6.
        if (packet.Length > 271) throw new IOException("DNS name is too long.");
        using var socket = policy.CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(server, 53), timeout.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        byte[] request = packet.ToArray();
        await stream.WriteAsync(new byte[] { (byte)(request.Length >> 8), (byte)request.Length }, timeout.Token);
        await stream.WriteAsync(request, timeout.Token);
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, timeout.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(response, timeout.Token);
        return Parse(response, id);
    }

    internal static IPAddress[] Parse(byte[] response, ushort id)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != id || (response[2] & 0xF8) != 0x80 || (response[3] & 15) != 0)
            throw new IOException("Invalid or unsuccessful VPN DNS response.");
        int questions = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4));
        int answers = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
        if (questions != 1 || answers > 256) throw new IOException("Invalid VPN DNS record count.");
        int offset = SkipName(response, 12);
        offset += 4;
        var addresses = new List<IPAddress>();
        for (int i = 0; i < answers; i++)
        {
            offset = SkipName(response, offset);
            if (offset + 10 > response.Length) throw new IOException("Truncated DNS record.");
            int type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
            int cls = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2));
            int size = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8));
            offset += 10;
            if (offset + size > response.Length) throw new IOException("Truncated DNS address.");
            if (type == 1 && cls == 1 && size == 4) addresses.Add(new IPAddress(response.AsSpan(offset, 4)));
            offset += size;
        }
        if (addresses.Count == 0) throw new IOException("VPN DNS returned no IPv4 addresses.");
        return addresses.Distinct().ToArray();
    }

    private static int SkipName(byte[] bytes, int offset)
    {
        for (int count = 0; count < 128 && offset < bytes.Length; count++)
        {
            int size = bytes[offset++];
            if (size == 0) return offset;
            if ((size & 0xC0) == 0xC0 && offset < bytes.Length) return offset + 1;
            if (size > 63 || offset + size > bytes.Length) break;
            offset += size;
        }
        throw new IOException("Malformed DNS name.");
    }
}
