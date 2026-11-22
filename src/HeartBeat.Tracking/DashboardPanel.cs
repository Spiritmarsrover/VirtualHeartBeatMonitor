using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.Tracking;

public enum PanelMouse { Move, Down, Up }

public readonly record struct PanelMouseEvent(PanelMouse Kind, float X, float Y);

/// <summary>
/// A tab in the SteamVR dashboard showing an image the app draws, with laser-pointer mouse
/// input in image pixel coordinates (origin top-left).
/// </summary>
public sealed class DashboardPanel : IDisposable
{
    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();
    private readonly CVROverlay _overlay;
    private readonly ulong _main, _thumbnail;
    private bool _useFile;

    public int Width { get; }
    public int Height { get; }
    public string? LastError { get; private set; }

    public DashboardPanel(CVROverlay overlay, string key, string name, int width, int height, float widthMeters = 1.6f)
    {
        _overlay = overlay;
        Width = width;
        Height = height;
        ulong main = 0, thumb = 0;
        var err = overlay.CreateDashboardOverlay(key, name, ref main, ref thumb);
        if (err != EVROverlayError.None) throw new InvalidOperationException($"CreateDashboardOverlay failed: {err}");
        (_main, _thumbnail) = (main, thumb);
        overlay.SetOverlayWidthInMeters(_main, widthMeters);
        overlay.SetOverlayInputMethod(_main, VROverlayInputMethod.Mouse);
        var scale = new HmdVector2_t { v0 = width, v1 = height };
        overlay.SetOverlayMouseScale(_main, ref scale);
    }

    /// <summary>True while the user has this tab open in the dashboard.</summary>
    public bool Visible => _overlay.IsOverlayVisible(_main);

    public List<PanelMouseEvent> PollMouse()
    {
        var events = new List<PanelMouseEvent>();
        var ev = new VREvent_t();
        while (_overlay.PollNextOverlayEvent(_main, ref ev, EventSize))
        {
            var kind = (EVREventType)ev.eventType switch
            {
                EVREventType.VREvent_MouseMove => PanelMouse.Move,
                EVREventType.VREvent_MouseButtonDown => PanelMouse.Down,
                EVREventType.VREvent_MouseButtonUp => PanelMouse.Up,
                _ => (PanelMouse?)null,
            };
            // OpenVR mouse coordinates start at the bottom-left; flip to top-left.
            if (kind is { } k) events.Add(new PanelMouseEvent(k, ev.data.mouse.x, Height - ev.data.mouse.y));
        }
        return events;
    }

    /// <summary>
    /// Uploads an RGBA image. Large raw uploads can be refused; then it switches to loading a PNG
    /// written by <paramref name="writePng"/> (called with a temp path) from then on.
    /// </summary>
    public void SetImage(byte[] rgba, Action<string> writePng)
    {
        if (!_useFile)
        {
            var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                var err = _overlay.SetOverlayRaw(_main, pin.AddrOfPinnedObject(), (uint)Width, (uint)Height, 4);
                if (err == EVROverlayError.None) return;
                LastError = $"Raw upload refused ({err}), using image files";
                _useFile = true;
            }
            finally
            {
                pin.Free();
            }
        }
        string path = Path.Combine(Path.GetTempPath(), "virtualheartbeatmonitor_dashboard.png");
        writePng(path);
        var fileErr = _overlay.SetOverlayFromFile(_main, path);
        if (fileErr != EVROverlayError.None) LastError = $"Dashboard image: {fileErr}";
    }

    /// <summary>Shows a Direct3D 11 texture (BGRA). Returns the SteamVR result.</summary>
    public EVROverlayError SetTexture(IntPtr d3d11Texture)
    {
        var t = new Texture_t { handle = d3d11Texture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
        var err = _overlay.SetOverlayTexture(_main, ref t);
        if (err != EVROverlayError.None) LastError = $"SetOverlayTexture: {err}";
        return err;
    }

    public void SetThumbnail(string pngPath) => _overlay.SetOverlayFromFile(_thumbnail, pngPath);

    public void Dispose()
    {
        _overlay.DestroyOverlay(_main);
        _overlay.DestroyOverlay(_thumbnail);
    }
}
