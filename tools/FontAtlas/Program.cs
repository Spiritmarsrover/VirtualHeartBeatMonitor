using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

// Renders the panel fonts into src/HeartBeat.Native/Assets/ui-font.bin.
//
//   dotnet run --project tools/FontAtlas -- <NotoSans-Regular.ttf> <NotoSans-Bold.ttf> <output.bin>
//
// Format (little endian): "HBFA", int version 1, int fontCount; per font: string name, float size,
// float ascent, float lineHeight, int glyphCount; per glyph: int codepoint, int width, int height,
// int left, int top (both relative to the pen position / line top), float advance, byte[w*h] alpha.

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: FontAtlas <NotoSans-Regular.ttf> <NotoSans-Bold.ttf> <output.bin>");
    return 1;
}

// One collection per file: with both in one collection GDI+ kept using the Regular outlines for Bold.
using var regularFonts = new PrivateFontCollection();
regularFonts.AddFontFile(args[0]);
using var boldFonts = new PrivateFontCollection();
boldFonts.AddFontFile(args[1]);

// name, size in px, bold — the sizes the native panels use (see HeartBeat.Native/UiFont.cs).
(string name, float size, bool bold)[] wanted =
[
    ("bold72", 72, true), ("bold44", 44, true), ("bold22", 22, true),
    ("regular19", 19, false), ("regular16", 16, false), ("regular13", 13, false),
];
var chars = Enumerable.Range(32, 95).Select(c => (char)c).Concat("·°–…").ToArray();

using var outStream = File.Create(args[2]);
using var w = new BinaryWriter(outStream, Encoding.UTF8);
w.Write("HBFA"u8.ToArray());
w.Write(1);
w.Write(wanted.Length);

foreach (var (name, size, bold) in wanted)
{
    var family = (bold ? boldFonts : regularFonts).Families.Single();
    var style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
    using var font = new Font(family, size, style, GraphicsUnit.Pixel);
    float em = family.GetEmHeight(style);
    float ascent = family.GetCellAscent(style) * size / em;
    float line = family.GetLineSpacing(style) * size / em;
    w.Write(name);
    w.Write(size);
    w.Write(ascent);
    w.Write(line);
    w.Write(chars.Length);

    int pad = (int)size, canvas = (int)(size * 3);
    using var bmp = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
    long bytes = 0;
    foreach (char c in chars)
    {
        g.Clear(Color.Black);
        g.DrawString(c.ToString(), font, Brushes.White, pad, pad, fmt);
        float advance = g.MeasureString(c.ToString(), font, PointF.Empty, fmt).Width;

        var data = bmp.LockBits(new Rectangle(0, 0, canvas, canvas), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * canvas];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        int minX = canvas, minY = canvas, maxX = -1, maxY = -1;
        for (int y = 0; y < canvas; y++)
            for (int x = 0; x < canvas; x++)
                if (px[y * data.Stride + x * 4 + 2] > 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }

        w.Write((int)c);
        if (maxX < 0) { w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(advance); continue; }
        int gw = maxX - minX + 1, gh = maxY - minY + 1;
        w.Write(gw);
        w.Write(gh);
        w.Write(minX - pad);
        w.Write(minY - pad);
        w.Write(advance);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                w.Write(px[y * data.Stride + x * 4 + 2]);
        bytes += gw * gh;
    }
    Console.WriteLine($"{name}: {chars.Length} glyphs, ascent {ascent:0.0}, line {line:0.0}, {bytes / 1024.0:0} KB");
}
Console.WriteLine($"Wrote {args[2]} ({outStream.Length / 1024.0:0} KB)");
return 0;
