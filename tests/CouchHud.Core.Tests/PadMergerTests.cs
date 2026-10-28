using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

public class PadMergerTests
{
    private const ushort A = 0x1000, B = 0x2000, Guide = 0x0400;

    private static PadSample? P(ushort buttons = 0) => new PadSample(buttons, 0, 0, 0, 0, 0, 0);

    private static string Ev(IReadOnlyList<(string Button, bool Down)> e) => string.Join(",", e.Select(x => (x.Down ? "+" : "-") + x.Button));

    [Fact]
    public void RightStick_ReadsUpAndDown_AndSmallDeflectionIsIgnored()
    {
        // The setup assistant's long texts scroll with it; positive is up in XInput, like the left stick.
        Assert.Equal(["rsup"], PadMerger.ToPressed(new PadSample(0, 0, 0, 0, 0, 0, 30000)));
        Assert.Equal(["rsdown"], PadMerger.ToPressed(new PadSample(0, 0, 0, 0, 0, 0, -30000)));
        Assert.Empty(PadMerger.ToPressed(new PadSample(0, 0, 0, 0, 0, 0, PadMerger.StickThreshold)));
        Assert.Empty(PadMerger.ToPressed(new PadSample(0, 0, 0, 0, 0, 30000, 0)));
    }

    [Fact]
    public void RightStick_PressAndRelease_AreEventsOfTheirOwn()
    {
        var m = new PadMerger();
        PadSample? Stick(short ry) => new PadSample(0, 0, 0, 0, 0, 0, ry);
        Assert.Equal("+rsup", Ev(m.Update([Stick(30000)])));
        Assert.Empty(m.Update([Stick(30000)]));
        Assert.True(m.IsPressed("rsup"));
        Assert.Equal(["+rsdown", "-rsup"], Ev(m.Update([Stick(-30000)])).Split(',').Order(StringComparer.Ordinal));
        Assert.Equal("-rsdown", Ev(m.Update([Stick(0)])));
    }

    [Fact]
    public void MirroredPads_ProduceSingleDownAndUp()
    {
        var m = new PadMerger();
        Assert.Equal("+a", Ev(m.Update([P(A), P(A)])));
        Assert.Empty(m.Update([P(A), P(A)]));
        Assert.Equal("-a", Ev(m.Update([P(), P()])));
    }

    [Fact]
    public void OverlappingHold_NoUpUntilAllReleased()
    {
        var m = new PadMerger();
        Assert.Equal("+a", Ev(m.Update([P(A), null])));
        Assert.Empty(m.Update([P(A), P(A)]));
        Assert.Empty(m.Update([P(), P(A)]));
        Assert.True(m.IsPressed("a"));
        Assert.Equal("-a", Ev(m.Update([P(), P()])));
    }

    [Fact]
    public void GuideBit_IsReported()
    {
        var m = new PadMerger();
        Assert.Equal("+guide", Ev(m.Update([P(Guide), P(Guide)])));
        Assert.Equal("-guide", Ev(m.Update([P(), P()])));
    }

    [Fact]
    public void DisconnectWhileHeld_EmitsUp()
    {
        var m = new PadMerger();
        m.Update([P(A)]);
        Assert.Equal("-a", Ev(m.Update([null])));
        Assert.False(m.IsPressed("a"));
    }

    [Fact]
    public void DisconnectOfOneMirror_KeepsButtonHeld()
    {
        var m = new PadMerger();
        m.Update([P(A), P(A)]);
        Assert.Empty(m.Update([null, P(A)]));
    }

    [Fact]
    public void MultipleChangesInOnePoll_AreAllReported()
    {
        var m = new PadMerger();
        m.Update([P(A)]);
        var e = m.Update([P(B)]);
        Assert.Contains(("b", true), e);
        Assert.Contains(("a", false), e);
        Assert.Equal(2, e.Count);
    }

    [Fact]
    public void HandoffBetweenPads_IsSilent()
    {
        var m = new PadMerger();
        m.Update([P(A), P()]);
        Assert.Empty(m.Update([P(), P(A)]));
    }

    [Fact]
    public void StickAndTrigger_MapToButtons_AndStrongestStickWins()
    {
        var m = new PadMerger();
        var weak = new PadSample(0, 200, 0, 1000, 0, 0, 0);
        var strong = new PadSample(0, 0, 0, 30000, 20000, 0, 0);
        var e = m.Update([weak, strong]);
        Assert.Contains(("lt", true), e);
        Assert.Contains(("right", true), e);
        Assert.Contains(("up", true), e);
        Assert.True(m.Sticks().X > 0.9);
    }

    [Fact]
    public void MirrorDetection_FiresOnceForIdenticalChanges()
    {
        var m = new PadMerger();
        var hits = new List<(int, int)>();
        m.MirrorDetected += (a, b) => hits.Add((a, b));
        m.Update([P(), P()]);
        m.Update([P(A), P(A)]);
        m.Update([P(), P()]);
        Assert.Single(hits);
        Assert.Equal((0, 1), hits[0]);
    }

    [Fact]
    public void MirrorDetection_IgnoresDifferentChanges()
    {
        var m = new PadMerger();
        var hits = 0;
        m.MirrorDetected += (_, _) => hits++;
        m.Update([P(), P()]);
        m.Update([P(A), P(B)]);
        Assert.Equal(0, hits);
    }
}
