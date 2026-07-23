using NAudio.Dsp;
using NAudio.Wave;

namespace TaskbarMusic.Services;

/// <summary>
/// Captures system audio (WASAPI loopback), runs an FFT, and exposes a small set
/// of smoothed frequency bands (0..1) plus an overall level. Capture data is
/// computed into a private buffer and atomically published to UI readers. If
/// capture can't start it produces silence and the UI uses its idle wave.
/// </summary>
internal sealed class AudioVisualizer : IDisposable
{
    public const int BandCount = 24;
    private const int FftLen = 1024;   // must be 2^M
    private const int M = 10;

    private readonly Complex[] _fft = new Complex[FftLen];
    private int _pos;

    private readonly object _lifecycleLock = new();
    private readonly object _processingLock = new();
    private WasapiLoopbackCapture? _capture;
    private float[] _publishedBands = new float[BandCount];
    private float[] _workingBands = new float[BandCount];
    private float _level;
    private int _active;
    private int _disposed;
    private long _lifecycleVersion;

    // Return a snapshot so callers cannot retain a buffer that the capture
    // thread will reuse on a later FFT cycle.
    public float[] Bands
    {
        get
        {
            lock (_processingLock)
                return (float[])_publishedBands.Clone();
        }
    }
    public float Level => Volatile.Read(ref _level);
    public bool Active => Volatile.Read(ref _active) != 0;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _lifecycleVersion++;
            StopCore();
            TryStartCore();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _lifecycleVersion++;
            StopCore();
        }
    }

    private bool TryStartCore()
    {
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnRecordingStopped;
            Volatile.Write(ref _capture, capture);
            Volatile.Write(ref _active, 1);
            capture.StartRecording();
            return true;
        }
        catch
        {
            Volatile.Write(ref _capture, null);
            Volatile.Write(ref _active, 0);
            if (capture is not null)
            {
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.Dispose(); } catch { }
            }
            return false;
        }
    }

    private void StopCore()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        Volatile.Write(ref _active, 0);

        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }

        // Wait for an in-flight callback to leave before resetting its state.
        lock (_processingLock)
        {
            Array.Clear(_fft);
            Array.Clear(_publishedBands);
            Array.Clear(_workingBands);
            Volatile.Write(ref _level, 0);
            _pos = 0;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture)
        {
            return;
        }

        long version;
        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(capture, Volatile.Read(ref _capture)))
                return;
            capture.DataAvailable -= OnData;
            Volatile.Write(ref _active, 0);
            version = _lifecycleVersion;

            // Keep validation and clear atomic against Stop/Start. Unsubscribing
            // first plus this lock also drains any in-flight final data callback.
            lock (_processingLock)
            {
                Array.Clear(_publishedBands);
                Array.Clear(_workingBands);
                Volatile.Write(ref _level, 0);
            }
        }
        if (e.Exception is not null)
            App.LogException("AudioVisualizer capture stopped", e.Exception);

        // Device switches and driver resets stop WASAPI without changing media
        // state, so MainWindow would otherwise never call Start again. Retry only
        // if this exact capture is still current; an idle Stop invalidates it.
        _ = RestartAfterFailureAsync(capture, version);
    }

    private async Task RestartAfterFailureAsync(
        WasapiLoopbackCapture failedCapture,
        long expectedVersion)
    {
        try
        {
            const int attempts = 5;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
                lock (_lifecycleLock)
                {
                    if (_disposed != 0 || expectedVersion != _lifecycleVersion)
                        return;

                    if (attempt == 0)
                    {
                        if (!ReferenceEquals(failedCapture, Volatile.Read(ref _capture)))
                            return;
                        StopCore();
                    }
                    else if (Volatile.Read(ref _capture) is not null)
                    {
                        return;
                    }

                    if (TryStartCore())
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogException("AudioVisualizer capture restart", ex);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Runs on NAudio's capture thread. A stale callback from a session we've
        // already Stop()ped must not touch the shared FFT buffers, and ANY throw
        // here is an unrecoverable AppDomain crash — so guard + swallow (+ log).
        if (sender is not WasapiLoopbackCapture capture
            || !ReferenceEquals(capture, Volatile.Read(ref _capture)))
            return;

        try
        {
            lock (_processingLock)
            {
                // Stop/Start may have happened while this callback waited.
                if (!ReferenceEquals(capture, Volatile.Read(ref _capture))) return;

                var fmt = capture.WaveFormat;
                int bytesPerSample = fmt.BitsPerSample / 8;
                int channels = Math.Max(1, fmt.Channels);
                int frame = bytesPerSample * channels;
                if (frame == 0) return;

                for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                {
                    float sample = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                        ? BitConverter.ToSingle(e.Buffer, i)
                        : bytesPerSample == 2
                            ? BitConverter.ToInt16(e.Buffer, i) / 32768f
                            : 0f;

                    _fft[_pos].X = (float)(sample * Hann(_pos, FftLen));
                    _fft[_pos].Y = 0;
                    if (++_pos >= FftLen) { _pos = 0; Compute(); }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogException("AudioVisualizer capture thread", ex);
        }
    }

    private void Compute()
    {
        FastFourierTransform.FFT(true, M, _fft);

        var previous = Volatile.Read(ref _publishedBands);
        var next = _workingBands;
        int bins = FftLen / 2;
        float overall = 0;
        for (int b = 0; b < BandCount; b++)
        {
            // Log-ish spacing so bass doesn't dominate the whole strip.
            int i0 = (int)(Math.Pow((double)b / BandCount, 2) * bins);
            int i1 = Math.Max(i0 + 1, (int)(Math.Pow((double)(b + 1) / BandCount, 2) * bins));

            float sum = 0; int cnt = 0;
            for (int i = i0; i < i1 && i < bins; i++)
            {
                float mag = MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                sum += mag; cnt++;
            }
            float avg = cnt > 0 ? sum / cnt : 0;
            float v = Math.Clamp(MathF.Log10(1 + avg * 40f), 0, 1);

            // Attack fast, decay slow — looks musical.
            next[b] = v > previous[b] ? v : previous[b] * 0.80f + v * 0.20f;
            overall += next[b];
        }

        Volatile.Write(ref _publishedBands, next);
        _workingBands = previous;
        Volatile.Write(ref _level, Math.Clamp(overall / BandCount * 2.2f, 0, 1));
    }

    private static double Hann(int n, int len) => 0.5 * (1 - Math.Cos(2 * Math.PI * n / (len - 1)));

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifecycleVersion++;
            StopCore();
        }
    }
}
