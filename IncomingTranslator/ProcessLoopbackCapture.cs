using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace IncomingTranslator;

/// <summary>
/// Windows application loopback, restricted to one process and its children.
/// This never falls back to an endpoint's all-application loopback stream.
/// https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = typeof(IAudioClient).GUID;
    private readonly object _gate = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private CancellationTokenSource? _stopCancellation;
    private Thread? _thread;
    private TaskCompletionSource<bool> _startup = NewCompletion();
    private WaveFormat _waveFormat = new(44100, 16, 2);
    private bool _disposed;
    private int _waitHandlesDisposed;
    private long _capturedBytes;

    public ProcessLoopbackCapture(int processId)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Application-only audio capture requires Windows build 20348 or newer. No desktop-audio fallback is enabled.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        TargetProcessId = processId;
    }

    public static bool IsSupported => OperatingSystem.IsWindows() && IncomingCaptureCapabilities.SupportsProcessLoopback(IncomingCaptureCapabilities.CurrentVersion);
    public int TargetProcessId { get; }
    public long CapturedBytes => Interlocked.Read(ref _capturedBytes);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    // The official sample requests stereo PCM16/44.1 kHz with AutoConvertPcm.
    // A known format is important: process-loopback does not depend on a device's mix format.
    public WaveFormat WaveFormat
    {
        get { lock (_gate) return _waveFormat; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_thread is not null) throw new InvalidOperationException("Stop capture before changing its format.");
                if (value.Encoding != WaveFormatEncoding.Pcm || value.BitsPerSample != 16 || value.Channels != 2)
                    throw new ArgumentException("Process loopback expects stereo 16-bit PCM.", nameof(value));
                _waveFormat = value;
            }
        }
    }

    public void StartRecording()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) throw new InvalidOperationException("Process audio capture is already running.");
            _startup = NewCompletion();
            _stopEvent.Reset();
            _stopCancellation = new CancellationTokenSource();
            CancellationTokenSource cancellation = _stopCancellation;
            WaveFormat format = _waveFormat;
            _thread = new Thread(() => CaptureThread(format, cancellation))
            {
                IsBackground = true,
                Name = "Selected application audio capture"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            try { _thread.Start(); }
            catch
            {
                _thread = null;
                _stopCancellation = null;
                cancellation.Dispose();
                throw;
            }
        }
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        StartRecording();
        Task ready;
        lock (_gate) ready = _startup.Task;
        try { await ready.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { StopRecording(); throw; }
    }

    public void StopRecording()
    {
        lock (_gate)
        {
            if (_thread is null) return;
            _stopCancellation?.Cancel();
            _stopEvent.Set();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        StopRecording();
        // Do not release handles or COM objects underneath a still-running capture callback.
        // The worker owns COM cleanup and also finishes handle disposal after a bounded join.
        if (thread is not null && Thread.CurrentThread != thread)
            thread.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            if (_thread is null) DisposeWaitHandle();
        }
        GC.SuppressFinalize(this);
    }

    public static int? TryFindVrChatProcessId()
    {
        foreach (Process process in Process.GetProcessesByName("VRChat"))
        {
            using (process)
            {
                try { if (!process.HasExited) return process.Id; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return null;
    }

    /// <summary>Short local diagnostic; measures levels without storing any captured speech.</summary>
    public static async Task<string> ProbeAsync(int processId, int milliseconds = 2000, CancellationToken cancellationToken = default)
    {
        if (milliseconds < 100 || milliseconds > 10000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        using var capture = new ProcessLoopbackCapture(processId);
        object probeGate = new();
        long samples = 0;
        double squares = 0;
        double peak = 0;
        Exception? stoppedFailure = null;
        capture.DataAvailable += (_, args) =>
        {
            lock (probeGate)
            {
                for (int index = 0; index + 1 < args.BytesRecorded; index += 2)
                {
                    double sample = BitConverter.ToInt16(args.Buffer, index) / 32768.0;
                    squares += sample * sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                    samples++;
                }
            }
        };
        capture.RecordingStopped += (_, args) => { lock (probeGate) stoppedFailure = args.Exception; };
        await capture.StartRecordingAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        capture.StopRecording();
        lock (probeGate)
        {
            if (stoppedFailure is not null) throw new InvalidOperationException("Process-only capture probe failed.", stoppedFailure);
            return FormattableString.Invariant($"process={processId}, format={capture.WaveFormat}, bytes={capture.CapturedBytes}, rms={(samples == 0 ? 0 : Math.Sqrt(squares / samples)):0.000000}, peak={peak:0.000000}");
        }
    }

    private void CaptureThread(WaveFormat format, CancellationTokenSource cancellation)
    {
        Exception? failure = null;
        AudioClient? client = null;
        IAudioClient? rawClient = null;
        AutoResetEvent? samplesReady = null;
        bool comInitialized = false;
        bool started = false;
        try
        {
            int comResult = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
            Marshal.ThrowExceptionForHR(comResult);
            comInitialized = true;
            using Process process = Process.GetProcessById(TargetProcessId);
            if (process.HasExited) throw new InvalidOperationException("The selected application has exited. Waiting for it to start again.");

            var activation = new ActivationHandler(TargetProcessId);
            try
            {
                activation.Begin();
                activation.Completion.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token).GetAwaiter().GetResult();
                rawClient = activation.TakeClient();
            }
            catch
            {
                // Activation itself cannot be canceled. A late callback retains its native
                // parameters and disposes any eventual result, rather than using freed memory.
                activation.Abandon();
                throw;
            }
            client = new AudioClient(rawClient);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                0, 0, format, Guid.Empty);
            samplesReady = new AutoResetEvent(false);
            client.SetEventHandle(samplesReady.SafeWaitHandle.DangerousGetHandle());
            AudioCaptureClient capture = client.AudioCaptureClient;
            byte[] buffer = new byte[Math.Max(format.BlockAlign, checked(client.BufferSize * format.BlockAlign))];
            WaitHandle[] waits = { _stopEvent, samplesReady };
            cancellation.Token.ThrowIfCancellationRequested();
            client.Start();
            started = true;
            _startup.TrySetResult(true);
            long lastProcessCheck = Environment.TickCount64;

            while (!cancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 250) == 0) break;
                if (Environment.TickCount64 - lastProcessCheck >= 500)
                {
                    lastProcessCheck = Environment.TickCount64;
                    process.Refresh();
                    if (process.HasExited)
                        throw new InvalidOperationException("The selected application exited. Capture must reconnect to its new process when it restarts.");
                }
                while (!cancellation.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = checked(frames * format.BlockAlign);
                    try
                    {
                        if (bytes > buffer.Length) Array.Resize(ref buffer, bytes);
                        if ((flags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero)
                            Array.Clear(buffer, 0, bytes);
                        else Marshal.Copy(data, buffer, 0, bytes);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    if (bytes == 0) continue;
                    Interlocked.Add(ref _capturedBytes, bytes);
                    // Like NAudio's own capture, the buffer is reused after this synchronous event.
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _startup.TrySetCanceled(cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
            _startup.TrySetException(exception);
        }
        finally
        {
            try { if (started) client?.Stop(); } catch { }
            try
            {
                if (client is not null) client.Dispose();
                else if (rawClient is not null) ReleaseCom(rawClient);
            }
            catch { }
            // Windows retains the sample-ready handle while the audio client exists.
            // Close it only after stopping and releasing that client, not on leaving try.
            samplesReady?.Dispose();
            if (comInitialized) CoUninitialize();
            lock (_gate)
            {
                _thread = null;
                _stopCancellation = null;
                if (_disposed) DisposeWaitHandle();
            }
            cancellation.Dispose();
            // Do not marshal this to the UI: the existing capture supervisor owns its dispatch.
            try { RecordingStopped?.Invoke(this, new StoppedEventArgs(failure)); } catch { }
        }
    }

    private void DisposeWaitHandle()
    {
        if (Interlocked.Exchange(ref _waitHandlesDisposed, 1) == 0) _stopEvent.Dispose();
    }

    private static TaskCompletionSource<bool> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ReleaseCom(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    // Public COM interfaces are supplied by NAudio; IAgileObject must also be exposed so
    // Windows can invoke the completion handler on its own MTA worker without an STA hop.
    [ComVisible(true), ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _completion = NewCompletion();
        private IntPtr _parameters;
        private IntPtr _variant;
        private IActivateAudioInterfaceAsyncOperation? _operation;
        private IAudioClient? _client;
        private bool _completed;
        private bool _abandoned;

        public ActivationHandler(int processId)
        {
            var parameters = new AudioClientActivationParameters
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = checked((uint)processId),
                ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            };
            _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParameters>());
            Marshal.StructureToPtr(parameters, _parameters, false);
            var variant = new BlobPropVariant
            {
                VariantType = 65, // VT_BLOB
                Blob = new NativeBlob { Size = checked((uint)Marshal.SizeOf<AudioClientActivationParameters>()), Data = _parameters }
            };
            _variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
            Marshal.StructureToPtr(variant, _variant, false);
        }

        public Task Completion => _completion.Task;

        public void Begin()
        {
            try
            {
                Guid iid = AudioClientId;
                int result = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, _variant, this, out var operation);
                Marshal.ThrowExceptionForHR(result);
                lock (_gate)
                {
                    // The callback is allowed to complete before this function returns.
                    if (_completed) ReleaseCom(operation);
                    else _operation = operation;
                }
                GC.KeepAlive(this);
            }
            catch
            {
                lock (_gate)
                {
                    _completed = true;
                    FreeParameters();
                }
                throw;
            }
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Exception? failure = null;
            object? result = null;
            try
            {
                operation.GetActivateResult(out int activationResult, out result);
                Marshal.ThrowExceptionForHR(activationResult);
                if (result is not IAudioClient client)
                    throw new InvalidOperationException("Windows did not return a process audio capture client.");
                lock (_gate)
                {
                    if (_abandoned) ReleaseCom(client);
                    else _client = client;
                    result = null; // Ownership moved to the pending result, or was released.
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                if (result is not null) { try { ReleaseCom(result); } catch { } }
            }
            finally
            {
                lock (_gate)
                {
                    _completed = true;
                    FreeParameters();
                    if (_operation is not null)
                    {
                        try { ReleaseCom(_operation); } catch { }
                        _operation = null;
                    }
                }
                if (failure is null) _completion.TrySetResult(true);
                else _completion.TrySetException(failure);
            }
        }

        public IAudioClient TakeClient()
        {
            lock (_gate)
            {
                IAudioClient client = _client ?? throw new InvalidOperationException("Process audio activation was canceled or returned no client.");
                _client = null;
                return client;
            }
        }

        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                if (_client is not null)
                {
                    try { ReleaseCom(_client); } catch { }
                    _client = null;
                }
                // Keep activation allocations alive until ActivateCompleted. The Windows
                // operation retains this COM callback even after the capture worker exits.
            }
        }

        private void FreeParameters()
        {
            if (_variant != IntPtr.Zero) Marshal.FreeHGlobal(_variant);
            if (_parameters != IntPtr.Zero) Marshal.FreeHGlobal(_parameters);
            _variant = _parameters = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParameters
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public NativeBlob Blob;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid iid,
        IntPtr activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
