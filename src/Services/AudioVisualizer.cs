using NAudio.Dsp;
using NAudio.Wave;

namespace TaskbarMusic.Services;

/// <summary>
/// Captures system audio (WASAPI loopback), runs an FFT, and exposes a small set
/// of smoothed frequency bands (0..1) plus an overall level. Purely cosmetic, so
/// it reads/writes the band array without locking. If capture can't start (no
/// device, etc.) it just produces silence and the UI falls back to an idle wave.
/// </summary>
internal sealed class AudioVisualizer : IDisposable
{
    public const int BandCount = 24;
    private const int FftLen = 1024;   // must be 2^M
    private const int M = 10;

    private readonly Complex[] _fft = new Complex[FftLen];
    private int _pos;

    private WasapiLoopbackCapture? _capture;
    private readonly float[] _bands = new float[BandCount];

    public float[] Bands => _bands;
    public float Level { get; private set; }
    public bool Active { get; private set; }

    public void Start()
    {
        Stop();
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
            Active = true;
        }
        catch
        {
            _capture = null;
            Active = false;
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        Active = false;
        Array.Clear(_bands);
        Level = 0;
        _pos = 0;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Runs on NAudio's capture thread. A stale callback from a session we've
        // already Stop()ped must not touch the shared FFT buffers, and ANY throw
        // here is an unrecoverable AppDomain crash — so guard + swallow (+ log).
        if (!ReferenceEquals(sender, _capture)) return;
        var fmt = _capture?.WaveFormat;
        if (fmt is null) return;

        try
        {
            int bytesPerSample = fmt.BitsPerSample / 8;
            int channels = Math.Max(1, fmt.Channels);
            int frame = bytesPerSample * channels;
            if (frame == 0) return;

            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sample = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(e.Buffer, i)
                    : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, i) / 32768f : 0f;

                _fft[_pos].X = (float)(sample * Hann(_pos, FftLen));
                _fft[_pos].Y = 0;
                if (++_pos >= FftLen) { _pos = 0; Compute(); }
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
            _bands[b] = v > _bands[b] ? v : _bands[b] * 0.80f + v * 0.20f;
            overall += _bands[b];
        }
        Level = Math.Clamp(overall / BandCount * 2.2f, 0, 1);
    }

    private static double Hann(int n, int len) => 0.5 * (1 - Math.Cos(2 * Math.PI * n / (len - 1)));

    public void Dispose() => Stop();
}
