using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace HeartBeat.App;

/// <summary>Draws the HUD (Windows): a heart in the status colour with the BPM inside.</summary>
public sealed class HudRenderer : IDisposable
{
    public const int Size = HudStatus.Size;

    private readonly Bitmap _bitmap = new(Size, Size, PixelFormat.Format32bppArgb);
    private readonly Font _two = new("Segoe UI", 40, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _three = new("Segoe UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);

    public Bitmap Draw(HudView view)
    {
        using var g = Graphics.FromImage(_bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var (r, gr, b) = HudStatus.Color(view.State);
        var pts = new PointF[72];
        float size = 54 * view.Pulse; // leaves room for the beat pulse (up to x1.18)
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length;
            double x = 16 * Math.Pow(Math.Sin(t), 3);
            double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            pts[i] = new PointF(64 + (float)(x / 17) * size, 68 - (float)((y + 2.5) / 17) * size);
        }
        using (var fill = new SolidBrush(Color.FromArgb(255, r, gr, b))) g.FillPolygon(fill, pts);

        var font = view.Text.Length >= 3 ? _three : _two;
        var box = new RectangleF(0, 30, Size, 50);
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            g.DrawString(view.Text, font, shadow, new RectangleF(box.X + 1.5f, box.Y + 2, box.Width, box.Height), center);
        g.DrawString(view.Text, font, Brushes.White, box, center);
        return _bitmap;
    }

    public void SavePng(string path) => _bitmap.Save(path, ImageFormat.Png);

    public void Dispose()
    {
        _bitmap.Dispose();
        _two.Dispose();
        _three.Dispose();
    }
}
