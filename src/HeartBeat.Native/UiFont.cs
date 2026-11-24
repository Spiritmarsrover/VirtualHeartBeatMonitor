using System.Text;

namespace HeartBeat.Native;

/// <summary>One pre-rendered glyph: alpha coverage, placed relative to the pen position and line top.</summary>
public sealed record Glyph(int Width, int Height, int Left, int Top, float Advance, byte[] Alpha);

/// <summary>
/// A font size from the embedded glyph atlas (Assets/ui-font.bin, rendered from Noto Sans by
/// tools/FontAtlas). Text is positioned like System.Drawing: y is the top of the line box.
/// </summary>
public sealed class UiFont
{
    public string Name { get; }
    public float Size { get; }
    public float Ascent { get; }
    public float LineHeight { get; }
    private readonly Dictionary<int, Glyph> _glyphs;

    private UiFont(string name, float size, float ascent, float lineHeight, Dictionary<int, Glyph> glyphs)
    {
        Name = name;
        Size = size;
        Ascent = ascent;
        LineHeight = lineHeight;
        _glyphs = glyphs;
    }

    public Glyph? Get(char c) => _glyphs.GetValueOrDefault(c) ?? _glyphs.GetValueOrDefault('?');

    public float Measure(string text)
    {
        float w = 0;
        foreach (char c in text) w += Get(c)?.Advance ?? 0;
        return w;
    }

    private static readonly Lazy<Dictionary<string, UiFont>> All = new(Load);

    public static UiFont Bold72 => All.Value["bold72"];
    public static UiFont Bold44 => All.Value["bold44"];
    public static UiFont Bold22 => All.Value["bold22"];
    public static UiFont Regular19 => All.Value["regular19"];
    public static UiFont Regular16 => All.Value["regular16"];
    public static UiFont Regular13 => All.Value["regular13"];

    private static Dictionary<string, UiFont> Load()
    {
        using var stream = typeof(UiFont).Assembly.GetManifestResourceStream("ui-font.bin")
            ?? throw new InvalidOperationException("ui-font.bin is not embedded");
        using var r = new BinaryReader(stream, Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "HBFA" || r.ReadInt32() != 1)
            throw new InvalidDataException("ui-font.bin: unknown format");
        int count = r.ReadInt32();
        var fonts = new Dictionary<string, UiFont>();
        for (int f = 0; f < count; f++)
        {
            string name = r.ReadString();
            float size = r.ReadSingle(), ascent = r.ReadSingle(), line = r.ReadSingle();
            int glyphs = r.ReadInt32();
            var map = new Dictionary<int, Glyph>(glyphs);
            for (int i = 0; i < glyphs; i++)
            {
                int code = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32(), left = r.ReadInt32(), top = r.ReadInt32();
                float advance = r.ReadSingle();
                map[code] = new Glyph(w, h, left, top, advance, r.ReadBytes(w * h));
            }
            fonts[name] = new UiFont(name, size, ascent, line, map);
        }
        return fonts;
    }
}
