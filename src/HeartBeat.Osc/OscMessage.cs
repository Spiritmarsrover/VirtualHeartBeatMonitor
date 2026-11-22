using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace HeartBeat.Osc;

/// <summary>Minimal OSC 1.0 message encoder: int32, float32, string and bool (T/F) arguments.</summary>
public static class OscMessage
{
    public static byte[] Encode(string address, params object[] args)
    {
        var buffer = new List<byte>(64);
        WriteString(buffer, address);
        var tags = new StringBuilder(",");
        foreach (var a in args)
        {
            tags.Append(a switch
            {
                int => 'i',
                float or double => 'f',
                string => 's',
                bool b => b ? 'T' : 'F',
                _ => throw new ArgumentException($"Unsupported OSC argument type {a?.GetType().Name}"),
            });
        }
        WriteString(buffer, tags.ToString());
        Span<byte> four = stackalloc byte[4];
        foreach (var a in args)
        {
            switch (a)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(four, i);
                    buffer.AddRange(four.ToArray());
                    break;
                case float or double:
                    BinaryPrimitives.WriteSingleBigEndian(four, Convert.ToSingle(a));
                    buffer.AddRange(four.ToArray());
                    break;
                case string s:
                    WriteString(buffer, s);
                    break;
            }
        }
        return buffer.ToArray();
    }

    private static void WriteString(List<byte> buffer, string s)
    {
        buffer.AddRange(Encoding.UTF8.GetBytes(s));
        int pad = 4 - (Encoding.UTF8.GetByteCount(s) % 4); // always at least one terminating zero
        for (int i = 0; i < pad; i++) buffer.Add(0);
    }
}

/// <summary>Where OSC messages go. The UDP implementation sends to VRChat; tests use a fake.</summary>
public interface IOscSink
{
    void Send(string address, params object[] args);
}

public sealed class UdpOscSink : IOscSink, IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly object _gate = new();

    public UdpOscSink(string host, int port) => _udp.Connect(host, port);

    public void Send(string address, params object[] args)
    {
        var bytes = OscMessage.Encode(address, args);
        lock (_gate)
        {
            try { _udp.Send(bytes, bytes.Length); }
            catch (SocketException) { /* nobody listening (VRChat closed): ignore */ }
        }
    }

    public void Dispose() => _udp.Dispose();
}
