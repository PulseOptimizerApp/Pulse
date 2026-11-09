namespace Pulse.Core.Clips;

/// <summary>
/// Where the replay's bytes live: fixed 64 KB blocks that are reused, either in memory or in one temporary file
/// (deleted when it's closed). Reusing same-size blocks keeps memory flat: growing buffers used to hold up to twice
/// the replay and left big freed arrays behind for the garbage collector.
/// </summary>
public sealed class ChunkStore : IDisposable
{
    public const int Size = 64 * 1024; // under the large-object threshold, so blocks never fragment memory
    readonly List<byte[]> _mem = new();
    readonly Stack<int> _free = new();
    FileStream _file;
    int _count;

    public bool OnDisk => _file != null;

    /// <summary>A store in memory, or (when <paramref name="tempFile"/> is given) in that file on disk.</summary>
    public ChunkStore(string tempFile = null)
    {
        if (tempFile == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        _file = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    }

    public int Alloc()
    {
        if (_free.Count > 0) return _free.Pop();
        if (_file == null) _mem.Add(new byte[Size]);
        return _count++;
    }

    public void Free(int id) => _free.Push(id);

    public void Write(int id, byte[] data, int length)
    {
        if (_file == null) { Buffer.BlockCopy(data, 0, _mem[id], 0, length); return; }
        _file.Position = (long)id * Size;
        _file.Write(data, 0, length);
    }

    public void CopyTo(int id, int length, Stream output, byte[] scratch)
    {
        if (_file == null) { output.Write(_mem[id], 0, length); return; }
        _file.Position = (long)id * Size;
        _file.ReadExactly(scratch, 0, length);
        output.Write(scratch, 0, length);
    }

    /// <summary>Bytes this store keeps in Pulse's memory.</summary>
    public long MemoryBytes => _file == null ? (long)_mem.Count * Size : 0;

    public void Dispose() { try { _file?.Dispose(); } catch { } _file = null; _mem.Clear(); _free.Clear(); }
}

/// <summary>
/// The instant-replay buffer. FFmpeg streams the encoded recording as MPEG-TS (188-byte packets); this keeps the last
/// N seconds, split into groups that each start at a video keyframe, so a saved clip always starts cleanly. The bytes
/// are kept in a <see cref="ChunkStore"/> (memory, or a temporary file on disk).
/// </summary>
public sealed class TsRing : IDisposable
{
    public const int PacketSize = 188;
    public const int VideoPid = 0x100;   // FFmpeg's first stream; Pulse always maps video first
    const int PatPid = 0;

    sealed class Group
    {
        public double Time;                    // seconds on the ring's clock when the keyframe arrived
        public readonly List<(int Id, int Length)> Chunks = new();
        public long Length;
    }

    ChunkStore _store = new();
    readonly byte[] _stage = new byte[ChunkStore.Size]; // the newest block, filled before it's stored
    readonly byte[] _scratch = new byte[ChunkStore.Size];
    int _stageLen;

    /// <summary>Keeps the replay in a temporary file on disk instead of memory (Pulse then uses almost no memory for it).</summary>
    public void UseDisk(string tempFile)
    {
        lock (_gate)
        {
            Clear();
            _store.Dispose();
            try { _store = new ChunkStore(tempFile); }
            catch { _store = new ChunkStore(); } // no room or no access: memory still works
        }
    }

    public bool OnDisk { get { lock (_gate) return _store.OnDisk; } }
    /// <summary>Bytes the replay takes in Pulse's memory (blocks in memory, or just the newest block when on disk).</summary>
    public long MemoryBytes { get { lock (_gate) return _store.MemoryBytes + _stage.Length * 2; } }

    void Append(ReadOnlySpan<byte> p)
    {
        int i = 0;
        while (i < p.Length)
        {
            int take = Math.Min(p.Length - i, _stage.Length - _stageLen);
            p.Slice(i, take).CopyTo(_stage.AsSpan(_stageLen));
            _stageLen += take;
            i += take;
            if (_stageLen == _stage.Length) Flush();
        }
        _current.Length += p.Length;
    }

    /// <summary>Stores the newest block in the current group.</summary>
    void Flush()
    {
        if (_stageLen == 0 || _current == null) { _stageLen = 0; return; }
        int id = _store.Alloc();
        try { _store.Write(id, _stage, _stageLen); }
        catch
        {
            // The disk filled up or went away: carry on in memory (the replay so far is dropped).
            _store.Dispose();
            _store = new ChunkStore();
            foreach (var g in _groups) { g.Chunks.Clear(); g.Length = 0; }
            _groups.Clear();
            _current = null;
            _bytes = 0;
            _stageLen = 0;
            return;
        }
        _current.Chunks.Add((id, _stageLen));
        _stageLen = 0;
    }

    void Drop(Group g)
    {
        foreach (var (id, _) in g.Chunks) _store.Free(id);
        _bytes -= g.Length;
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
    // Long recordings being written to disk as they happen (the session hotkey, a VALORANT match kept for highlights).
    // Each starts at the next keyframe so its file plays from its first frame.
    sealed class Tap { public Stream Out; public bool WaitsForKey = true; public double FirstKey = -1; }
    readonly List<Tap> _taps = new();

    /// <summary>
    /// Also writes everything from the next keyframe on to <paramref name="output"/> (a long recording), until
    /// <see cref="StopTap"/>. Writes go straight to the file, so memory use doesn't grow with the recording's length.
    /// </summary>
    public void StartTap(Stream output)
    {
        lock (_gate)
        {
            _taps.Add(new Tap { Out = output });
            if (_pat != null) output.Write(_pat);
            if (_pmt != null) output.Write(_pmt);
        }
    }

    /// <summary>Stops the oldest long recording and returns its stream (the caller closes it).</summary>
    public Stream StopTap()
    {
        lock (_gate)
        {
            if (_taps.Count == 0) return null;
            var t = _taps[0];
            _taps.RemoveAt(0);
            return t.Out;
        }
    }

    /// <summary>
    /// Stops the long recording writing to <paramref name="output"/>. Returns the ring-clock time of its first keyframe
    /// (where its file starts), or -1 if nothing was written.
    /// </summary>
    public double StopTap(Stream output)
    {
        lock (_gate)
        {
            var t = _taps.FirstOrDefault(x => x.Out == output);
            if (t == null) return -1;
            _taps.Remove(t);
            return t.FirstKey;
        }
    }

    /// <summary>The ring-clock time where the clip <see cref="SaveTo"/> would start (its first keyframe), or -1.</summary>
    public double StartTimeFor(double seconds, double now)
    {
        lock (_gate)
        {
            if (_groups.Count == 0) return -1;
            double start = _groups.First.Value.Time;
            foreach (var g in _groups) if (now - g.Time >= seconds) start = g.Time;
            return start;
        }
    }

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
        bool key = false;
        if (pid == VideoPid)
        {
            _lastVideo = now;
            if (IsKeyframeStart(p)) { key = true; _lastKeyframe = now; Flush(); _groups.AddLast(_current = new Group { Time = now }); }
        }
        for (int t = _taps.Count - 1; t >= 0; t--)
        {
            var tap = _taps[t];
            if (key && tap.WaitsForKey) { tap.WaitsForKey = false; tap.FirstKey = now; }
            if (!tap.WaitsForKey)
                try { tap.Out.Write(p); } catch { _taps.RemoveAt(t); } // disk full or removed: the replay keeps working
        }
        if (_current == null) return; // nothing useful before the first keyframe
        // No keyframe for a long time means the picture stopped: don't keep piling sound onto the last frame,
        // or a saved clip becomes minutes of a frozen picture.
        if (pid != VideoPid && now - _current.Time > StaleGroupSeconds) return;
        Append(p);
        if (_current == null) return; // the store failed and was reset
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
            Drop(_groups.First.Value);
            _groups.RemoveFirst();
        }
        // Keep the newest group that started at least KeepSeconds ago, drop everything before it.
        while (_groups.First?.Next != null && now - _groups.First.Next.Value.Time >= KeepSeconds)
        {
            Drop(_groups.First.Value);
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
                foreach (var (id, len) in g.Value.Chunks) _store.CopyTo(id, len, output, _scratch);
                if (g.Value == _current && _stageLen > 0) output.Write(_stage, 0, _stageLen);
                n += g.Value.Length;
            }
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var g in _groups) foreach (var (id, _) in g.Chunks) _store.Free(id);
            _groups.Clear();
            _current = null;
            _bytes = 0;
            _carryLen = 0;
            _stageLen = 0;
        }
    }

    public void Dispose() { lock (_gate) { Clear(); _store.Dispose(); } }
}
