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
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _tracked;
    private readonly Random _rng = new();
    private readonly Dictionary<MediaTrackKey, (Color Primary, Color Secondary)> _paletteCache = new();
    private readonly Queue<MediaTrackKey> _paletteOrder = new();
    private readonly LatestVersionGate _versionGate = new();
    private int _disposed;
    private const int PaletteCacheCapacity = 64;

    public event Action<MediaInfo>? Changed;

    public MediaService(Dispatcher uiDispatcher) => _ui = uiDispatcher;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        lock (_stateLock)
        {
            if (_disposed != 0) return;
            if (_manager is not null) return;

            _manager = manager;
            _manager.SessionsChanged += OnManagerChanged;
            _manager.CurrentSessionChanged += OnManagerChanged;
        }
        Reevaluate();
    }

    // ---- Session selection ------------------------------------------------

    private void Reevaluate()
    {
        GlobalSystemMediaTransportControlsSession? chosen;
        long version;

        // Runs on a WinRT thread-pool thread. A session can go invalid between
        // enumeration and property reads (source app closes) -> COMException ->
        // hard crash. Guard like BuildInfoAsync already does.
        try
        {
            lock (_stateLock)
            {
                if (_disposed != 0) return;

                chosen = PickBestSession();
                if (!ReferenceEquals(chosen, _tracked))
                {
                    Detach(_tracked);
                    _tracked = chosen;
                    Attach(_tracked);
                }
                version = _versionGate.Advance();
            }
        }
        catch (Exception ex)
        {
            App.LogException("MediaService (WinRT callback thread)", ex);
            return;
        }
        _ = PushUpdateAsync(chosen, version);
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
        try
        {
            s.MediaPropertiesChanged += OnSessionChanged;
            s.PlaybackInfoChanged += OnSessionChanged;
        }
        catch
        {
            // A session can vanish while handlers are being attached.
            Detach(s);
        }
    }

    private void Detach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return;
        try { s.MediaPropertiesChanged -= OnSessionChanged; } catch { }
        try { s.PlaybackInfoChanged -= OnSessionChanged; } catch { }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        // A property changed but the *best* session might now be different too.
        => Reevaluate();

    private void OnManagerChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        object args) => Reevaluate();

    // ---- Snapshot ---------------------------------------------------------

    private async Task PushUpdateAsync(
        GlobalSystemMediaTransportControlsSession? session,
        long version)
    {
        await _updateGate.WaitAsync();
        try
        {
            // Coalesce queued callbacks before doing an expensive WinRT/art read.
            if (!IsCurrent(session, version)) return;

            var info = await BuildInfoAsync(session);
            if (!IsCurrent(session, version) || _ui.HasShutdownStarted) return;

            _ = _ui.BeginInvoke(() =>
            {
                // The dispatcher may have been busy while the track changed.
                if (IsCurrent(session, version))
                    Changed?.Invoke(info);
            });
        }
        catch (Exception ex)
        {
            App.LogException("MediaService update pipeline", ex);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private bool IsCurrent(
        GlobalSystemMediaTransportControlsSession? session,
        long version)
    {
        lock (_stateLock)
        {
            return _disposed == 0
                && _versionGate.IsCurrent(version)
                && ReferenceEquals(session, _tracked);
        }
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
            string artist = string.IsNullOrWhiteSpace(props.Artist)
                ? (props.AlbumArtist ?? "")
                : props.Artist;
            var paletteKey = new MediaTrackKey(
                s.SourceAppUserModelId ?? "",
                props.Title ?? "",
                artist,
                props.AlbumTitle ?? "");
            var (a1, a2) = GetOrCreatePalette(paletteKey, art as BitmapSource);

            return new MediaInfo(
                Title: props.Title ?? "",
                Artist: artist,
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

    private (Color Primary, Color Secondary) GetOrCreatePalette(
        MediaTrackKey key,
        BitmapSource? art)
    {
        // BuildInfoAsync is serialized by _updateGate, so Random and the cache
        // are only ever touched by one update at a time.
        if (_paletteCache.TryGetValue(key, out var palette))
            return palette;

        palette = ColorHelper.FromArt(art, _rng);
        _paletteCache[key] = palette;
        _paletteOrder.Enqueue(key);

        if (_paletteOrder.Count > PaletteCacheCapacity)
            _paletteCache.Remove(_paletteOrder.Dequeue());

        return palette;
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            if (stream.Size == 0) return null;

            using var reader = new DataReader(stream);
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
        await RunTransportAsync(static s => s.TryTogglePlayPauseAsync().AsTask());
    }

    public async Task NextAsync()
    {
        await RunTransportAsync(static s => s.TrySkipNextAsync().AsTask());
    }

    public async Task PreviousAsync()
    {
        await RunTransportAsync(static s => s.TrySkipPreviousAsync().AsTask());
    }

    private async Task RunTransportAsync(
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> operation)
    {
        await _transportGate.WaitAsync();
        try
        {
            GlobalSystemMediaTransportControlsSession? session;
            lock (_stateLock)
            {
                if (_disposed != 0) return;
                session = _tracked;
            }

            if (session is not null)
                await operation(session);
        }
        catch
        {
            // The selected session can disappear between snapshot and command.
        }
        finally
        {
            _transportGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _versionGate.Advance();
            Detach(_tracked);
            _tracked = null;

            if (_manager is not null)
            {
                try { _manager.SessionsChanged -= OnManagerChanged; } catch { }
                try { _manager.CurrentSessionChanged -= OnManagerChanged; } catch { }
                _manager = null;
            }
            Changed = null;
        }
    }
}
