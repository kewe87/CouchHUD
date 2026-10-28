namespace CouchHud.Core.Input;

/// <summary>Raw state of one pad slot (an XInput slot, or a PlayStation pad read over HID in the same shape).</summary>
public readonly record struct PadSample(ushort Buttons, byte LeftTrigger, byte RightTrigger, short LX, short LY, short RX, short RY);

/// <summary>
/// Merges the states of all connected pad slots into one logical pad. Steam Input, DS4Windows
/// and similar tools add virtual pads that mirror the real one (also a PlayStation pad CouchHUD reads
/// itself over HID); a button counts as down as soon as any pad holds it and as up once no pad does,
/// so events are never duplicated.
/// </summary>
public sealed class PadMerger
{
    public static readonly (string Name, ushort Mask)[] Buttons =
    [
        ("up", 0x0001), ("down", 0x0002), ("left", 0x0004), ("right", 0x0008),
        ("start", 0x0010), ("back", 0x0020), ("ls", 0x0040), ("rs", 0x0080),
        ("lb", 0x0100), ("rb", 0x0200), ("guide", 0x0400),
        ("a", 0x1000), ("b", 0x2000), ("x", 0x4000), ("y", 0x8000)
    ];

    public const short StickThreshold = 16000;
    public const byte TriggerThreshold = 128;

    private HashSet<string> _union = [];
    private HashSet<string>[] _perSlot = [];
    private PadSample?[] _samples = [];
    private bool _mirrorReported;

    /// <summary>Buttons currently held on any pad.</summary>
    public IReadOnlyCollection<string> Pressed => _union;

    public bool IsPressed(string button) => _union.Contains(button);

    /// <summary>The slots that saw a new press in the last poll (a button or stick direction that was not held there before).</summary>
    public IReadOnlyList<int> PressedSlots { get; private set; } = [];

    /// <summary>Per slot with a new press in the last poll: the newly held buttons, sorted and joined ("a,up").</summary>
    public IReadOnlyList<(int Slot, string Key)> NewPresses { get; private set; } = [];

    /// <summary>Whether a slot holds any button, direction or trigger after the last poll.</summary>
    public bool Holds(int slot) => slot >= 0 && slot < _perSlot.Length && _perSlot[slot].Count > 0;

    /// <summary>Raised once per instance when two slots report identical changes in the same poll.</summary>
    public event Action<int, int>? MirrorDetected;

    /// <summary>Strongest stick deflection among connected pads, -1..1.</summary>
    public (double X, double Y, double ScrollY) Sticks()
    {
        PadSample? best = null;
        var bestScore = -1;
        foreach (var s in _samples)
        {
            if (s is not { } p) continue;
            var score = Math.Abs((int)p.LX) + Math.Abs((int)p.LY) + Math.Abs((int)p.RY);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best is { } b ? (b.LX / 32768.0, b.LY / 32768.0, b.RY / 32768.0) : (0, 0, 0);
    }

    private static readonly Dictionary<string, ushort> Dpad = new() { ["up"] = 0x0001, ["down"] = 0x0002, ["left"] = 0x0004, ["right"] = 0x0008 };

    /// <summary>
    /// A direction that is held only by the left stick, not by the d-pad of any pad (in the last poll). Where the stick is the
    /// cursor (the couch browser) such a direction is no button.
    /// </summary>
    public bool FromStickOnly(string button)
    {
        if (!Dpad.TryGetValue(button, out var mask)) return false;
        foreach (var s in _samples)
        {
            if (s is { } p && (p.Buttons & mask) != 0) return false;
        }
        return true;
    }

    /// <summary>Feeds one poll (index = slot, null = disconnected) and returns the Down/Up events of the merged pad.</summary>
    public IReadOnlyList<(string Button, bool Down)> Update(IReadOnlyList<PadSample?> slots)
    {
        var perSlot = new HashSet<string>[slots.Count];
        var union = new HashSet<string>();
        for (var i = 0; i < slots.Count; i++)
        {
            perSlot[i] = slots[i] is { } s ? ToPressed(s) : [];
            union.UnionWith(perSlot[i]);
        }

        var pressedSlots = new List<int>();
        var newPresses = new List<(int, string)>();
        for (var i = 0; i < perSlot.Length; i++)
        {
            var before = i < _perSlot.Length ? _perSlot[i] : [];
            var fresh = perSlot[i].Where(b => !before.Contains(b)).Order(StringComparer.Ordinal).ToList();
            if (fresh.Count == 0) continue;
            pressedSlots.Add(i);
            newPresses.Add((i, string.Join(",", fresh)));
        }
        PressedSlots = pressedSlots;
        NewPresses = newPresses;

        var events = new List<(string, bool)>();
        foreach (var b in union) if (!_union.Contains(b)) events.Add((b, true));
        foreach (var b in _union) if (!union.Contains(b)) events.Add((b, false));

        if (!_mirrorReported) DetectMirror(perSlot, slots);

        _union = union;
        _perSlot = perSlot;
        _samples = slots.ToArray();
        return events;
    }

    private void DetectMirror(HashSet<string>[] now, IReadOnlyList<PadSample?> slots)
    {
        var changed = new List<(int Slot, string Key)>();
        for (var i = 0; i < now.Length; i++)
        {
            // Only compare slots that were live in the previous poll too (a fresh connection is not a mirror).
            if (i >= _samples.Length || _samples[i] is null || slots[i] is null) continue;
            var before = _perSlot[i];
            var diff = now[i].Except(before).Select(b => "+" + b)
                .Concat(before.Except(now[i]).Select(b => "-" + b)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (diff.Count > 0) changed.Add((i, string.Join(",", diff)));
        }
        for (var a = 0; a < changed.Count; a++)
        {
            for (var b = a + 1; b < changed.Count; b++)
            {
                if (changed[a].Key != changed[b].Key) continue;
                _mirrorReported = true;
                MirrorDetected?.Invoke(changed[a].Slot, changed[b].Slot);
                return;
            }
        }
    }

    public static HashSet<string> ToPressed(PadSample g)
    {
        var pressed = new HashSet<string>();
        foreach (var (name, mask) in Buttons)
        {
            if ((g.Buttons & mask) != 0) pressed.Add(name);
        }
        // Left stick doubles as d-pad for navigation.
        if (g.LY > StickThreshold) pressed.Add("up");
        if (g.LY < -StickThreshold) pressed.Add("down");
        if (g.LX < -StickThreshold) pressed.Add("left");
        if (g.LX > StickThreshold) pressed.Add("right");
        if (g.LeftTrigger > TriggerThreshold) pressed.Add("lt");
        if (g.RightTrigger > TriggerThreshold) pressed.Add("rt");
        return pressed;
    }
}
