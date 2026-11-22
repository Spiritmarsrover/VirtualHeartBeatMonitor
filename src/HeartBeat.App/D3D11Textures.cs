using System.Runtime.InteropServices;

namespace HeartBeat.App;

/// <summary>
/// Minimal Direct3D 11 access for overlay textures, with no extra packages: create a device,
/// create BGRA textures, and copy pixels into them. SteamVR overlays drawn from a D3D11 texture
/// update without flicker and without the size limit of raw uploads.
/// COM methods are called through their vtable slots (d3d11.h order) and checked at start-up.
/// </summary>
public sealed unsafe class D3D11Textures : IDisposable
{
    private const int DriverTypeHardware = 1;
    private const uint CreateBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint FormatB8G8R8A8Unorm = 87;
    private const uint BindShaderResource = 0x8;

    // vtable slots
    private const int Release = 2;
    private const int DeviceCreateTexture2D = 5;
    private const int Texture2DGetDesc = 10;
    private const int ContextUpdateSubresource = 48;
    private const int ContextFlush = 111;

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    private IntPtr _device, _context;

    private D3D11Textures(IntPtr device, IntPtr context) { _device = device; _context = context; }

    /// <summary>Creates a device on the default adapter, or returns null (with a reason) if that fails.</summary>
    public static D3D11Textures? TryCreate(out string? error)
    {
        error = null;
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateBgraSupport,
                IntPtr.Zero, 0, SdkVersion, out var device, out _, out var context);
            if (hr < 0) { error = $"D3D11CreateDevice failed (0x{hr:X8})"; return null; }
            var d3d = new D3D11Textures(device, context);

            // Self-check on throwaway textures before trusting the vtable layout: the texture has the
            // requested size, and pixels written with Upload read back unchanged.
            if (!d3d.SelfCheck())
            {
                d3d.Dispose();
                error = "Direct3D self-check failed";
                return null;
            }
            return d3d;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            error = $"Direct3D unavailable: {e.Message}";
            return null;
        }
    }

    /// <summary>A BGRA texture usable as an overlay texture. Release with <see cref="ReleaseTexture"/>.</summary>
    public IntPtr CreateTexture(int width, int height) => CreateTexture(width, height, UsageDefault, BindShaderResource, 0);

    private const uint UsageDefault = 0, UsageStaging = 3, CpuAccessRead = 0x20000, MapRead = 1;
    private const int ContextMap = 14, ContextUnmap = 15, ContextCopyResource = 47;

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource { public IntPtr Data; public uint RowPitch, DepthPitch; }

    private bool SelfCheck()
    {
        const int w = 16, h = 8;
        IntPtr texture = CreateTexture(w, h), staging = CreateTexture(w, h, UsageStaging, 0, CpuAccessRead);
        try
        {
            if (texture == IntPtr.Zero || staging == IntPtr.Zero || !HasSize(texture, w, h)) return false;
            var pixels = new uint[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 0xFF000000u | (uint)(i * 2654435761u >> 8);
            fixed (uint* p = pixels) Upload(texture, (IntPtr)p, w * 4);

            var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, ContextCopyResource);
            copy(_context, staging, texture);
            MappedSubresource mapped;
            var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(_context, ContextMap);
            if (map(_context, staging, 0, MapRead, 0, &mapped) < 0 || mapped.Data == IntPtr.Zero) return false;
            bool same = true;
            for (int y = 0; y < h && same; y++)
                for (int x = 0; x < w && same; x++)
                    same = ((uint*)(mapped.Data + y * (int)mapped.RowPitch))[x] == pixels[y * w + x];
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, ContextUnmap))(_context, staging, 0);
            return same;
        }
        finally
        {
            ReleaseTexture(texture);
            ReleaseTexture(staging);
        }
    }

    private IntPtr CreateTexture(int width, int height, uint usage, uint bindFlags, uint cpuAccess)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = FormatB8G8R8A8Unorm, SampleCount = 1, SampleQuality = 0,
            Usage = usage, BindFlags = bindFlags, CpuAccessFlags = cpuAccess, MiscFlags = 0,
        };
        IntPtr texture;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Slot(_device, DeviceCreateTexture2D);
        int hr = fn(_device, &desc, IntPtr.Zero, &texture);
        return hr < 0 ? IntPtr.Zero : texture;
    }

    /// <summary>Copies BGRA pixels (rows of width*4 bytes) into the texture.</summary>
    public void Upload(IntPtr texture, IntPtr bgra, int rowPitch)
    {
        var update = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, IntPtr, uint, uint, void>)Slot(_context, ContextUpdateSubresource);
        update(_context, texture, 0, IntPtr.Zero, bgra, (uint)rowPitch, 0);
        var flush = (delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(_context, ContextFlush);
        flush(_context);
    }

    public static void ReleaseTexture(IntPtr texture)
    {
        if (texture != IntPtr.Zero) ReleaseCom(texture);
    }

    private bool HasSize(IntPtr texture, int width, int height)
    {
        Texture2DDesc desc;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Slot(texture, Texture2DGetDesc);
        fn(texture, &desc);
        return desc.Width == width && desc.Height == height && desc.Format == FormatB8G8R8A8Unorm;
    }

    private static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    private static void ReleaseCom(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, Release))(com);

    public void Dispose()
    {
        if (_context != IntPtr.Zero) { ReleaseCom(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { ReleaseCom(_device); _device = IntPtr.Zero; }
    }
}
