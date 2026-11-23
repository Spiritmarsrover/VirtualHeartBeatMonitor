using System.Text;

namespace HeartBeat.EyePpg;

/// <summary>
/// Reads a multipart/x-mixed-replace MJPEG stream (as served by FrameEyeCameraFeed on the headset):
/// each part has headers incl. Content-Length, a blank line, then the JPEG bytes.
/// </summary>
public static class Mjpeg
{
    public delegate void FrameHandler(byte[] jpeg, int length, double hostTime);

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Reads frames until cancelled or the connection drops.</summary>
    public static async Task ReadAsync(string url, Func<double> clock, FrameHandler onFrame, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var reader = new BufferedReader(stream);
        var buffer = new byte[1 << 20];
        while (!ct.IsCancellationRequested)
        {
            int length = -1;
            // Headers: skip boundary / blank lines until a Content-Length, then up to the blank line.
            while (true)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) return;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.AsSpan(15).Trim());
                else if (line.Length == 0 && length >= 0) break;
            }
            if (length > buffer.Length) buffer = new byte[length];
            if (!await reader.ReadExactlyAsync(buffer, length, ct)) return;
            onFrame(buffer, length, clock());
        }
    }

    private sealed class BufferedReader(Stream stream)
    {
        private readonly byte[] _buf = new byte[1 << 16];
        private int _pos, _len;

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _pos = 0;
            _len = await stream.ReadAsync(_buf, ct);
            return _len > 0;
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _len && !await FillAsync(ct)) return sb.Length > 0 ? sb.ToString() : null;
                byte b = _buf[_pos++];
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                if (sb.Length < 1024) sb.Append((char)b);
            }
        }

        public async Task<bool> ReadExactlyAsync(byte[] into, int count, CancellationToken ct)
        {
            int got = 0;
            while (got < count)
            {
                if (_pos >= _len && !await FillAsync(ct)) return false;
                int n = Math.Min(count - got, _len - _pos);
                Buffer.BlockCopy(_buf, _pos, into, got, n);
                _pos += n;
                got += n;
            }
            return true;
        }
    }
}
