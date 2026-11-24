using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HeartBeat.EyePpg;

/// <summary>
/// One eye camera recorded as block means: each frame is cut into <see cref="Block"/>×<see cref="Block"/>
/// pixel blocks and only each block's mean brightness is kept (×64, as ushort). Averaging 64 pixels
/// keeps the tiny skin-brightness pulse while making the file ~30× smaller than the video.
///
/// File eye_&lt;n&gt;.bin: "EYEB", int version, width, height, block, cols, rows;
/// then per frame: double hostTime (s since recording start), int jpegBytes, ushort[cols*rows].
/// </summary>
public sealed class EyeFile
{
    public const int Block = 8;
    public const double Scale = 64.0;
    private static readonly byte[] Magic = "EYEB"u8.ToArray();

    public int Width, Height, Cols, Rows;
    public readonly List<double> Times = new();
    public readonly List<int> JpegBytes = new();
    public readonly List<ushort[]> Frames = new();

    public int BlockCount => Cols * Rows;

    public static EyeFile Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException($"{path} is not an eye file");
        int version = r.ReadInt32();
        if (version != 1) throw new InvalidDataException($"{path}: unsupported version {version}");
        var f = new EyeFile { Width = r.ReadInt32(), Height = r.ReadInt32() };
        int block = r.ReadInt32();
        if (block != Block) throw new InvalidDataException($"{path}: block size {block}");
        f.Cols = r.ReadInt32();
        f.Rows = r.ReadInt32();
        int n = f.BlockCount;
        long frameSize = 8 + 4 + 2L * n;
        while (r.BaseStream.Length - r.BaseStream.Position >= frameSize)
        {
            f.Times.Add(r.ReadDouble());
            f.JpegBytes.Add(r.ReadInt32());
            var blocks = new ushort[n];
            var bytes = r.ReadBytes(2 * n);
            Buffer.BlockCopy(bytes, 0, blocks, 0, bytes.Length);
            f.Frames.Add(blocks);
        }
        return f;
    }

    /// <summary>Appends frames to an eye file as they arrive.</summary>
    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _w;
        private bool _headerWritten;
        private readonly string _path;
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public long Frames { get; private set; }

        public Writer(string path)
        {
            _path = path;
            _w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
        }

        public void Write(double hostTime, int jpegBytes, in GreyFrame frame)
        {
            if (!Header(frame.Width, frame.Height)) return;
            _w.Write(hostTime);
            _w.Write(jpegBytes);
            var blocks = BlockMeans(frame, Cols, Rows);
            _w.Write(MemoryMarshal.AsBytes(blocks.AsSpan()));
            Frames++;
        }

        /// <summary>Block sums computed on the headset from the raw frame (jpegBytes = 0: no JPEG involved).</summary>
        public void WriteBlocks(double hostTime, int width, int height, ReadOnlySpan<ushort> sums)
        {
            if (!Header(width, height) || sums.Length != Cols * Rows) return;
            _w.Write(hostTime);
            _w.Write(0);
            _w.Write(MemoryMarshal.AsBytes(sums));
            Frames++;
        }

        private bool Header(int width, int height)
        {
            if (!_headerWritten)
            {
                Cols = width / Block;
                Rows = height / Block;
                _w.Write(Magic);
                _w.Write(1);
                _w.Write(width);
                _w.Write(height);
                _w.Write(Block);
                _w.Write(Cols);
                _w.Write(Rows);
                _headerWritten = true;
                return true;
            }
            // the stream changed size mid-recording; keep the file consistent
            return width / Block == Cols && height / Block == Rows;
        }

        public void Dispose() => _w.Dispose();
    }

    public static ushort[] BlockMeans(in GreyFrame f, int cols, int rows)
    {
        var sums = new int[cols * rows];
        for (int y = 0; y < rows * Block; y++)
        {
            int rowBase = (y / Block) * cols;
            var line = f.Pixels.AsSpan(y * f.Width, cols * Block);
            for (int c = 0; c < cols; c++)
            {
                int s = 0;
                var px = line.Slice(c * Block, Block);
                for (int i = 0; i < Block; i++) s += px[i];
                sums[rowBase + c] += s;
            }
        }
        var result = new ushort[sums.Length];
        // sum of 64 pixels == mean * 64, which is exactly the stored scale.
        for (int i = 0; i < sums.Length; i++) result[i] = (ushort)sums[i];
        return result;
    }
}

/// <summary>An 8-bit greyscale image.</summary>
public readonly record struct GreyFrame(int Width, int Height, byte[] Pixels)
{
    public static GreyFrame Decode(byte[] jpeg, int length)
    {
        using var ms = new MemoryStream(jpeg, 0, length, writable: false);
        using var bmp = new Bitmap(ms);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var pixels = new byte[w * h];
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < w; x++) pixels[y * w + x] = row[x * 3 + 1]; // green == grey for a grey image
            }
            return new GreyFrame(w, h, pixels);
        }
        finally { bmp.UnlockBits(data); }
    }
}
