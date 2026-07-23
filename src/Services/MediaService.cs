using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMusic.Services;

/// <summary>Snapshot of what's playing, as the UI wants it.</summary>
internal sealed record MediaInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    bool CanGoNext,
    bool CanGoPrevious,
    ImageSource? Art,
    Color Accent1,
    Color Accent2,
    string SourceApp)
{
    public static readonly MediaInfo Empty = new("", "", false, false, false, null,
        Color.FromRgb(0x3A, 0x6E, 0xA5), Color.FromRgb(0x8A, 0x5C, 0xF6), "");
    public bool HasMedia => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Wraps the System Media Transport Controls. Picks the "best" session with a
/// bias toward Apple Music &gt; other known players &gt; whatever is playing, and
/// raises <see cref="Changed"/> on the UI thread whenever anything moves.
/// </summary>
internal sealed class MediaService : IDisposable
{
    // AppUserModelId substrings, highest priority first. Apple Music (Store app
    // and the older iTunes bridge) win over everything else.
    private static readonly string[] PreferredApps =
    {
        "applemusic", "apple.music", "itunes",
    };

    private static readonly string[] KnownPlayers =
    {
        "spotify", "tidal", "deezer", "youtube", "chrome", "msedge", "firefox",
        "vlc", "foobar", "musicbee", "groove", "zune", "media",
    };

    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _tracked;
    private readonly Random _rng = new();

    public event Action<MediaInfo>? Changed;

    public MediaService(Dispatcher uiDispatcher) => _ui = uiDispatcher;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Reevaluate();
        _manager.CurrentSessionChanged += (_, _) => Reevaluate();
        Reevaluate();
    }

    // ---- Session selection ------------------------------------------------

    private void Reevaluate()
    {
        // Runs on a WinRT thread-pool thread. A session can go invalid between
        // enumeration and property reads (source app closes) -> COMException ->
        // hard crash. Guard like BuildInfoAsync already does.
        try
        {
            var chosen = PickBestSession();
            if (!ReferenceEquals(chosen, _tracked))
            {
                Detach(_tracked);
                _tracked = chosen;
                Attach(_tracked);
            }
        }
        catch (Exception ex)
        {
            App.LogException("MediaService (WinRT callback thread)", ex);
            return;
        }
        _ = PushUpdateAsync();
    }

    private GlobalSystemMediaTransportControlsSession? PickBestSession()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions();
        if (sessions.Count == 0) return null;

        GlobalSystemMediaTransportControlsSession? best = null;
        int bestScore = int.MinValue;
        foreach (var s in sessions)
        {
            string id = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
            bool playing = s.GetPlaybackInfo()?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            int score = 0;
            if (PreferredApps.Any(id.Contains)) score += 1000;
            else if (KnownPlayers.Any(id.Contains)) score += 100;
            if (playing) score += 50; // prefer something actually making sound

            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }
        // Fall back to the OS's notion of "current" if scoring found nothing.
        return best ?? _manager.GetCurrentSession();
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return;
        s.MediaPropertiesChanged += OnSessionChanged;
        s.PlaybackInfoChanged += OnSessionChanged;
    }

    private void Detach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return;
        s.MediaPropertiesChanged -= OnSessionChanged;
        s.PlaybackInfoChanged -= OnSessionChanged;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        // A property changed but the *best* session might now be different too.
        => Reevaluate();

    // ---- Snapshot ---------------------------------------------------------

    private async Task PushUpdateAsync()
    {
        var info = await BuildInfoAsync(_tracked);
        if (_ui.HasShutdownStarted) return; // don't post to a dying Dispatcher
        _ = _ui.BeginInvoke(() => Changed?.Invoke(info));
    }

    private async Task<MediaInfo> BuildInfoAsync(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return MediaInfo.Empty;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var pb = s.GetPlaybackInfo();
            bool playing = pb?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var art = await LoadThumbnailAsync(props.Thumbnail);
            var (a1, a2) = ColorHelper.FromArt(art as BitmapSource, _rng);

            return new MediaInfo(
                Title: props.Title ?? "",
                Artist: string.IsNullOrWhiteSpace(props.Artist) ? (props.AlbumArtist ?? "") : props.Artist,
                IsPlaying: playing,
                CanGoNext: pb?.Controls.IsNextEnabled ?? false,
                CanGoPrevious: pb?.Controls.IsPreviousEnabled ?? false,
                Art: art,
                Accent1: a1,
                Accent2: a2,
                SourceApp: s.SourceAppUserModelId ?? "");
        }
        catch
        {
            return MediaInfo.Empty; // session vanished mid-read
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            if (stream.Size == 0) return null;

            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze(); // cross-thread safe
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---- Transport controls ----------------------------------------------

    public async Task TogglePlayPauseAsync()
    {
        if (_tracked is not null) await _tracked.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_tracked is not null) await _tracked.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_tracked is not null) await _tracked.TrySkipPreviousAsync();
    }

    public void Dispose() => Detach(_tracked);
}
