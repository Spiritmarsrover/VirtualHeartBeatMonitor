using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Puts a rendered panel image onto a SteamVR overlay: as a Direct3D 11 texture when possible
/// (no flicker, no size limit), otherwise as raw pixels.
/// </summary>
public sealed class PanelPresenter : IDisposable
{
    private readonly string _name;
    private D3D11Textures? _d3d;
    private IntPtr _texture;
    private byte[]? _rgba;

    public string Mode { get; private set; }

    public PanelPresenter(string name, D3D11Textures? d3d, int width, int height)
    {
        _name = name;
        _d3d = d3d;
        if (d3d != null) _texture = d3d.CreateTexture(width, height);
        Mode = _texture != IntPtr.Zero ? "texture" : "raw";
        AppLog.Write($"{name} panel: {Mode} upload");
    }

    /// <param name="setTexture">Calls SetOverlayTexture for this panel.</param>
    /// <param name="setRaw">Uploads RGBA bytes instead (fallback).</param>
    public void Present(Bitmap bitmap, Func<IntPtr, EVROverlayError> setTexture, Action<byte[]> setRaw)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (_texture != IntPtr.Zero && _d3d != null)
            {
                _d3d.Upload(_texture, data.Scan0, data.Stride);
                var err = setTexture(_texture);
                if (err == EVROverlayError.None) return;
                AppLog.Write($"{_name} panel: texture refused ({err}), switching to raw upload");
                D3D11Textures.ReleaseTexture(_texture);
                _texture = IntPtr.Zero;
                Mode = "raw";
            }

            int bytes = bitmap.Width * bitmap.Height * 4;
            if (_rgba?.Length != bytes) _rgba = new byte[bytes];
            Marshal.Copy(data.Scan0, _rgba, 0, bytes);
            for (int i = 0; i < bytes; i += 4) (_rgba[i], _rgba[i + 2]) = (_rgba[i + 2], _rgba[i]); // BGRA -> RGBA
            setRaw(_rgba);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        D3D11Textures.ReleaseTexture(_texture);
        _texture = IntPtr.Zero;
        _d3d = null;
    }
}
