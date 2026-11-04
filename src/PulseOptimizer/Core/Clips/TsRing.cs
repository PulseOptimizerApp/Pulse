namespace Pulse.Core.Clips;

/// <summary>
/// The instant-replay buffer. FFmpeg streams the encoded recording as MPEG-TS (188-byte packets); this keeps the last
/// N seconds in memory, split into groups that each start at a video keyframe, so a saved clip always starts cleanly.
/// Nothing is written to disk until a clip is saved.
/// </summary>
public sealed class TsRing
{
    public const int PacketSize = 188;
    public const int VideoPid = 0x100;   // FFmpeg's first stream; Pulse always maps video first
    const int PatPid = 0;

    sealed class Group
    {
        public double Time;                    // seconds on the ring's clock when the keyframe arrived
        public readonly MemoryStream Data = new();
    }

    readonly object _gate = new();
    readonly LinkedList<Group> _groups = new();
    readonly byte[] _carry = new byte[PacketSize];
    int _carryLen;
    byte[] _pat, _pmt;
    int _pmtPid = -1;
    long _bytes;
    Group _current;
    double _lastVideo = -1, _lastKeyframe = -1;

    /// <summary>Seconds of replay kept (a little more is kept so a full-length clip always starts on a keyframe).</summary>
    public double KeepSeconds { get; set; } = 60;
    /// <summary>Hard memory cap in bytes (0 = none). Old groups are dropped past it, whatever their age.</summary>
    public long MaxBytes { get; set; }

    /// <summary>
    /// Seconds since the last video keyframe arrived, or -1 if none has yet. Keyframes come every second while the
    /// capture works, so a large value means the picture stopped (for example a display mode change when a match
    /// loads) while sound kept going.
    /// </summary>
    public double SecondsSinceKeyframe(double now) { lock (_gate) return _lastKeyframe < 0 ? -1 : now - _lastKeyframe; }
    /// <summary>Seconds since any video data arrived, or -1 if none has yet.</summary>
    public double SecondsSinceVideo(double now) { lock (_gate) return _lastVideo < 0 ? -1 : now - _lastVideo; }
    /// <summary>Bytes currently held, for the "memory used" readout.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }
    /// <summary>Seconds of replay currently held.</summary>
    public double HeldSeconds(double now)
    {
        lock (_gate) return _groups.First == null ? 0 : now - _groups.First.Value.Time;
    }

    /// <summary>Adds bytes straight from FFmpeg's output (any length; partial packets are carried over).</summary>
    public void Write(ReadOnlySpan<byte> data, double now)
    {
        lock (_gate)
        {
            int i = 0;
            if (_carryLen > 0)
            {
                int take = Math.Min(PacketSize - _carryLen, data.Length);
                data[..take].CopyTo(_carry.AsSpan(_carryLen));
                _carryLen += take;
                i = take;
                if (_carryLen < PacketSize) return;
                Packet(_carry, now);
                _carryLen = 0;
            }
            for (; i + PacketSize <= data.Length; i += PacketSize)
            {
                if (data[i] != 0x47) { i = Resync(data, i) - PacketSize; continue; } // lost sync: find the next 0x47
                Packet(data.Slice(i, PacketSize), now);
            }
            if (i < data.Length) { data[i..].CopyTo(_carry); _carryLen = data.Length - i; }
            Prune(now);
        }
    }

    static int Resync(ReadOnlySpan<byte> data, int from)
    {
        for (int j = from + 1; j < data.Length; j++)
            if (data[j] == 0x47) return j;
        return data.Length;
    }

    void Packet(ReadOnlySpan<byte> p, double now)
    {
        int pid = ((p[1] & 0x1F) << 8) | p[2];
        if (pid == PatPid) { _pat = p.ToArray(); _pmtPid = PmtPidFromPat(p); return; }
        if (pid == _pmtPid) { _pmt = p.ToArray(); return; }
        if (pid == VideoPid)
        {
            _lastVideo = now;
            if (IsKeyframeStart(p)) { _lastKeyframe = now; _groups.AddLast(_current = new Group { Time = now }); }
        }
        if (_current == null) return; // nothing useful before the first keyframe
        // No keyframe for a long time means the picture stopped: don't keep piling sound onto the last frame,
        // or a saved clip becomes minutes of a frozen picture.
        if (pid != VideoPid && now - _current.Time > StaleGroupSeconds) return;
        _current.Data.Write(p);
        _bytes += PacketSize;
    }

    /// <summary>A packet that starts a video frame with the random-access flag set (FFmpeg sets it on keyframes).</summary>
    internal static bool IsKeyframeStart(ReadOnlySpan<byte> p)
    {
        bool payloadStart = (p[1] & 0x40) != 0;
        int afc = (p[3] >> 4) & 0x3;
        if (!payloadStart || (afc & 0x2) == 0 || p[4] == 0) return false; // no adaptation field
        return (p[5] & 0x40) != 0; // random_access_indicator
    }

    static int PmtPidFromPat(ReadOnlySpan<byte> p)
    {
        // PAT: header (4) + pointer field + table: table_id(1) len(2) ts_id(2) ver(1) sec(1) last(1) then programs.
        int ptr = 4 + 1 + p[4];
        int program = ptr + 8;
        if (program + 4 > p.Length) return -1;
        return ((p[program + 2] & 0x1F) << 8) | p[program + 3];
    }

    /// <summary>A group (one keyframe to the next) longer than this is treated as a stalled picture.</summary>
    public const double StaleGroupSeconds = 5;

    void Prune(double now)
    {
        while (MaxBytes > 0 && _bytes > MaxBytes && _groups.First?.Next != null)
        {
            _bytes -= _groups.First.Value.Data.Length;
            _groups.RemoveFirst();
        }
        // Keep the newest group that started at least KeepSeconds ago, drop everything before it.
        while (_groups.First?.Next != null && now - _groups.First.Next.Value.Time >= KeepSeconds)
        {
            _bytes -= _groups.First.Value.Data.Length;
            _groups.RemoveFirst();
        }
    }

    /// <summary>
    /// The last <paramref name="seconds"/> of recording as a playable MPEG-TS file (starting at a keyframe, with the
    /// stream tables first). Returns the number of bytes written, or 0 if nothing has been recorded yet.
    /// </summary>
    public long SaveTo(Stream output, double seconds, double now)
    {
        lock (_gate)
        {
            if (_groups.Count == 0 || _pat == null || _pmt == null) return 0;
            // Start at the last keyframe at or before the cut-off, so the clip is at least as long as asked.
            var start = _groups.First;
            for (var g = _groups.First; g != null; g = g.Next)
                if (now - g.Value.Time >= seconds) start = g;
            output.Write(_pat);
            output.Write(_pmt);
            long n = 2 * PacketSize;
            for (var g = start; g != null; g = g.Next)
            {
                g.Value.Data.Position = 0;
                g.Value.Data.CopyTo(output);
                g.Value.Data.Position = g.Value.Data.Length;
                n += g.Value.Data.Length;
            }
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _groups.Clear();
            _current = null;
            _bytes = 0;
            _carryLen = 0;
        }
    }
}
