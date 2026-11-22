using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.Core.Tests;

public class RigidTests
{
    private static HmdMatrix34_t Pose(float yawDeg, float pitchDeg, float x, float y, float z)
    {
        // yaw about y, then pitch about x
        double a = yawDeg * Math.PI / 180, b = pitchDeg * Math.PI / 180;
        float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a), cb = (float)Math.Cos(b), sb = (float)Math.Sin(b);
        var yaw = new HmdMatrix34_t { m0 = ca, m2 = sa, m5 = 1, m8 = -sa, m10 = ca };
        var pitch = Rigid.FromTiltAndOffset(pitchDeg, x, y, z);
        return Rigid.Multiply(yaw, pitch);
    }

    private static void AssertClose(HmdMatrix34_t a, HmdMatrix34_t b)
    {
        var x = Rigid.ToArray(a);
        var y = Rigid.ToArray(b);
        for (int i = 0; i < 12; i++) Assert.Equal(y[i], x[i], 4);
    }

    [Fact]
    public void Inverse_undoes_a_transform()
    {
        var m = Pose(30, -20, 0.1f, 1.2f, -0.4f);
        AssertClose(Rigid.Multiply(Rigid.Inverse(m), m), Rigid.Identity);
        AssertClose(Rigid.Multiply(m, Rigid.Inverse(m)), Rigid.Identity);
    }

    [Fact]
    public void Mirroring_twice_is_a_no_op()
    {
        var m = Pose(40, 10, 0.03f, 0.02f, 0.12f);
        AssertClose(Rigid.MirrorX(Rigid.MirrorX(m)), m);
    }

    [Fact]
    public void Grabbed_panel_keeps_its_offset_to_the_hand()
    {
        // What WristGrab does: panel = hand · (hand⁻¹ · panel at grab time), placement = wrist⁻¹ · panel.
        var wrist = Pose(10, 0, 0, 1.0f, 0);
        var placement = Rigid.FromTiltAndOffset(-45, 0, 0.02f, 0.12f);
        var panel = Rigid.Multiply(wrist, placement);
        var hand = Pose(-30, 15, 0.2f, 1.1f, 0.1f);
        var handToPanel = Rigid.Multiply(Rigid.Inverse(hand), panel);

        // Not moving the hand: the placement must stay exactly the same (no jump when grabbing).
        var same = Rigid.Multiply(Rigid.Inverse(wrist), Rigid.Multiply(hand, handToPanel));
        AssertClose(same, placement);

        // Moving the hand 10 cm up moves the panel 10 cm up in the world.
        var handUp = hand;
        handUp.m7 += 0.1f;
        var newPanel = Rigid.Multiply(handUp, handToPanel);
        Assert.Equal(panel.m7 + 0.1f, newPanel.m7, 4);
    }
}
