using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

public class PadMirrorsTests
{
    private const int XSlots = 4, HidSlots = 4;
    private const int Xbox = 0, Mirror = 0, Hid = 4; // slot 0 is either a real Xbox pad or Steam's mirror; slot 4 the DualSense

    private sealed class Rig
    {
        public long Now = 100_000;
        public readonly PadMirrors M;
        public readonly bool[] Connected = new bool[XSlots + HidSlots];
        public readonly bool[] Holds = new bool[XSlots + HidSlots];

        public Rig(params int[] connected)
        {
            M = new PadMirrors(XSlots, HidSlots, () => Now);
            foreach (var s in connected) Connected[s] = true;
            M.Update(Connected, [], Holds);
        }

        public void Poll(int afterMs, params (int Slot, string Key)[] presses)
        {
            Now += afterMs;
            M.Update(Connected, presses, Holds);
        }

        // A press on the real pad and its mirror `lag` ms later (negative: the mirror first), then time for the verdict.
        public void Mirrored(string key, int lag = 16, string? mirrorKey = null)
        {
            if (lag == 0) Poll(500, (Hid, key), (Mirror, mirrorKey ?? key));
            else if (lag > 0) { Poll(500, (Hid, key)); Poll(lag, (Mirror, mirrorKey ?? key)); }
            else { Poll(500, (Mirror, mirrorKey ?? key)); Poll(-lag, (Hid, key)); }
            Poll(PadMirrors.WindowMs + 16);
        }

        // A press on slot 0 alone while the PlayStation pad is idle (or holds a button: a long-press activator).
        public void Alone(string key, bool hidHolds = false)
        {
            Holds[Hid] = hidHolds;
            Poll(1000, (Xbox, key));
            Poll(PadMirrors.WindowMs + 16);
            Holds[Hid] = false;
        }

        // A press on the PlayStation pad that slot 0 does not follow: what a pad of its own does, never a mirror.
        public void HidAlone(string key = "a")
        {
            Poll(1000, (Hid, key));
            Poll(PadMirrors.WindowMs + 16);
        }

        // Both players play on their own for a while: the evidence of a pad of its own.
        public void PlaySeparately(int rounds = PadMirrors.History)
        {
            for (var i = 0; i < rounds; i++)
            {
                HidAlone();
                Alone("a");
            }
        }
    }

    private static bool OtherController(PadMirrors m, string source = "controller") =>
        BluetoothRecovery.OtherInputForSwitchOff(m.PhysicalCount(), bluetoothPads: 1, source);

    [Fact]
    public void Mirror_IsRecognizedFromConsistentCoincidence_AndCountsOnce()
    {
        var r = new Rig(Mirror, Hid);
        Assert.Equal(1, r.M.PhysicalCount()); // unknown: counts low
        for (var i = 0; i < PadMirrors.HitsForMirror - 1; i++) r.Mirrored(i % 2 == 0 ? "a" : "down");
        Assert.False(r.M.IsMirror(Mirror));
        r.Mirrored("b");
        Assert.True(r.M.IsMirror(Mirror));
        Assert.Equal(1, r.M.PhysicalCount());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-16)]
    [InlineData(48)]
    public void Mirror_InTheSamePoll_AheadOrLate_IsRecognized(int lag)
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a", lag);
        Assert.True(r.M.IsMirror(Mirror));
    }

    [Fact]
    public void Mirror_WithARemappedButtonNowAndThen_IsRecognized()
    {
        var r = new Rig(Mirror, Hid);
        r.Mirrored("a", mirrorKey: "b"); // a Steam Input layout turned Cross into B: tells nothing
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("x");
        r.Mirrored("a", mirrorKey: "b");
        Assert.True(r.M.IsMirror(Mirror));
    }

    [Fact]
    public void OwnXboxPad_NextToAPlayStationPad_CountsTwice()
    {
        var r = new Rig(Xbox, Hid);
        r.PlaySeparately();
        Assert.False(r.M.IsMirror(Xbox));
        Assert.Equal(2, r.M.PhysicalCount());
        Assert.True(OtherController(r.M));
    }

    [Fact]
    public void LonePressesAlone_NeverMakeAPadOfItsOwn()
    {
        // A DS4Windows mirror (vendor 045E, not Steam): touch regions or gyro mapped to buttons while the real pad rests.
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < 30; i++) r.Alone("rb");
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(OtherController(r.M));
    }

    [Fact]
    public void KnownMirror_With30LonePresses_IsNoOtherController()
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a");
        for (var i = 0; i < 30; i++) r.Alone("rb");
        Assert.True(r.M.IsMirror(Mirror));
        Assert.False(OtherController(r.M));
    }

    [Fact]
    public void LonePresses_WhileAnotherSonyPadCannotBeRead_AreNoEvidence()
    {
        // Pad A (slot 4) is read; pad B waits for its reopen, its mirror (slot 0) presses: lone against A, but no misses meanwhile.
        var r = new Rig(Mirror, Hid);
        r.M.HidUnsettled = true;
        r.PlaySeparately(20);
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(OtherController(r.M));
    }

    [Fact]
    public void LonePresses_RightAfterAHidPadDropped_AreNoEvidence()
    {
        // Lone presses on both sides, quickly: within 5 s this would be evidence of a pad of its own.
        static void Quickly(Rig r)
        {
            for (var i = 0; i < PadMirrors.History; i++)
            {
                r.Poll(50, (Hid, "a"));
                r.Poll(300, (Mirror, "b"));
                r.Poll(PadMirrors.WindowMs + 16);
            }
        }
        var control = new Rig(Mirror, Hid);
        Quickly(control);
        Assert.Equal(2, control.M.PhysicalCount());

        var r = new Rig(Mirror, Hid, 5);
        r.Connected[5] = false; // the second PlayStation pad went away a moment ago, its mirror (slot 0) stays a little longer
        r.Poll(16);
        Quickly(r);
        Assert.True(r.Now - 100_000 < PadMirrors.SettleMs);
        Assert.Equal(1, r.M.PhysicalCount());
    }

    [Fact]
    public void CouchCoop_BothPlayersMashing_KeepsTheXboxPadItsOwn()
    {
        var r = new Rig(Xbox, Hid);
        r.PlaySeparately();
        var keys = new[] { "a", "b", "x", "y", "up", "down" };
        var random = new Random(7);
        for (var i = 0; i < 400; i++)
        {
            // Both press within a quarter second of each other, now and then the same button within a few milliseconds.
            var same = i % 4 == 0;
            var hidKey = keys[random.Next(keys.Length)];
            var xKey = same ? hidKey : keys[random.Next(keys.Length)];
            r.Holds[Hid] = random.Next(2) == 0;
            r.Poll(150 + random.Next(200), (Hid, hidKey));
            r.Poll(same ? random.Next(0, 60) : 100 + random.Next(140), (Xbox, xKey));
            r.Holds[Hid] = false;
            Assert.False(r.M.IsMirror(Xbox), $"round {i}");
        }
        r.Poll(PadMirrors.WindowMs + 16);
        Assert.False(r.M.IsMirror(Xbox));
        Assert.Equal(2, r.M.PhysicalCount());
    }

    [Fact]
    public void CouchCoop_BeforeTheXboxPadIsKnown_CountsLow()
    {
        var r = new Rig(Xbox, Hid);
        r.Poll(100, (Hid, "a"));
        r.Poll(120, (Xbox, "b"));
        r.Poll(PadMirrors.WindowMs + 16);
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(OtherController(r.M));
    }

    [Fact]
    public void MirrorWithActivatorPresses_NeverCountsAsAnotherController()
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a");
        for (var round = 0; round < 20; round++)
        {
            // Long-press and turbo activators: the mirror presses alone while the real pad holds its button. No evidence.
            for (var i = 0; i < 5; i++) r.Alone("y", hidHolds: true);
            // Touch regions: lone presses with the real pad idle.
            for (var i = 0; i < PadMirrors.History + 4; i++) r.Alone("rb");
            r.Mirrored("a");
            Assert.True(r.M.IsMirror(Mirror), $"round {round}");
            Assert.False(OtherController(r.M), $"round {round}");
        }
    }

    [Fact]
    public void SteamVirtualPad_NextToAPlayStationPad_NeverCountsAsAnotherController()
    {
        var r = new Rig(Mirror, Hid);
        r.M.MarkSteamVirtual(Mirror, true);
        r.M.Update(r.Connected, [], r.Holds);
        // Even evidence of a pad of its own: Steam always mirrors some physical pad.
        r.PlaySeparately(20);
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(OtherController(r.M));
        Assert.True(OtherController(r.M, "keyboard"));
    }

    [Fact]
    public void SteamVirtualPad_WithoutAPress_CountsNothing()
    {
        var r = new Rig(Mirror);
        r.M.MarkSteamVirtual(Mirror, true);
        r.Poll(16);
        Assert.Equal(0, r.M.PhysicalCount());
        r.Poll(16, (Mirror, "a"));
        Assert.Equal(1, r.M.PhysicalCount()); // it pressed: someone holds a pad behind it
    }

    [Fact]
    public void WhenTheHidPadGoesAway_ItsMirrorCountsNothing_UntilItPresses()
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("x");
        r.Connected[Hid] = false; // Bluetooth off: the DualSense is gone, Steam keeps its virtual pad a while
        r.Poll(500);
        Assert.False(r.M.IsMirror(Mirror));
        Assert.Equal(0, r.M.PhysicalCount()); // no other controller: Bluetooth comes back by itself
        Assert.True(r.M.IsPhantom(Mirror));  // and the pad list is empty: "no controller left"
        r.Poll(500, (Mirror, "a"));
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(r.M.IsPhantom(Mirror));
    }

    [Fact]
    public void WhenTheHidPadGoesAway_AnUnknownSlotCountsNothing_AnOwnPadStays()
    {
        var unknown = new Rig(Mirror, Hid);
        unknown.Connected[Hid] = false;
        unknown.Poll(500);
        Assert.Equal(0, unknown.M.PhysicalCount());

        var own = new Rig(Xbox, Hid);
        own.PlaySeparately();
        own.Connected[Hid] = false;
        own.Poll(500);
        Assert.Equal(1, own.M.PhysicalCount());
    }

    [Fact]
    public void WrongMirror_IsCorrectedByAClearMissStreak()
    {
        var r = new Rig(Xbox, Hid);
        // Two players pressed the same buttons at the same moment six times: taken for a mirror (counts low, the safe side).
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a", lag: 20);
        Assert.True(r.M.IsMirror(Xbox));
        for (var i = 0; i < PadMirrors.NonFollowsForOwn; i++) r.HidAlone();
        for (var i = 0; i < PadMirrors.History - 1; i++) r.Alone("b");
        Assert.True(r.M.IsMirror(Xbox));
        r.Alone("b");
        Assert.False(r.M.IsMirror(Xbox));
        Assert.Equal(2, r.M.PhysicalCount());
    }

    [Fact]
    public void WrongMirror_WithoutHitsForMinutes_IsUnknownAgain()
    {
        var r = new Rig(Xbox, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a", lag: 20);
        Assert.True(r.M.IsMirror(Xbox));
        // The Xbox player keeps playing while the PlayStation player holds a button: no misses, but no hit either.
        for (var i = 0; i < 6; i++)
        {
            r.Poll(60_000);
            r.Alone("b", hidHolds: true);
        }
        Assert.False(r.M.IsMirror(Xbox)); // its battery and low-battery warning show again
    }

    [Fact]
    public void IdleMirror_StaysAMirror()
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a");
        r.Poll(30 * 60_000);
        r.Mirrored("a");
        Assert.True(r.M.IsMirror(Mirror));
    }

    [Fact]
    public void WrongOwn_IsCorrectedByAClearHitStreak()
    {
        var r = new Rig(Mirror, Hid);
        // At first the mirror lagged so much it looked like a pad of its own (presses alone, the real pad's presses unanswered).
        r.PlaySeparately();
        Assert.Equal(2, r.M.PhysicalCount());
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a");
        Assert.True(r.M.IsMirror(Mirror));
        Assert.Equal(1, r.M.PhysicalCount());
        Assert.False(OtherController(r.M));
    }

    [Fact]
    public void XboxPadsAlone_CountAsUsual()
    {
        var r = new Rig(0, 1);
        Assert.Equal(2, r.M.PhysicalCount());
        r.Connected[0] = r.Connected[1] = false;
        r.Connected[Hid] = true;
        r.Poll(16);
        Assert.Equal(1, r.M.PhysicalCount());
    }

    [Fact]
    public void ADisconnect_ForgetsTheMirror()
    {
        var r = new Rig(Mirror, Hid);
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("x");
        Assert.True(r.M.IsMirror(Mirror));
        r.Connected[Mirror] = false;
        r.Poll(500);
        Assert.False(r.M.IsMirror(Mirror));
        Assert.Equal(1, r.M.PhysicalCount());
    }

    [Fact]
    public void BluetoothSwitchOff_AMirroredPadIsNoOtherController_BeforeAndAfterTheMatch()
    {
        var r = new Rig(Mirror, Hid); // DualSense over Bluetooth, read over HID, and Steam's mirror of it
        Assert.False(OtherController(r.M));
        for (var i = 0; i < PadMirrors.HitsForMirror; i++) r.Mirrored("a");
        Assert.False(OtherController(r.M));
        Assert.True(OtherController(r.M, "keyboard"));
    }

    [Theory]
    [InlineData(0, new[] { 0, 6, 5 }, 1)]
    [InlineData(5, new[] { 0, 6, 5 }, 2)]
    [InlineData(6, new[] { 0, 6, 5 }, 3)]
    [InlineData(4, new[] { 4 }, 1)]
    [InlineData(4, new[] { 1, 4 }, 3)]
    public void PlayStationPads_AreNumberedAfterTheXInputPads(int index, int[] connected, int expected) =>
        Assert.Equal(expected, BatteryRules.PadNumber(index, connected));

    [Fact]
    public void PadMerger_NamesTheNewButtonsPerSlot()
    {
        var merger = new PadMerger();
        var slots = new PadSample?[8];
        slots[0] = new PadSample(0, 0, 0, 0, 0, 0, 0);
        slots[4] = new PadSample(0, 0, 0, 0, 0, 0, 0);
        merger.Update(slots);
        slots[4] = new PadSample(0x1000 | 0x0001, 0, 0, 0, 0, 0, 0);
        merger.Update(slots);
        Assert.Equal([(4, "a,up")], merger.NewPresses);
        Assert.True(merger.Holds(4));
        Assert.False(merger.Holds(0));
    }
}
