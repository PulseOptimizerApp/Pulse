using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Clips;

/// <summary>
/// Captures sound with Windows' own audio API (WASAPI): what the PC plays (loopback, the same as "Stereo Mix") or the
/// default microphone. Everything is turned into 48 kHz stereo float, the format FFmpeg is told to expect.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int Rate = 48000;
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    const int CLSCTX_ALL = 23;

    readonly bool _loopback;
    readonly object _gate = new();
    readonly Queue<float[]> _chunks = new();
    int _queued; // floats waiting
    Thread _thread;
    volatile bool _stop;
    public float Volume { get; set; } = 1f;
    public string Error { get; private set; }

    AudioCapture(bool loopback) => _loopback = loopback;

    public static AudioCapture GameSound() => new(true);
    public static AudioCapture Microphone() => new(false);

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = _loopback ? "Pulse game audio" : "Pulse mic", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Takes up to <paramref name="count"/> floats (interleaved stereo). Returns how many were copied.</summary>
    public int Read(float[] dest, int count)
    {
        lock (_gate)
        {
            int copied = 0;
            while (copied < count && _chunks.Count > 0)
            {
                var c = _chunks.Peek();
                int take = Math.Min(c.Length - _offset, count - copied);
                Array.Copy(c, _offset, dest, copied, take);
                copied += take;
                _offset += take;
                if (_offset >= c.Length) { _chunks.Dequeue(); _offset = 0; }
            }
            _queued -= copied;
            return copied;
        }
    }
    int _offset;

    /// <summary>Floats waiting to be read.</summary>
    public int Queued { get { lock (_gate) return _queued; } }

    /// <summary>Drops the oldest audio so the queue holds at most <paramref name="max"/> floats (keeps sound in sync).</summary>
    public void Trim(int max)
    {
        var scratch = new float[4096];
        while (Queued > max) Read(scratch, Math.Min(scratch.Length, Queued - max));
    }

    void Push(float[] f)
    {
        lock (_gate)
        {
            _chunks.Enqueue(f);
            _queued += f.Length;
            // Never hold more than 2 seconds: if nobody reads, old sound goes.
            while (_queued > Rate * 2 * 2 && _chunks.Count > 1) { _queued -= _chunks.Dequeue().Length - _offset; _offset = 0; }
        }
    }

    void Loop()
    {
        while (!_stop)
        {
            try { CaptureUntilLost(); }
            catch (Exception ex) { Error = ex.Message; }
            // The device went away (headset unplugged, default changed): wait a moment and pick up the new default.
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
        }
    }

    void CaptureUntilLost()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(_loopback ? 0 /*eRender*/ : 1 /*eCapture*/, 0 /*eConsole*/, out var device));
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var clientObj));
            var client = (IAudioClient)clientObj;
            Check(client.GetMixFormat(out var fmtPtr));
            try
            {
                var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(fmtPtr);
                bool isFloat = fmt.wFormatTag == 3 || (fmt.wFormatTag == 0xFFFE && Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(fmtPtr).SubFormat == FloatSubtype);
                int channels = fmt.nChannels, bits = fmt.wBitsPerSample, rate = (int)fmt.nSamplesPerSec;
                Check(client.Initialize(0 /*shared*/, _loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0, 2_000_000 /*200 ms*/, 0, fmtPtr, IntPtr.Zero));
                var ciid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref ciid, out var capObj));
                var capture = (IAudioCaptureClient)capObj;
                Check(client.Start());
                Error = null;
                var resampler = new Resampler(rate);
                try
                {
                    while (!_stop)
                    {
                        Thread.Sleep(5);
                        while (true)
                        {
                            int hr = capture.GetNextPacketSize(out var frames);
                            if (hr < 0) return; // device lost
                            if (frames == 0) break;
                            hr = capture.GetBuffer(out var data, out var n, out var flags, out _, out _);
                            if (hr < 0) return;
                            try
                            {
                                if (n > 0) Push(resampler.Process(data, (int)n, channels, bits, isFloat, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0, Volume));
                            }
                            finally { capture.ReleaseBuffer(n); }
                        }
                    }
                }
                finally
                {
                    client.Stop();
                    Marshal.ReleaseComObject(capture);
                    Marshal.ReleaseComObject(client);
                    Marshal.ReleaseComObject(device);
                }
            }
            finally { Marshal.FreeCoTaskMem(fmtPtr); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
    }

    // ───────── format conversion ─────────

    /// <summary>Any WASAPI format → 48 kHz stereo float. Takes the first two channels (front left/right); mono is duplicated.</summary>
    internal sealed class Resampler
    {
        readonly double _step;
        double _pos;          // position in the input stream, in input frames, relative to _prev
        float _prevL, _prevR; // last input frame of the previous packet (for interpolation across packets)

        public Resampler(int inRate) => _step = inRate / (double)Rate;

        public unsafe float[] Process(IntPtr data, int frames, int channels, int bits, bool isFloat, bool silent, float volume)
        {
            var l = new float[frames];
            var r = new float[frames];
            if (!silent)
            {
                for (int i = 0; i < frames; i++)
                {
                    l[i] = Sample(data, i * channels, bits, isFloat) * volume;
                    r[i] = channels > 1 ? Sample(data, i * channels + 1, bits, isFloat) * volume : l[i];
                }
            }
            return Convert(l, r);
        }

        /// <summary>Linear interpolation from the input rate to 48 kHz. Exposed for tests.</summary>
        internal float[] Convert(float[] l, float[] r)
        {
            int frames = l.Length;
            if (Math.Abs(_step - 1) < 1e-9)
            {
                var same = new float[frames * 2];
                for (int i = 0; i < frames; i++) { same[2 * i] = l[i]; same[2 * i + 1] = r[i]; }
                if (frames > 0) { _prevL = l[^1]; _prevR = r[^1]; }
                return same;
            }
            var output = new List<float>((int)(frames / _step * 2) + 4);
            // Input index -1 is the previous packet's last frame.
            while (_pos < frames - 1 + 1e-9)
            {
                int i0 = (int)Math.Floor(_pos);
                double t = _pos - i0;
                float l0 = i0 < 0 ? _prevL : l[i0], r0 = i0 < 0 ? _prevR : r[i0];
                float l1 = l[Math.Min(i0 + 1, frames - 1)], r1 = r[Math.Min(i0 + 1, frames - 1)];
                output.Add((float)(l0 + (l1 - l0) * t));
                output.Add((float)(r0 + (r1 - r0) * t));
                _pos += _step;
            }
            _pos -= frames; // next packet's index 0 follows this packet's last frame (index frames-1 → -1)
            if (frames > 0) { _prevL = l[^1]; _prevR = r[^1]; }
            return output.ToArray();
        }

        static unsafe float Sample(IntPtr data, int index, int bits, bool isFloat)
        {
            byte* p = (byte*)data;
            if (isFloat) return ((float*)p)[index];
            return bits switch
            {
                16 => ((short*)p)[index] / 32768f,
                24 => ((p[index * 3] | (p[index * 3 + 1] << 8) | ((sbyte)p[index * 3 + 2] << 16))) / 8388608f,
                32 => ((int*)p)[index] / 2147483648f,
                _ => 0f,
            };
        }
    }

    // ───────── COM ─────────

    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
