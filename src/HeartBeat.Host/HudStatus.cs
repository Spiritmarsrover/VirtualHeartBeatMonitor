using HeartBeat.Tracking;

namespace HeartBeat.App;

/// <summary>What the HUD heart's colour says.</summary>
public enum HudState
{
    /// <summary>A number is shown with good confidence: red.</summary>
    Confident,
    /// <summary>A number is shown, but confidence is low: orange.</summary>
    Unsure,
    /// <summary>No number yet / lost it while still: grey ("measuring, stay still").</summary>
    Measuring,
    /// <summary>The user is moving: blue-grey.</summary>
    Moving,
    /// <summary>No devices sending data, or no SteamVR session: dark.</summary>
    NoData,
}

/// <summary>The HUD's content, shared by the Windows and Steam Frame drawers.</summary>
public readonly record struct HudView(HudState State, string Text, float Pulse)
{
    /// <summary>Redraw when this changes (the pulse animation is drawn on top of it).</summary>
    public string Key => $"{State}|{Text}";
}

public static class HudStatus
{
    /// <summary>Confidence below this shows the number in orange (unsure).</summary>
    public const double UnsureBelow = 0.8;

    public static HudView For(LiveSession? session)
    {
        var snap = session?.LatestSnapshot;
        if (session == null || snap == null || (snap.Devices.Count > 0 && snap.Devices.All(d => !d.Receiving)))
            return new HudView(HudState.NoData, "--", 1);
        var heart = snap.Heart;
        if (heart.Displayed)
        {
            // Heart pops up on each beat and settles back within about 0.3 s, as on the panels.
            double sinceBeat = HostClock.Now - session.LastBeatHostTime;
            float pulse = (float)(1 + 0.18 * Math.Exp(-Math.Max(0, sinceBeat) / 0.11));
            return new HudView(heart.Confidence < UnsureBelow ? HudState.Unsure : HudState.Confident, heart.DisplayText, pulse);
        }
        return new HudView(snap.UserMoving ? HudState.Moving : HudState.Measuring, "--", 1);
    }

    /// <summary>Heart colour (RGB) per state.</summary>
    public static (byte R, byte G, byte B) Color(HudState state) => state switch
    {
        HudState.Confident => (232, 72, 85),
        HudState.Unsure => (240, 150, 60),
        HudState.Measuring => (130, 138, 148),
        HudState.Moving => (88, 118, 168),
        _ => (64, 68, 76),
    };

    public const int Size = 128;
}
