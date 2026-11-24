using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.Tracking;

public enum OverlayHand { Off, Left, Right }

/// <summary>
/// A small in-VR panel attached to a controller like a watch. Its placement is a transform
/// relative to the controller, stored for the left hand and mirrored for the right hand.
/// </summary>
public sealed class WristOverlay : IDisposable
{
    private readonly CVROverlay _overlay;
    private readonly CVRSystem _system;
    private readonly ulong _handle;
    private uint _attachedTo = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;
    private OverlayHand _hand = OverlayHand.Off;
    private bool _visible;
    private double _missingSince = double.NaN;

    public string? LastError { get; private set; }

    /// <summary>Panel width in metres.</summary>
    public float WidthMeters { get; set; } = 0.09f;

    /// <summary>Placement relative to the left controller (mirrored when worn on the right).</summary>
    public HmdMatrix34_t Placement { get; set; } = Rigid.FromTiltAndOffset(-45f, 0f, 0.02f, 0.12f);

    /// <summary>The controller the panel is attached to, or invalid.</summary>
    public uint AttachedDevice => _attachedTo;
    public OverlayHand Hand => _hand;

    public WristOverlay(CVROverlay overlay, CVRSystem system, string key, string name)
    {
        _overlay = overlay;
        _system = system;
        ulong handle = 0;
        var err = overlay.CreateOverlay(key, name, ref handle);
        if (err != EVROverlayError.None) throw new InvalidOperationException($"CreateOverlay failed: {err}");
        _handle = handle;
        overlay.SetOverlayWidthInMeters(_handle, WidthMeters);
    }

    /// <summary>Attaches to the chosen hand (re-attaching if that controller changed) or hides the panel.</summary>
    public void Update(OverlayHand hand)
    {
        if (hand == OverlayHand.Off)
        {
            if (_visible) _overlay.HideOverlay(_handle);
            _visible = false;
            _hand = hand;
            return;
        }

        uint device = _system.GetTrackedDeviceIndexForControllerRole(
            hand == OverlayHand.Left ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
        if (device == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            // Controllers can drop out for a moment; only hide after a few seconds so the panel doesn't blink.
            if (double.IsNaN(_missingSince)) _missingSince = HostClock.Now;
            if (_visible && HostClock.Now - _missingSince > 3)
            {
                _overlay.HideOverlay(_handle);
                _visible = false;
            }
            return;
        }
        _missingSince = double.NaN;

        if (device != _attachedTo || hand != _hand)
        {
            _attachedTo = device;
            _hand = hand;
            _overlay.SetOverlayWidthInMeters(_handle, WidthMeters);
            ApplyRelative(PlacementForHand());
        }
        if (!_visible) _overlay.ShowOverlay(_handle);
        _visible = true;
    }

    /// <summary>Placement for the current hand (the stored left-hand placement, mirrored on the right).</summary>
    public HmdMatrix34_t PlacementForHand() => _hand == OverlayHand.Right ? Rigid.MirrorX(Placement) : Placement;

    /// <summary>Sets the placement from a transform relative to the current hand (e.g. while being moved).</summary>
    public void SetPlacementForHand(in HmdMatrix34_t relative)
    {
        Placement = _hand == OverlayHand.Right ? Rigid.MirrorX(relative) : relative;
        ApplyRelative(relative);
    }

    private void ApplyRelative(HmdMatrix34_t relative)
    {
        if (_attachedTo == Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid) return;
        var err = _overlay.SetOverlayTransformTrackedDeviceRelative(_handle, _attachedTo, ref relative);
        LastError = err == EVROverlayError.None ? null : err.ToString();
    }

    /// <summary>Re-applies size and placement on the next <see cref="Update"/> (after settings changed).</summary>
    public void Invalidate() => _attachedTo = Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid;

    public bool Visible => _visible;

    /// <summary>Shows a Direct3D 11 texture (BGRA). Returns the SteamVR result.</summary>
    public EVROverlayError SetTexture(IntPtr d3d11Texture) =>
        SetTexture(new Texture_t { handle = d3d11Texture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto });

    /// <summary>Any texture type (Vulkan on the Steam Frame).</summary>
    public EVROverlayError SetTexture(Texture_t t)
    {
        var err = _overlay.SetOverlayTexture(_handle, ref t);
        LastError = err == EVROverlayError.None ? null : $"SetOverlayTexture: {err}";
        return err;
    }

    /// <summary>Uploads a new image (RGBA, 4 bytes per pixel, row-major).</summary>
    public void SetImage(byte[] rgba, int width, int height)
    {
        if (!_visible) return;
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var err = _overlay.SetOverlayRaw(_handle, pin.AddrOfPinnedObject(), (uint)width, (uint)height, 4);
            LastError = err == EVROverlayError.None ? null : $"SetOverlayRaw: {err}";
        }
        finally
        {
            pin.Free();
        }
    }

    public void Dispose() => _overlay.DestroyOverlay(_handle);
}
