namespace CouchHud.Core.Input;

/// <summary>
/// Tells which XInput slots are only the virtual mirror (Steam Input, DS4Windows) of a PlayStation pad CouchHUD reads itself over
/// HID, so every physical pad counts once: in the battery list and wherever "another controller" decides something (Bluetooth).
/// <para>
/// Evidence over a sliding window, from both sides. Every new press of an XInput slot is judged once it is <see cref="WindowMs"/>
/// old: a <b>hit</b> on HID pad h when h pressed the very same buttons within <see cref="MatchMs"/> (before or after); a <b>miss</b>
/// when no HID pad pressed anything within <see cref="WindowMs"/> and none held a button at that moment (an activator of Steam Input
/// or DS4Windows, a long press or turbo, comes with a held button on the real pad); anything else tells nothing. Every new press of
/// a HID pad is judged the same way for each XInput slot: <b>followed</b> when the slot pressed anything within
/// <see cref="WindowMs"/>, else a <b>non-follow</b> (a mirror always follows; touch regions or gyro mapped to buttons only add lone
/// presses to a mirror, they never make it skip one). The last <see cref="History"/> judgements of each kind decide:
/// </para>
/// <list type="bullet">
/// <item>Mirror of h: at least <see cref="HitsForMirror"/> presses hit h (two players pressing the same buttons in the same 80 ms
/// that often is no coincidence any more), also for a slot that was taken for a pad of its own.</item>
/// <item>A pad of its own: the last <see cref="MissesForOwn"/> presses were all misses (a known mirror: the whole window) <b>and</b>
/// at least <see cref="NonFollowsForOwn"/> HID presses were not followed. Lone presses alone never make a pad of its own.</item>
/// </list>
/// No misses are counted while a HID pad dropped in the last <see cref="SettleMs"/> or while <see cref="HidUnsettled"/> (a Sony
/// pad that cannot be read right now: its mirror's presses would look lone). A mirror that pressed again and again without a hit
/// for <see cref="MirrorExpiryMs"/> is unknown again (a real pad taken for a mirror gets its battery warning back).
/// <para>
/// Until a slot is known, <see cref="PhysicalCount"/> counts low. When a HID pad goes away (Bluetooth off), the XInput slots that
/// were not known as pads of their own are orphaned and count 0 until they press something themselves; a Steam virtual pad
/// (<see cref="MarkSteamVirtual"/>) counts 0 until its first press and never counts as a pad of its own next to a HID pad. Not
/// thread-safe: one caller (the polling thread).
/// </para>
/// </summary>
public sealed class PadMirrors
{
    public const int WindowMs = 250;
    public const int MatchMs = 80;
    public const int History = 8;
    public const int HitsForMirror = 6;
    public const int MissesForOwn = 6;
    public const int NonFollowsForOwn = 4;
    public const int SettleMs = 5000;
    public const long MirrorExpiryMs = 5 * 60_000;
    private const int MaxPending = 16;
    private const int HidLog = 16;
    private const int Miss = -1, Nothing = -2;

    private enum Kind { Unknown, Mirror, Own }

    private sealed class Slot
    {
        public Kind Kind;
        public int MirrorOf;
        public bool SteamVirtual;
        public bool Pressed;                 // pressed anything since it connected (or since it was orphaned)
        public bool Orphan;                  // its HID pad went away: counts 0 until it presses something itself
        public long LastHit;
        public int SinceHit;                 // judged presses without a hit since the last hit
        public readonly Queue<int> Judged = new();                          // HID index (hit), Miss or Nothing
        public readonly Queue<bool> Follows = new();                        // per HID press: followed or not
        public readonly Queue<(string Key, long At, bool HidHeld)> Pending = new();
        public readonly Queue<long> Presses = new();                        // when it pressed lately
    }

    private readonly int _xinputSlots;
    private readonly int _hidSlots;
    private readonly Func<long> _clock;
    private readonly Slot[] _x;
    private readonly Queue<(string Key, long At)>[] _hidPresses;
    private readonly Queue<(string Key, long At)>[] _hidPending;
    private bool[] _connected;
    private long _lastHidDrop = long.MinValue / 2;

    public PadMirrors(int xinputSlots, int hidSlots, Func<long>? clockMs = null)
    {
        _xinputSlots = xinputSlots;
        _hidSlots = hidSlots;
        _clock = clockMs ?? (() => Environment.TickCount64);
        _x = Enumerable.Range(0, xinputSlots).Select(_ => new Slot()).ToArray();
        _hidPresses = Enumerable.Range(0, hidSlots).Select(_ => new Queue<(string, long)>()).ToArray();
        _hidPending = Enumerable.Range(0, hidSlots).Select(_ => new Queue<(string, long)>()).ToArray();
        _connected = new bool[xinputSlots + hidSlots];
    }

    /// <summary>A Sony pad is listed but cannot be read right now (its reopen waits): lone presses are no evidence meanwhile.</summary>
    public bool HidUnsettled { get; set; }

    /// <summary>A connected XInput slot is Steam's virtual pad (vendor 0x28DE): it mirrors some physical pad.</summary>
    public void MarkSteamVirtual(int xinputSlot, bool steamVirtual)
    {
        if (xinputSlot >= 0 && xinputSlot < _xinputSlots) _x[xinputSlot].SteamVirtual = steamVirtual;
    }

    /// <summary>
    /// One poll: which slots are connected (XInput first, then the HID pads), the new presses per slot as
    /// <see cref="PadMerger.NewPresses"/> gives them, and which slots hold anything (<see cref="PadMerger.Holds"/>; null = none).
    /// </summary>
    public void Update(IReadOnlyList<bool> connected, IReadOnlyList<(int Slot, string Key)> newPresses, IReadOnlyList<bool>? holds = null)
    {
        var now = _clock();
        var next = new bool[_xinputSlots + _hidSlots];
        for (var i = 0; i < next.Length && i < connected.Count; i++) next[i] = connected[i];
        for (var x = 0; x < _xinputSlots; x++)
        {
            if (!next[x]) Forget(x);
        }
        for (var h = 0; h < _hidSlots; h++)
        {
            if (next[_xinputSlots + h]) continue;
            _hidPresses[h].Clear();
            _hidPending[h].Clear();
            if (!_connected[_xinputSlots + h]) continue;
            // The HID pad went away (Bluetooth off, cable out): whatever was not known as a pad of its own may be its mirror.
            _lastHidDrop = now;
            for (var x = 0; x < _xinputSlots; x++)
            {
                var s = _x[x];
                if (!next[x] || s.Kind == Kind.Own || (s.Kind == Kind.Mirror && s.MirrorOf != h)) continue;
                ClearEvidence(s);
                s.Kind = Kind.Unknown;
                s.Orphan = true;
            }
        }
        _connected = next;

        var hidHeld = false;
        for (var h = 0; h < _hidSlots; h++)
            hidHeld |= next[_xinputSlots + h] && holds is not null && _xinputSlots + h < holds.Count && holds[_xinputSlots + h];
        foreach (var (slot, key) in newPresses)
        {
            var h = slot - _xinputSlots;
            if (h < 0 || h >= _hidSlots || !next[slot]) continue;
            Push(_hidPresses[h], (key, now), HidLog);
            Push(_hidPending[h], (key, now), MaxPending);
        }
        foreach (var (slot, key) in newPresses)
        {
            if (slot < 0 || slot >= _xinputSlots || !next[slot]) continue;
            var s = _x[slot];
            s.Pressed = true;
            s.Orphan = false;
            Push(s.Pending, (key, now, hidHeld), MaxPending);
            Push(s.Presses, now, HidLog);
        }
        for (var x = 0; x < _xinputSlots; x++)
        {
            var s = _x[x];
            while (s.Pending.Count > 0 && now - s.Pending.Peek().At > WindowMs) Judge(s, s.Pending.Dequeue(), now);
        }
        for (var h = 0; h < _hidSlots; h++)
        {
            while (_hidPending[h].Count > 0 && now - _hidPending[h].Peek().At > WindowMs) JudgeHid(_hidPending[h].Dequeue());
        }
    }

    /// <summary>Whether an XInput slot is known to be the mirror of a HID pad (it then does not count as a pad of its own).</summary>
    public bool IsMirror(int xinputSlot) => xinputSlot >= 0 && xinputSlot < _xinputSlots && _x[xinputSlot].Kind == Kind.Mirror;

    /// <summary>
    /// Whether an XInput slot stands for no pad of its own right now: a known mirror, an orphaned one (its HID pad went away), or a
    /// Steam virtual pad that never pressed anything. Left out of the pad list, so "no controller left" is told when it is true.
    /// </summary>
    public bool IsPhantom(int xinputSlot)
    {
        if (xinputSlot < 0 || xinputSlot >= _xinputSlots) return false;
        var s = _x[xinputSlot];
        return s.Kind == Kind.Mirror || s.Orphan || (s.SteamVirtual && !s.Pressed);
    }

    /// <summary>
    /// Physical pads connected: the HID pads, the XInput slots known to be pads of their own, and the unknown XInput slots beyond
    /// the HID pads they could still mirror. Errs low, never high: "no other controller" lets Bluetooth come back by itself, while
    /// a mirror counted as another controller could leave the couch without its only one.
    /// </summary>
    public int PhysicalCount()
    {
        var hid = 0;
        var unpairedHid = 0;
        for (var h = 0; h < _hidSlots; h++)
        {
            if (!_connected[_xinputSlots + h]) continue;
            hid++;
            var paired = false;
            for (var x = 0; x < _xinputSlots; x++) paired |= _connected[x] && _x[x].Kind == Kind.Mirror && _x[x].MirrorOf == h;
            if (!paired) unpairedHid++;
        }
        var own = 0;
        var unknown = 0;
        for (var x = 0; x < _xinputSlots; x++)
        {
            var s = _x[x];
            if (!_connected[x] || s.Kind == Kind.Mirror || s.Orphan || (s.SteamVirtual && !s.Pressed)) continue;
            if (s.Kind == Kind.Own && !(s.SteamVirtual && hid > 0)) own++;
            else unknown++;
        }
        return hid + own + Math.Max(0, unknown - unpairedHid);
    }

    private static void Push<T>(Queue<T> queue, T item, int max)
    {
        queue.Enqueue(item);
        while (queue.Count > max) queue.Dequeue();
    }

    private void Judge(Slot s, (string Key, long At, bool HidHeld) press, long now)
    {
        var anyHid = false;
        var verdict = Miss;
        var best = long.MaxValue;
        for (var h = 0; h < _hidSlots; h++)
        {
            if (!_connected[_xinputSlots + h]) continue;
            anyHid = true;
            foreach (var (key, at) in _hidPresses[h])
            {
                var gap = Math.Abs(at - press.At);
                if (key == press.Key && gap <= MatchMs && gap < best)
                {
                    best = gap;
                    verdict = h;
                }
                else if (gap <= WindowMs && verdict == Miss) verdict = Nothing;
            }
        }
        if (!anyHid) return; // nothing to mirror: no evidence either way
        // A held button on the real pad (activators), a pad that just dropped or one that cannot be read: a lone press proves nothing.
        if (verdict == Miss && (press.HidHeld || HidUnsettled || press.At - _lastHidDrop < SettleMs)) verdict = Nothing;
        Push(s.Judged, verdict, History);
        if (verdict >= 0)
        {
            s.LastHit = now;
            s.SinceHit = 0;
        }
        else s.SinceHit++;
        Decide(s, now);
    }

    private void JudgeHid((string Key, long At) press)
    {
        // Steam may keep the PS button for itself: its presses never reach the mirror and tell nothing.
        if (press.Key == "guide") return;
        for (var x = 0; x < _xinputSlots; x++)
        {
            if (!_connected[x]) continue;
            var s = _x[x];
            var followed = s.Presses.Any(at => Math.Abs(at - press.At) <= WindowMs);
            Push(s.Follows, followed, History);
        }
    }

    private void Decide(Slot s, long now)
    {
        for (var h = 0; h < _hidSlots; h++)
        {
            if (s.Judged.Count(v => v == h) < HitsForMirror) continue;
            s.Kind = Kind.Mirror;
            s.MirrorOf = h;
            return;
        }
        var streak = 0;
        foreach (var v in s.Judged.Reverse()) { if (v != Miss) break; streak++; }
        var needed = s.Kind == Kind.Mirror ? History : MissesForOwn;
        if (streak >= needed && s.Follows.Count(f => !f) >= NonFollowsForOwn)
        {
            s.Kind = Kind.Own;
            return;
        }
        // A mirror that kept pressing without a single hit for minutes: maybe a real pad after all (its battery shows again).
        if (s.Kind == Kind.Mirror && s.SinceHit >= HitsForMirror && now - s.LastHit > MirrorExpiryMs) s.Kind = Kind.Unknown;
    }

    private static void ClearEvidence(Slot s)
    {
        s.Judged.Clear();
        s.Follows.Clear();
        s.Pending.Clear();
        s.Presses.Clear();
        s.SinceHit = 0;
    }

    private void Forget(int x)
    {
        var s = _x[x];
        ClearEvidence(s);
        s.Kind = Kind.Unknown;
        s.SteamVirtual = false;
        s.Pressed = false;
        s.Orphan = false;
    }
}
