using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Clips;

/// <summary>
/// Plays a clip's sound tracks for the editor, mixed by Pulse itself and locked to the video's position, so any
/// source (game, Discord, music, microphone) can be muted or brought back instantly while you watch, scrub or loop.
/// The video player stays silent while this runs.
///
/// Each track is raw 16-bit stereo at 48 kHz in its own file (made by <see cref="ClipLibrary.ExtractTracks"/>) and is
/// read from disk as it plays, so a long recording doesn't fill memory. Output is Windows' waveOut: four 40 ms
/// buffers, refilled as each one finishes.
/// </summary>
public sealed class ClipMixer : IDisposable
{
    public const int Rate = 48000;
    const int Channels = 2, BytesPerFrame = 4;
    const int BufferMs = 40, Buffers = 4;
    const int BufferFrames = Rate * BufferMs / 1000;
    const double ResyncSeconds = 0.06; // drift allowed before the sound jumps to where the picture is

    readonly FileStream[] _tracks;
    readonly long[] _frames;
    readonly object _gate = new();
    readonly Thread _thread;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop;
    int[] _chosen = { 0 };
    double _videoPos, _videoAt; // the picture's position, and when it was reported (on _clock)
    bool _playing;
    double _cursor;              // seconds into the clip of the next sound written
    IntPtr _wave;
    readonly IntPtr[] _headers = new IntPtr[Buffers], _data = new IntPtr[Buffers];

    /// <summary>True when Windows accepted the output; false means fall back to the video player's own sound.</summary>
    public bool Ready { get; }

    public ClipMixer(IReadOnlyList<string> trackFiles)
    {
        _tracks = trackFiles.Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess)).ToArray();
        _frames = _tracks.Select(t => t.Length / BytesPerFrame).ToArray();
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, wBitsPerSample = 16,
            nBlockAlign = BytesPerFrame, nAvgBytesPerSec = Rate * BytesPerFrame,
        };
        Ready = waveOutOpen(out _wave, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) == 0;
        if (!Ready) return;
        for (int i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferFrames * BytesPerFrame);
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            for (int b = 0; b < HeaderSize; b += 8) Marshal.WriteInt64(_headers[i], b, 0);
            Marshal.WriteIntPtr(_headers[i], 0, _data[i]);
            Marshal.WriteInt32(_headers[i], 8, BufferFrames * BytesPerFrame);
            waveOutPrepareHeader(_wave, _headers[i], HeaderSize);
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulse clip sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Which tracks to hear, mixed (0 = everything as recorded, 1+ = the separate sources). Empty = silence.</summary>
    public void SetChosen(IReadOnlyList<int> tracks)
    {
        lock (_gate) _chosen = tracks.Where(t => t >= 0 && t < _tracks.Length).ToArray();
    }

    /// <summary>The picture's position and whether it's playing (call often, e.g. from the player's timer).</summary>
    public void Sync(double seconds, bool playing)
    {
        lock (_gate)
        {
            _videoPos = seconds;
            _videoAt = _clock.Elapsed.TotalSeconds;
            _playing = playing;
        }
    }

    void Loop()
    {
        var mix = new int[BufferFrames * Channels];
        var read = new byte[BufferFrames * BytesPerFrame];
        var out16 = new short[BufferFrames * Channels];
        // Prime every buffer, then refill each one as soon as Windows has played it.
        for (int i = 0; i < Buffers && !_stop; i++) { Fill(i, mix, read, out16, ahead: i * BufferMs / 1000.0); waveOutWrite(_wave, _headers[i], HeaderSize); }
        while (!_stop)
        {
            bool wrote = false;
            for (int i = 0; i < Buffers && !_stop; i++)
            {
                if ((Marshal.ReadInt32(_headers[i], FlagsOffset) & WHDR_DONE) == 0) continue;
                // This buffer is heard after the ones still queued ahead of it.
                Fill(i, mix, read, out16, ahead: (Buffers - 1) * BufferMs / 1000.0);
                Marshal.WriteInt32(_headers[i], FlagsOffset, Marshal.ReadInt32(_headers[i], FlagsOffset) & ~WHDR_DONE);
                waveOutWrite(_wave, _headers[i], HeaderSize);
                wrote = true;
            }
            if (!wrote) Thread.Sleep(4);
        }
    }

    void Fill(int index, int[] mix, byte[] read, short[] out16, double ahead)
    {
        int[] chosen; bool playing; double target;
        lock (_gate)
        {
            chosen = _chosen;
            playing = _playing;
            target = _videoPos + (playing ? _clock.Elapsed.TotalSeconds - _videoAt : 0) + ahead;
        }
        Array.Clear(mix);
        if (playing && chosen.Length > 0)
        {
            if (Math.Abs(_cursor - target) > ResyncSeconds) _cursor = target; // seek, loop or drift: follow the picture
            long first = (long)Math.Round(_cursor * Rate);
            foreach (var t in chosen)
            {
                if (first < 0 || first >= _frames[t]) continue;
                int frames = (int)Math.Min(BufferFrames, _frames[t] - first);
                var f = _tracks[t];
                f.Position = first * BytesPerFrame;
                int got = 0;
                while (got < frames * BytesPerFrame)
                {
                    int n = f.Read(read, got, frames * BytesPerFrame - got);
                    if (n <= 0) break;
                    got += n;
                }
                for (int s = 0; s < got / 2; s++) mix[s] += (short)(read[2 * s] | (read[2 * s + 1] << 8));
            }
            _cursor += BufferMs / 1000.0;
        }
        else _cursor = target; // paused or everything muted: silence, but stay with the picture
        for (int s = 0; s < mix.Length; s++) out16[s] = (short)Math.Clamp(mix[s], short.MinValue, short.MaxValue);
        Marshal.Copy(out16, 0, _data[index], out16.Length);
    }

    /// <summary>Mixes 16-bit samples the way playback does (summed, clipped). For tests.</summary>
    internal static short[] MixForTest(params short[][] tracks)
    {
        int n = tracks.Max(t => t.Length);
        var r = new short[n];
        for (int i = 0; i < n; i++) r[i] = (short)Math.Clamp(tracks.Sum(t => i < t.Length ? t[i] : 0), short.MinValue, short.MaxValue);
        return r;
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(500); } catch { }
        if (_wave != IntPtr.Zero)
        {
            waveOutReset(_wave);
            for (int i = 0; i < Buffers; i++)
            {
                if (_headers[i] == IntPtr.Zero) continue;
                waveOutUnprepareHeader(_wave, _headers[i], HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
                _headers[i] = _data[i] = IntPtr.Zero;
            }
            waveOutClose(_wave);
            _wave = IntPtr.Zero;
        }
        foreach (var t in _tracks) t.Dispose();
    }

    // ───────── waveOut ─────────

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    // WAVEHDR on 64-bit: lpData(8) dwBufferLength(4) dwBytesRecorded(4) dwUser(8) dwFlags(4) dwLoops(4) lpNext(8) reserved(8)
    static readonly int HeaderSize = IntPtr.Size == 8 ? 48 : 32;
    static readonly int FlagsOffset = IntPtr.Size == 8 ? 24 : 16;
    const int WHDR_DONE = 1;

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
}
