using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HeartBeat.App;

/// <summary>
/// The app's heart icon: a red heart with a white pulse line (the line is left out at very small
/// sizes). Used for the exe icon (pre-generated app.ico), the window, the tray and the SteamVR tab.
/// </summary>
public static class IconArt
{
    public static readonly Color Red = Color.FromArgb(255, 227, 72, 90);
    public static readonly Color Grey = Color.FromArgb(255, 128, 134, 142);

    public static Bitmap Render(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        var pts = new PointF[96];
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length;
            double x = 16 * Math.Pow(Math.Sin(t), 3);
            double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            // The curve spans x ±16, y -17..12; fit it into the square with a small margin.
            pts[i] = new PointF((float)(s / 2 + x / 34.5 * s), (float)(s * 0.47 - y / 34.5 * s));
        }
        using (var fill = new SolidBrush(color)) g.FillPolygon(fill, pts);

        if (size >= 24)
        {
            // A heartbeat trace across the heart.
            float w = Math.Max(1.5f, s / 14);
            using var pen = new Pen(Color.White, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            float y0 = s * 0.47f;
            g.DrawLines(pen, new[]
            {
                new PointF(s * 0.17f, y0), new PointF(s * 0.38f, y0), new PointF(s * 0.45f, y0 - s * 0.17f),
                new PointF(s * 0.53f, y0 + s * 0.15f), new PointF(s * 0.60f, y0), new PointF(s * 0.83f, y0),
            });
        }
        return bmp;
    }

    /// <summary>An Icon for the tray or window (caller disposes).</summary>
    public static Icon ToIcon(int size, Color color)
    {
        using var bmp = Render(size, color);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Writes a multi-size .ico (PNG entries, 16–256 px).</summary>
    public static void WriteIco(string path, Color color)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(sz =>
        {
            using var bmp = Render(sz, color);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var f = new BinaryWriter(File.Create(path));
        f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            f.Write((byte)0); f.Write((byte)0);
            f.Write((short)1); f.Write((short)32);
            f.Write(images[i].Length); f.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) f.Write(img);
    }
}
