using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>
/// A small overlay fixed to the headset view (lower left, 1 m away). Burn-in protection for OLED
/// screens: it drifts slowly by about ±1 cm in a pattern that takes minutes to repeat, and is drawn
/// at 80 % opacity, so no pixel shows the same thing for long.
/// </summary>
public sealed class HudOverlay : IDisposable
{
    private readonly CVROverlay _overlay;
    private readonly ulong _handle;
    private bool _shown;
    private double _nextPlacement;

    // Lower left of the view, 1 m ahead; the drift is added to this.
    private const float BaseX = -0.30f, BaseY = -0.24f, Distance = 1.0f;
    private const float DriftX = 0.012f, DriftY = 0.010f;

    public string? LastError { get; private set; }
    public bool Shown => _shown;

    public HudOverlay(CVROverlay overlay, string key, string name, float widthMeters = 0.06f)
    {
        _overlay = overlay;
        ulong handle = 0;
        var err = overlay.CreateOverlay(key, name, ref handle);
        if (err == EVROverlayError.KeyInUse)
        {
            // SteamVR can keep the key from a previous run for a moment.
            overlay.FindOverlay(key, ref handle);
            overlay.DestroyOverlay(handle);
            err = overlay.CreateOverlay(key, name, ref handle);
        }
        if (err != EVROverlayError.None) throw new InvalidOperationException($"CreateOverlay failed: {err}");
        _handle = handle;
        overlay.SetOverlayWidthInMeters(_handle, widthMeters);
        overlay.SetOverlayAlpha(_handle, 0.8f);
        overlay.SetOverlaySortOrder(_handle, 10);
    }

    public void Show(bool show)
    {
        if (show == _shown) return;
        var err = show ? _overlay.ShowOverlay(_handle) : _overlay.HideOverlay(_handle);
        LastError = err == EVROverlayError.None ? null : $"{(show ? "ShowOverlay" : "HideOverlay")}: {err}";
        _shown = show;
        _nextPlacement = 0;
    }

    /// <summary>Places the HUD relative to the headset, with the slow anti-burn-in drift. Call regularly.</summary>
    public void UpdatePlacement(double time)
    {
        if (!_shown || time < _nextPlacement) return;
        _nextPlacement = time + 0.5;
        float dx = DriftX * (float)Math.Sin(2 * Math.PI * time / 313);
        float dy = DriftY * (float)Math.Sin(2 * Math.PI * time / 197 + 1.3);
        var m = new HmdMatrix34_t
        {
            m0 = 1, m5 = 1, m10 = 1,
            m3 = BaseX + dx, m7 = BaseY + dy, m11 = -Distance,
        };
        var err = _overlay.SetOverlayTransformTrackedDeviceRelative(_handle, Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd, ref m);
        if (err != EVROverlayError.None) LastError = $"SetOverlayTransformTrackedDeviceRelative: {err}";
    }

    public EVROverlayError SetTexture(IntPtr d3d11Texture) =>
        SetTexture(new Texture_t { handle = d3d11Texture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto });

    public EVROverlayError SetTexture(Texture_t t)
    {
        var err = _overlay.SetOverlayTexture(_handle, ref t);
        LastError = err == EVROverlayError.None ? null : $"SetOverlayTexture: {err}";
        return err;
    }

    /// <summary>Uploads an RGBA image (4 bytes per pixel, row-major).</summary>
    public void SetImage(byte[] rgba, int width, int height)
    {
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var err = _overlay.SetOverlayRaw(_handle, pin.AddrOfPinnedObject(), (uint)width, (uint)height, 4);
            LastError = err == EVROverlayError.None ? null : $"SetOverlayRaw: {err}";
        }
        finally { pin.Free(); }
    }

    public void Dispose() => _overlay.DestroyOverlay(_handle);
}
