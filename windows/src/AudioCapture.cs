using System.Runtime.InteropServices;

namespace Rewind;

sealed class AudioCapture : IDisposable
{
    public enum Source { SystemLoopback, Microphone, Process }

    readonly Source source;
    readonly uint pid;
    readonly Action<AudioChunk> onChunk;
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new();
    volatile bool stop;
    Exception? startError;

    public string Description => source switch
    {
        Source.SystemLoopback => "system audio",
        Source.Microphone => "microphone",
        _ => $"app audio (pid {pid})",
    };

    AudioCapture(Source source, uint pid, Action<AudioChunk> onChunk)
    {
        this.source = source; this.pid = pid; this.onChunk = onChunk;
        thread = new Thread(Run) { IsBackground = true, Name = "Rewind audio " + source, Priority = ThreadPriority.Highest };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    public static AudioCapture Start(Source source, uint pid, Action<AudioChunk> onChunk)
    {
        var c = new AudioCapture(source, pid, onChunk);
        c.thread.Start();
        if (!c.ready.Wait(TimeSpan.FromSeconds(5))) { c.Dispose(); throw new RewindException($"Opening the {c.Description} timed out."); }
        if (c.startError != null) { var e = c.startError; c.Dispose(); throw e; }
        return c;
    }

    public void Dispose()
    {
        stop = true;
        if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(2000);
    }


    sealed class Stream : IDisposable
    {
        public IAudioClient Client = null!;
        public IAudioCaptureClient Capture = null!;
        public EventWaitHandle Event = new AutoResetEvent(false);
        public string? DeviceId;
        public bool Pcm16;

        public void Dispose()
        {
            try { Client?.Stop(); } catch { }
            if (Capture != null) Marshal.ReleaseComObject(Capture);
            if (Client != null) Marshal.ReleaseComObject(Client);
            Event.Dispose();
        }
    }

    void Run()
    {
        Stream? s = null;
        try { s = Open(); }
        catch (Exception e) { startError = e; ready.Set(); return; }
        ready.Set();

        long nextDeviceCheck = Environment.TickCount64 + 2000;
        while (!stop)
        {
            try
            {
                if (s == null)
                {
                    Thread.Sleep(1000);
                    s = Open();
                    Log.Info($"reopened {Description}");
                }
                s.Event.WaitOne(100);
                Read(s);

                if (source != Source.Process && Environment.TickCount64 > nextDeviceCheck)
                {
                    nextDeviceCheck = Environment.TickCount64 + 2000;
                    if (DefaultDeviceId() is { } id && id != s.DeviceId)
                    {
                        Log.Info($"{Description}: default device changed");
                        s.Dispose(); s = null;
                        s = Open();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"{Description} capture", e);
                s?.Dispose(); s = null;
            }
        }
        s?.Dispose();
    }

    void Read(Stream s)
    {
        while (true)
        {
            Check(s.Capture.GetNextPacketSize(out uint packet));
            if (packet == 0) return;
            int hr = s.Capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc);
            if (hr == AUDCLNT_S_BUFFER_EMPTY) return;
            Check(hr);
            var samples = new float[frames * 2];
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && frames > 0)
            {
                if (s.Pcm16)
                {
                    unsafe
                    {
                        short* p = (short*)data;
                        for (int i = 0; i < samples.Length; i++) samples[i] = p[i] / 32768f;
                    }
                }
                else Marshal.Copy(data, samples, 0, samples.Length);
            }
            s.Capture.ReleaseBuffer(frames);
            long pts = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0 || qpc == 0
                ? Clock.Now - frames * Clock.Second / Audio.Rate
                : (long)qpc;
            if (frames > 0) onChunk(new AudioChunk(pts, samples));
        }
    }

    Stream Open()
    {
        var s = new Stream();
        try
        {
            if (source == Source.Process)
            {
                s.Client = ActivateProcessLoopback(pid);
                if (!TryInit(s, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, float32: true) &&
                    !TryInit(s, LOOPBACK | EVENTCALLBACK, float32: true) &&
                    !TryInit(s, LOOPBACK | EVENTCALLBACK, float32: false))
                    throw new RewindException("Couldn't open the app's audio.");
            }
            else
            {
                var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
                try
                {
                    int flow = source == Source.Microphone ? eCapture : eRender;
                    int hr = enumerator.GetDefaultAudioEndpoint(flow, eConsole, out var device);
                    if (hr == E_NOTFOUND || device == null)
                        throw new RewindException(source == Source.Microphone ? "No microphone was found." : "No audio output device was found.", hr);
                    Check(hr);
                    try
                    {
                        device.GetId(out var idPtr);
                        s.DeviceId = Marshal.PtrToStringUni(idPtr);
                        Marshal.FreeCoTaskMem(idPtr);
                        Guid iid = IID_IAudioClient;
                        hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                        if (hr == E_ACCESSDENIED) throw MicDenied(hr);
                        Check(hr);
                        s.Client = (IAudioClient)obj;
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                finally { Marshal.ReleaseComObject(enumerator); }

                uint flags = EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY | (source == Source.SystemLoopback ? LOOPBACK : 0);
                if (!TryInit(s, flags, float32: true, out int initHr))
                {
                    if (initHr == E_ACCESSDENIED) throw MicDenied(initHr);
                    Check(initHr);
                }
            }

            Check(s.Client.SetEventHandle(s.Event.SafeWaitHandle.DangerousGetHandle()));
            Guid cap = IID_IAudioCaptureClient;
            Check(s.Client.GetService(ref cap, out var capObj));
            s.Capture = (IAudioCaptureClient)capObj;
            Check(s.Client.Start());
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    RewindException MicDenied(int hr) => source == Source.Microphone
        ? new RewindException("Microphone access is off for desktop apps.", hr)
        : new RewindException("Windows denied access to the audio device.", hr);

    bool TryInit(Stream s, uint flags, bool float32) => TryInit(s, flags, float32, out _);

    static bool TryInit(Stream s, uint flags, bool float32, out int hr)
    {
        var fmt = new WAVEFORMATEXTENSIBLE
        {
            wFormatTag = 0xFFFE, nChannels = 2, nSamplesPerSec = Audio.Rate,
            wBitsPerSample = (ushort)(float32 ? 32 : 16), cbSize = 22,
            wValidBitsPerSample = (ushort)(float32 ? 32 : 16), dwChannelMask = 3,
            SubFormat = float32 ? KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM,
        };
        fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
        fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
        try
        {
            Marshal.StructureToPtr(fmt, p, false);
            hr = s.Client.Initialize(0 /* shared */, flags, 2_000_000 /* 200 ms */, 0, p, IntPtr.Zero);
            s.Pcm16 = !float32;
            return hr >= 0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static string? DefaultDeviceIdFor(int flow)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, eConsole, out var device) < 0 || device == null) return null;
                try
                {
                    device.GetId(out var idPtr);
                    var id = Marshal.PtrToStringUni(idPtr);
                    Marshal.FreeCoTaskMem(idPtr);
                    return id;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch { return null; }
    }

    string? DefaultDeviceId() => DefaultDeviceIdFor(source == Source.Microphone ? eCapture : eRender);

    public static bool HasMicrophone() => DefaultDeviceIdFor(eCapture) != null;


    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [ComVisible(true)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    static IAudioClient ActivateProcessLoopback(uint pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr pVar = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB
            {
                vt = 65 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(), pBlobData = pParams,
            }, pVar, false);
            var handler = new ActivationHandler();
            Guid iid = IID_IAudioClient;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pVar, handler, out var op));
            try
            {
                if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new RewindException("Activating app audio timed out.");
                Check(op.GetActivateResult(out int hr, out var obj));
                Check(hr);
                return (IAudioClient)obj;
            }
            finally { Marshal.ReleaseComObject(op); }
        }
        finally
        {
            Marshal.FreeHGlobal(pVar);
            Marshal.FreeHGlobal(pParams);
        }
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }


    const int eRender = 0, eCapture = 1, eConsole = 0;
    const uint CLSCTX_ALL = 0x17;
    const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;
    const int E_NOTFOUND = unchecked((int)0x80070490), E_ACCESSDENIED = unchecked((int)0x80070005);
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WAVEFORMATEXTENSIBLE
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
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

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
}
