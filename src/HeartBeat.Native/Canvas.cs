using System.Drawing;
using System.IO.Compression;

namespace HeartBeat.Native;

/// <summary>An RGBA colour (straight alpha).</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public static Rgba Argb(int a, int r, int g, int b) => new((byte)r, (byte)g, (byte)b, (byte)a);
    public Rgba WithAlpha(int a) => this with { A = (byte)a };
}

/// <summary>
/// A tiny software renderer for the in-VR panels on Linux (no System.Drawing there): RGBA pixels,
/// straight alpha, anti-aliased rounded boxes, circles, polygons and polylines, atlas text.
/// The pixel layout is what SetOverlayRaw expects (RGBA, rows top to bottom).
/// </summary>
public sealed class Canvas
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    private int _cx0, _cy0, _cx1, _cy1; // clip, pixels [x0, x1) x [y0, y1)
    private float[] _layer = [];

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        ResetClip();
    }

    public void SetClip(RectangleF r)
    {
        _cx0 = Math.Clamp((int)Math.Floor(r.X), 0, Width);
        _cy0 = Math.Clamp((int)Math.Floor(r.Y), 0, Height);
        _cx1 = Math.Clamp((int)Math.Ceiling(r.Right), 0, Width);
        _cy1 = Math.Clamp((int)Math.Ceiling(r.Bottom), 0, Height);
    }

    public void ResetClip() { _cx0 = 0; _cy0 = 0; _cx1 = Width; _cy1 = Height; }

    public void Clear(Rgba c)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = c.R; Pixels[i + 1] = c.G; Pixels[i + 2] = c.B; Pixels[i + 3] = c.A;
        }
    }

    /// <summary>Source-over blend of colour c at the given coverage (0..1).</summary>
    private void Blend(int x, int y, Rgba c, float coverage)
    {
        if (coverage <= 0 || x < _cx0 || x >= _cx1 || y < _cy0 || y >= _cy1) return;
        float a = c.A / 255f * Math.Min(coverage, 1f);
        if (a <= 0) return;
        int i = (y * Width + x) * 4;
        float da = Pixels[i + 3] / 255f;
        float oa = a + da * (1 - a);
        if (oa <= 0) return;
        float k = da * (1 - a);
        Pixels[i] = (byte)((c.R * a + Pixels[i] * k) / oa + 0.5f);
        Pixels[i + 1] = (byte)((c.G * a + Pixels[i + 1] * k) / oa + 0.5f);
        Pixels[i + 2] = (byte)((c.B * a + Pixels[i + 2] * k) / oa + 0.5f);
        Pixels[i + 3] = (byte)(oa * 255 + 0.5f);
    }

    public void FillRect(RectangleF r, Rgba c) => FillRoundedRect(r, 0, c);

    /// <summary>Rounded rectangle via its signed distance (radius 0 = sharp corners), anti-aliased.</summary>
    public void FillRoundedRect(RectangleF r, float radius, Rgba c)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, hx = r.Width / 2 - radius, hy = r.Height / 2 - radius;
        int x0 = (int)Math.Floor(r.X), x1 = (int)Math.Ceiling(r.Right), y0 = (int)Math.Floor(r.Y), y1 = (int)Math.Ceiling(r.Bottom);
        for (int y = Math.Max(y0, _cy0); y < Math.Min(y1, _cy1); y++)
            for (int x = Math.Max(x0, _cx0); x < Math.Min(x1, _cx1); x++)
                Blend(x, y, c, Coverage(RoundedBoxDistance(x + 0.5f - cx, y + 0.5f - cy, hx, hy, radius)));
    }

    /// <summary>Outline of a rounded rectangle, centred on its edge.</summary>
    public void StrokeRoundedRect(RectangleF r, float radius, float width, Rgba c)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, hx = r.Width / 2 - radius, hy = r.Height / 2 - radius;
        int x0 = (int)Math.Floor(r.X - width), x1 = (int)Math.Ceiling(r.Right + width), y0 = (int)Math.Floor(r.Y - width), y1 = (int)Math.Ceiling(r.Bottom + width);
        for (int y = Math.Max(y0, _cy0); y < Math.Min(y1, _cy1); y++)
            for (int x = Math.Max(x0, _cx0); x < Math.Min(x1, _cx1); x++)
                Blend(x, y, c, Coverage(Math.Abs(RoundedBoxDistance(x + 0.5f - cx, y + 0.5f - cy, hx, hy, radius)) - width / 2));
    }

    public void FillCircle(float cx, float cy, float radius, Rgba c)
    {
        int x0 = (int)Math.Floor(cx - radius - 1), x1 = (int)Math.Ceiling(cx + radius + 1);
        int y0 = (int)Math.Floor(cy - radius - 1), y1 = (int)Math.Ceiling(cy + radius + 1);
        for (int y = Math.Max(y0, _cy0); y < Math.Min(y1, _cy1); y++)
            for (int x = Math.Max(x0, _cx0); x < Math.Min(x1, _cx1); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                Blend(x, y, c, Coverage(MathF.Sqrt(dx * dx + dy * dy) - radius));
            }
    }

    /// <summary>Filled polygon (even-odd), anti-aliased with 4 sub-scanlines and exact horizontal coverage.</summary>
    public void FillPolygon(IReadOnlyList<PointF> pts, Rgba c)
    {
        if (pts.Count < 3) return;
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        int x0 = Math.Max((int)Math.Floor(minX), _cx0), x1 = Math.Min((int)Math.Ceiling(maxX) + 1, _cx1);
        int y0 = Math.Max((int)Math.Floor(minY), _cy0), y1 = Math.Min((int)Math.Ceiling(maxY) + 1, _cy1);
        if (x1 <= x0 || y1 <= y0) return;
        var cov = new float[x1 - x0];
        var xs = new List<float>(16);
        const int Sub = 4;
        for (int y = y0; y < y1; y++)
        {
            Array.Clear(cov);
            for (int s = 0; s < Sub; s++)
            {
                float sy = y + (s + 0.5f) / Sub;
                xs.Clear();
                for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                {
                    var a = pts[i]; var b = pts[j];
                    if ((a.Y <= sy) == (b.Y <= sy)) continue;
                    xs.Add(a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    float xa = Math.Max(xs[k], x0), xb = Math.Min(xs[k + 1], x1);
                    for (int px = (int)Math.Floor(xa); px < xb && px < x1; px++)
                    {
                        float overlap = Math.Min(xb, px + 1) - Math.Max(xa, px);
                        if (overlap > 0) cov[px - x0] += overlap / Sub;
                    }
                }
            }
            for (int x = x0; x < x1; x++) Blend(x, y, c, cov[x - x0]);
        }
    }

    /// <summary>Anti-aliased polyline with round joins (coverage taken as the max over segments, so joins don't darken).</summary>
    public void StrokePolyline(IReadOnlyList<PointF> pts, float width, Rgba c)
    {
        if (pts.Count < 2) return;
        float half = width / 2;
        int x0 = Math.Max((int)Math.Floor(pts.Min(p => p.X) - half - 1), _cx0), x1 = Math.Min((int)Math.Ceiling(pts.Max(p => p.X) + half + 1), _cx1);
        int y0 = Math.Max((int)Math.Floor(pts.Min(p => p.Y) - half - 1), _cy0), y1 = Math.Min((int)Math.Ceiling(pts.Max(p => p.Y) + half + 1), _cy1);
        if (x1 <= x0 || y1 <= y0) return;
        int w = x1 - x0, n = w * (y1 - y0);
        if (_layer.Length < n) _layer = new float[n];
        Array.Clear(_layer, 0, n);
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            int sx0 = Math.Max((int)Math.Floor(Math.Min(a.X, b.X) - half - 1), x0), sx1 = Math.Min((int)Math.Ceiling(Math.Max(a.X, b.X) + half + 1), x1);
            int sy0 = Math.Max((int)Math.Floor(Math.Min(a.Y, b.Y) - half - 1), y0), sy1 = Math.Min((int)Math.Ceiling(Math.Max(a.Y, b.Y) + half + 1), y1);
            float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            for (int y = sy0; y < sy1; y++)
                for (int x = sx0; x < sx1; x++)
                {
                    float px = x + 0.5f - a.X, py = y + 0.5f - a.Y;
                    float t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
                    float ex = px - t * dx, ey = py - t * dy;
                    float cov = Coverage(MathF.Sqrt(ex * ex + ey * ey) - half);
                    ref float l = ref _layer[(y - y0) * w + (x - x0)];
                    if (cov > l) l = cov;
                }
        }
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) Blend(x, y, c, _layer[(y - y0) * w + (x - x0)]);
    }

    /// <summary>Draws text with y at the top of the line box (like System.Drawing). Returns the width.</summary>
    public float DrawText(string text, UiFont font, Rgba c, float x, float y)
    {
        float pen = x;
        int top = (int)MathF.Round(y);
        foreach (char ch in text)
        {
            var g = font.Get(ch);
            if (g == null) continue;
            int gx = (int)MathF.Round(pen) + g.Left, gy = top + g.Top;
            for (int row = 0; row < g.Height; row++)
                for (int col = 0; col < g.Width; col++)
                {
                    byte a = g.Alpha[row * g.Width + col];
                    if (a != 0) Blend(gx + col, gy + row, c, a / 255f);
                }
            pen += g.Advance;
        }
        return pen - x;
    }

    public void DrawTextCentered(string text, UiFont font, Rgba c, RectangleF box) =>
        DrawText(text, font, c, box.X + (box.Width - font.Measure(text)) / 2, box.Y + (box.Height - font.LineHeight) / 2);

    /// <summary>Right-aligned on x, vertically centred on y.</summary>
    public void DrawTextRight(string text, UiFont font, Rgba c, float right, float centerY) =>
        DrawText(text, font, c, right - font.Measure(text), centerY - font.LineHeight / 2);

    private static float Coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);

    private static float RoundedBoxDistance(float px, float py, float hx, float hy, float r)
    {
        float qx = Math.Abs(px) - hx, qy = Math.Abs(py) - hy;
        float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return MathF.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    // ---- PNG (dashboard icon, development dumps) ------------------------------------------------

    public void SavePng(string path) => File.WriteAllBytes(path, EncodePng(Pixels, Width, Height));

    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(rgba, y * width * 4, width * 4);
                }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBE(head, 0, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc32(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        WriteBE(tail, 0, (int)crc);
        s.Write(tail);
    }

    private static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
