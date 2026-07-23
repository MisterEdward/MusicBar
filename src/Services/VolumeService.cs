using NAudio.CoreAudioApi;

namespace TaskbarMusic.Services;

/// <summary>Reads and nudges the default render device's master volume.</summary>
internal sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    private MMDevice? Device
    {
        get
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch { return null; } // no output device present
        }
    }

    /// <summary>Current master volume, 0..1. NaN if no device.</summary>
    public float Current
    {
        get
        {
            var d = Device;
            return d is null ? float.NaN : d.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
    }

    public bool IsMuted
    {
        get { var d = Device; return d is not null && d.AudioEndpointVolume.Mute; }
    }

    /// <summary>Change volume by <paramref name="deltaScalar"/> (e.g. 0.02). Returns the new level 0..1.</summary>
    public float Nudge(float deltaScalar)
    {
        var d = Device;
        if (d is null) return float.NaN;
        var vol = d.AudioEndpointVolume;
        float next = Math.Clamp(vol.MasterVolumeLevelScalar + deltaScalar, 0f, 1f);
        vol.MasterVolumeLevelScalar = next;
        if (next > 0 && vol.Mute) vol.Mute = false; // scrolling up unmutes
        return next;
    }

    public void ToggleMute()
    {
        var d = Device;
        if (d is not null) d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute;
    }

    public void Dispose() => _enumerator.Dispose();
}
