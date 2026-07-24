using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMusic.Controls;
using TaskbarMusic.Interop;
using TaskbarMusic.Services;

namespace TaskbarMusic;

public partial class MainWindow : Window
{
    private const double MinWidth_ = 190;
    private const double MaxWidth_ = 560;

    private readonly AppSettings _settings = SettingsService.Load();
    private readonly VolumeService _volume = new();
    private readonly CatWindow _cat;
    private MediaService? _media;
    private GlobalScrollHook? _scrollHook;
    private TrayIconService? _tray;
    private HwndSource? _hwndSource;

    private readonly DispatcherTimer _volumeHide;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _topmost;
    private readonly DispatcherTimer _fsWatch;
    private bool _isIdle = true;
    private bool _fsHidden;    // hidden because a fullscreen app is on our monitor
    private bool _cleanedUp;

    // Audio-reactive dot-field background + morphing idle indicator.
    private readonly DotFieldVisualizer _viz = new();
    private readonly MorphIndicator _morph = new();
    private readonly AudioVisualizer _audio = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastVizS;
    private float _vizLevel;

    // The pill grows/shrinks away from an anchored SIDE (grows toward screen
    // centre), so the collapsed circle stays on the side it was parked.
    private enum AnchorSide { Left, Right }
    private AnchorSide _anchorSide = AnchorSide.Right;
    private double _anchorX;    // DIP screen X of the anchored edge
    private bool _adjusting;    // true while we reposition during a size change
    private bool _placed;       // ignore size changes until first placement

    // Manual drag state (replaces DragMove — see notes in OnPillMouseDown).
    private bool _dragging;
    private int _dragLastCursorX;
    private int _dragLastCursorY;
    private double _dragProposedLeft;
    private double _dragProposedTop;
    private double _dragDipX = 1;      // physical px -> DIP factors
    private double _dragDipY = 1;
    private TaskbarEdge _taskbarEdge = TaskbarEdge.Bottom;
    private double _fsStreak;         // consecutive fullscreen readings (debounce)
    private bool _fsCandidate;

    public MainWindow(CatWindow cat)
    {
        InitializeComponent();
        _cat = cat;

        // Dot-field sits behind all pill content; morph indicator above it.
        _viz.Opacity = 0;
        _morph.Opacity = 0;
        ContentClip.Children.Insert(0, _viz);
        ContentClip.Children.Insert(1, _morph);

        // Clip content to the pill's rounded shape so the dot-field can't spill
        // square corners over the desktop. Radius follows the (stadium) height.
        ContentClip.SizeChanged += (_, _) =>
        {
            double r = ContentClip.ActualHeight / 2;
            ContentClip.Clip = new RectangleGeometry(
                new Rect(0, 0, ContentClip.ActualWidth, ContentClip.ActualHeight), r, r);
        };

        _volumeHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _volumeHide.Tick += (_, _) => { _volumeHide.Stop(); FadeVolumeOverlay(false); };

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); PersistPosition(); };

        // Re-pin to the top periodically (cheap, no flicker).
        _topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmost.Tick += (_, _) => { if (!_fsHidden) WindowChromeHelper.ReassertTopmost(this); };

        // Watch for a fullscreen app on our monitor -> auto-hide.
        _fsWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _fsWatch.Tick += (_, _) => CheckFullscreen();

        MenuCat.IsChecked = _settings.CatEnabled;
        MenuStartup.IsChecked = StartupService.IsEnabled();
        StartupService.RefreshIfEnabled(); // fix the path if the .exe was moved

        // Manual, taskbar-axis drag (see OnPillMouseDown for why not DragMove).
        Pill.PreviewMouseLeftButtonDown += OnPillMouseDown;
        Pill.PreviewMouseMove += OnPillMouseMove;
        Pill.PreviewMouseLeftButtonUp += OnPillMouseUp;
        Pill.LostMouseCapture += (_, _) => EndDrag();

        // Place once the real size is known; keep it anchored as it resizes.
        Loaded += (_, _) => { PlaceWindow(); _placed = true; UpdateAnchor(); SnapToTaskbar(); };
        SizeChanged += OnPillResized;
        LocationChanged += OnWindowMoved;
    }

    private void OnWindowMoved(object? sender, EventArgs e)
    {
        // Ignore our own re-anchoring and live drags (drag sets Left directly).
        // WPF can raise LocationChanged while Show() is still connecting the
        // visual tree. PointToScreen is illegal in that interval.
        if (!_placed || _adjusting || _dragging) return;
        UpdateAnchor();
        SnapToTaskbar();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    // OnPillResized is the ONLY horizontal authority. It must NOT call
    // SnapToTaskbar: doing so re-enters ApplyPillHeight/Measure inside the
    // SizeChanged (layout) pass and, under a drag storm, trips WPF's layout
    // recursion -> crash. Height/Top are handled solely inside SnapToTaskbar.
    private void OnPillResized(object sender, SizeChangedEventArgs e)
    {
        // _dragging guard matches OnWindowMoved/SnapToTaskbar: never fight the
        // drag's Left writes with a stale anchor if a width animation fires mid-drag.
        if (!_placed || _dragging) return;
        _adjusting = true;
        Left = _anchorSide == AnchorSide.Left
            ? ClampLeft(_anchorX)                 // grow to the right
            : ClampLeft(_anchorX - ActualWidth);  // grow to the left
        _adjusting = false;
        ClipToMonitor();
    }

    /// <summary>Pick the anchor edge from which monitor-half the pill sits on.</summary>
    private void UpdateAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return;

        var pt = new NativeMethods.POINT
        {
            X = windowRect.Left + (windowRect.Right - windowRect.Left) / 2,
            Y = windowRect.Top + (windowRect.Bottom - windowRect.Top) / 2,
        };
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

        double monitorCenterPx = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2.0;
        _anchorSide = pt.X < monitorCenterPx ? AnchorSide.Left : AnchorSide.Right;
        _anchorX = _anchorSide == AnchorSide.Left ? Left : Left + ActualWidth;
    }

    /// <summary>Pin the pill to the taskbar edge of the monitor it's over.</summary>
    private void SnapToTaskbar()
    {
        if (!_placed || _fsHidden || !IsVisible || _dragging) return;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect)) return;

        var center = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        var pt = new NativeMethods.POINT { X = (int)center.X, Y = (int)center.Y };
        if (!TaskbarHelper.TryGetTaskbar(pt, out var taskbar)) return;
        _taskbarEdge = taskbar.Edge;

        var fromDevice = src.CompositionTarget.TransformFromDevice;
        bool vertical = taskbar.Edge is TaskbarEdge.Left or TaskbarEdge.Right;
        int thicknessPx = vertical ? taskbar.Bounds.Width : taskbar.Bounds.Height;
        double thicknessDip = thicknessPx * (vertical ? fromDevice.M11 : fromDevice.M22);

        // Fit inside the taskbar, a touch smaller so it reads as part of it.
        double desiredH = Math.Clamp(thicknessDip - 8, 34, 60);
        ApplyPillHeight(desiredH);

        // Convert absolute pixels relative to the current HWND. Multiplying an
        // absolute desktop coordinate by one scale is wrong when monitors have
        // different DPI values or negative origins.
        double ToDipX(double screenX) => DpiCoordinates.ScreenPixelToDip(
            screenX, windowRect.Left, Left, fromDevice.M11);
        double ToDipY(double screenY) => DpiCoordinates.ScreenPixelToDip(
            screenY, windowRect.Top, Top, fromDevice.M22);

        double newLeft = Left;
        double newTop;
        if (!vertical)
        {
            double centerY = (taskbar.Bounds.Top + taskbar.Bounds.Bottom) / 2.0;
            newTop = ToDipY(centerY) - desiredH / 2;
        }
        else
        {
            double minTop = ToDipY(taskbar.Bounds.Top + 4);
            // SizeToContent applies ActualHeight on the next layout pass.
            // Use the requested height now so a bottom-positioned pill cannot
            // spill after a DPI/taskbar-thickness change.
            double maxTop = ToDipY(taskbar.Bounds.Bottom - 4) - desiredH;
            newTop = maxTop < minTop ? minTop : Math.Clamp(Top, minTop, maxTop);

            if (taskbar.Edge == TaskbarEdge.Left)
            {
                newLeft = ToDipX(taskbar.Bounds.Left + 4);
                _anchorSide = AnchorSide.Left;
                _anchorX = newLeft;
            }
            else
            {
                double right = ToDipX(taskbar.Bounds.Right - 4);
                newLeft = right - ActualWidth;
                _anchorSide = AnchorSide.Right;
                _anchorX = right;
            }
        }

        if (Math.Abs(newTop - Top) > 0.5 || Math.Abs(newLeft - Left) > 0.5)
        {
            _adjusting = true;
            Left = newLeft;
            Top = newTop;
            _adjusting = false;
        }
        ClipToMonitor();
    }

    /// <summary>Resize the pill (and album art) to a target height, keeping stadium corners.</summary>
    private void ApplyPillHeight(double h)
    {
        if (Math.Abs(Pill.Height - h) < 0.5) return;
        Pill.Height = h;
        Pill.CornerRadius = new CornerRadius(h / 2);

        double art = Math.Max(20, h - 12);            // ~6px inset top/bottom
        ArtHost.Width = ArtHost.Height = art;
        // Rounded SQUARE (not a circle): a modest radius, ~26% of the art size.
        ArtHost.CornerRadius = new CornerRadius(Math.Max(5, art * 0.26));

        if (_isIdle) AnimatePillWidth(h, TimeSpan.FromMilliseconds(200)); // collapsed = circle
        else AnimateToContentWidth();
    }

    private double ClampLeft(double left)
    {
        var src = PresentationSource.FromVisual(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (src?.CompositionTarget is null || hwnd == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            double fallbackMin = SystemParameters.VirtualScreenLeft + 4;
            double fallbackMax = fallbackMin + SystemParameters.VirtualScreenWidth - ActualWidth - 8;
            return fallbackMax < fallbackMin
                ? fallbackMin
                : Math.Clamp(left, fallbackMin, fallbackMax);
        }

        int virtualLeftPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int virtualWidthPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int windowWidthPx = Math.Max(1, windowRect.Right - windowRect.Left);
        double toDip = src.CompositionTarget.TransformFromDevice.M11;
        double min = DpiCoordinates.ScreenPixelToDip(
            virtualLeftPx + 4, windowRect.Left, Left, toDip);
        double max = DpiCoordinates.ScreenPixelToDip(
            virtualLeftPx + virtualWidthPx - windowWidthPx - 4,
            windowRect.Left, Left, toDip);
        return max < min ? min : Math.Max(min, Math.Min(left, max));
    }

    private double ClampTop(double top)
    {
        var src = PresentationSource.FromVisual(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (src?.CompositionTarget is null || hwnd == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            double fallbackMin = SystemParameters.VirtualScreenTop + 4;
            double fallbackMax = fallbackMin + SystemParameters.VirtualScreenHeight - ActualHeight - 8;
            return fallbackMax < fallbackMin
                ? fallbackMin
                : Math.Clamp(top, fallbackMin, fallbackMax);
        }

        int virtualTopPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int virtualHeightPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        int windowHeightPx = Math.Max(1, windowRect.Bottom - windowRect.Top);
        double toDip = src.CompositionTarget.TransformFromDevice.M22;
        double min = DpiCoordinates.ScreenPixelToDip(
            virtualTopPx + 4, windowRect.Top, Top, toDip);
        double max = DpiCoordinates.ScreenPixelToDip(
            virtualTopPx + virtualHeightPx - windowHeightPx - 4,
            windowRect.Top, Top, toDip);
        return max < min ? min : Math.Max(min, Math.Min(top, max));
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(OnWindowMessage);

        WindowChromeHelper.MakeOverlay(this);
        ApplyTheme(ThemeWatcher.IsLightTheme());

        // NOTE on acrylic: WindowChromeHelper.EnableAcrylic(...) turns on the real
        // Windows blur-behind, but because this is an AllowsTransparency window with
        // a shadow margin, the blur is drawn as a *rectangle* and would halo around
        // the rounded pill. We therefore ship a tuned translucent "glass card" (see
        // GlassBrush) which is reliable and haloes nothing. To try true acrylic,
        // set the root Grid Margin to 0, drop the DropShadowEffect, and uncomment:
        // WindowChromeHelper.EnableAcrylic(this,
        //     ThemeWatcher.IsLightTheme() ? Color.FromRgb(0xF3,0xF3,0xF3) : Color.FromRgb(0x2A,0x2A,0x2A), 0x99);

        // Hover-scroll volume, works even when we're not the focused window.
        _scrollHook = new GlobalScrollHook(this, Pill, OnVolumeScroll);
        _scrollHook.Install();

        // Keep it above everything.
        WindowChromeHelper.ReassertTopmost(this);
        _topmost.Start();
        _fsWatch.Start();

        // System-tray icon (safety net for reaching settings / a lost pill).
        _tray = new TrayIconService();
        _tray.StartupToggled += ToggleStartup;
        _tray.LockToggled += ToggleLock;
        _tray.CatToggled += ToggleCat;
        _tray.ResetRequested += ResetPosition;
        _tray.ExitRequested += ExitApp;
        SyncMenus();

        // Drive the dot-field background every frame.
        CompositionTarget.Rendering += OnRendering;

        // Show idle immediately, then connect to media.
        SetIdle(true, animate: false);
        try
        {
            _media = new MediaService(Dispatcher);
            _media.Changed += OnMediaChanged;
            await _media.InitializeAsync();
        }
        catch
        {
            // SMTC not available (very old Windows) — stay idle.
        }
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is NativeMethods.WM_DPICHANGED or
            NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_SETTINGCHANGE)
        {
            bool displayChanged = message == NativeMethods.WM_DISPLAYCHANGE;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_placed || !IsLoaded) return;

                if (displayChanged)
                {
                    var center = Pill.PointToScreen(
                        new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
                    var point = new NativeMethods.POINT
                    {
                        X = (int)Math.Round(center.X),
                        Y = (int)Math.Round(center.Y),
                    };
                    if (NativeMethods.MonitorFromPoint(
                        point, NativeMethods.MONITOR_DEFAULTTONULL) == IntPtr.Zero)
                    {
                        PlaceWindow();
                    }
                }

                UpdateAnchor();
                SnapToTaskbar();
                ClipToMonitor();
            });
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(OnWindowMessage);
            _hwndSource = null;
        }
        Cleanup();
        base.OnClosed(e);
    }

    // ======================================================================
    //  Media -> UI
    // ======================================================================

    private void OnMediaChanged(MediaInfo info)
    {
        if (!info.HasMedia)
        {
            SetIdle(true);
            return;
        }

        SetIdle(false);

        TitleRun.Text = info.Title;
        bool hasArtist = !string.IsNullOrWhiteSpace(info.Artist);
        SepRun.Text = hasArtist ? "   •   " : "";
        ArtistRun.Text = hasArtist ? info.Artist : "";

        ArtBrush.ImageSource = info.Art;
        ArtFallback.Visibility = info.Art is null ? Visibility.Visible : Visibility.Collapsed;

        PlayButton.Content = info.IsPlaying ? "\uE769" : "\uE768"; // pause : play
        PrevButton.IsEnabled = info.CanGoPrevious;
        NextButton.IsEnabled = info.CanGoNext;

        // Feed the dot-field palette (album-art colour + random accent per track).
        _viz.Target1 = info.Accent1;
        _viz.Target2 = info.Accent2;

        AnimateToContentWidth();
        SettleBounce();
    }

    // ======================================================================
    //  Dot-field render loop
    // ======================================================================

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_fsHidden) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastVizS;
        if (dt < 0.02) return; // cap at ~50 fps
        _lastVizS = now;
        if (dt > 0.1) dt = 0.1;

        if (_isIdle)
        {
            _morph.Tick(dt); // only the idle indicator animates when idle
            return;
        }

        // Smooth the audio energy so the field breathes instead of flickering.
        // ~40% more reactive again: bigger amplitude + snappier attack.
        float target = Math.Min(1f, _audio.Level * 1.8f);
        _vizLevel += (target - _vizLevel) * (float)Math.Clamp(dt * 15.0, 0, 1);
        _viz.Level = _vizLevel;
        _viz.Tick(dt);
    }

    private void FadeViz(bool show) =>
        _viz.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 0.95 : 0, TimeSpan.FromMilliseconds(show ? 260 : 200)));

    // ======================================================================
    //  Auto-hide when a fullscreen app is on our monitor
    // ======================================================================

    private void CheckFullscreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool fs = FullscreenDetector.ForegroundFullscreenOnSameMonitor(hwnd);

        if (fs == _fsHidden) { _fsStreak = 0; return; }  // already in the right state
        // Require two consecutive readings (~1.2s) so a transient (e.g. clicking
        // a taskbar app) can't flicker the pill.
        if (fs == _fsCandidate) _fsStreak++;
        else { _fsCandidate = fs; _fsStreak = 1; }
        if (_fsStreak >= 2) { _fsStreak = 0; HideForFullscreen(fs); }
    }

    private void HideForFullscreen(bool hide)
    {
        _fsHidden = hide;
        if (_scrollHook is not null) _scrollHook.IsEnabled = !hide;
        var scale = TimeSpan.FromMilliseconds(hide ? 340 : 440);

        if (hide)
        {
            // Liquid-glass shrink + fade, then actually hide so it blocks nothing.
            var s = new DoubleAnimation(0.72, scale) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => { if (_fsHidden) { Hide(); _cat.Hide(); _cat.StopIdle(); } };
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Show();
            _cat.Show(); // keep the transparent host ready for the next play→idle visit
            WindowChromeHelper.ReassertTopmost(this);
            SnapToTaskbar();

            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)));
            var s = new DoubleAnimation(1, scale) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
            PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        }
    }

    /// <summary>Measure the natural content width and glide the pill to it.</summary>
    private void AnimateToContentWidth()
    {
        ExpandedContent.Measure(new Size(double.PositiveInfinity, Pill.Height));
        double target = Math.Clamp(ExpandedContent.DesiredSize.Width + 6, MinWidth_, MaxWidth_);
        AnimatePillWidth(target, TimeSpan.FromMilliseconds(420));
    }

    private void AnimatePillWidth(double target, TimeSpan duration)
    {
        // No explicit 'from': the animation picks up the current (possibly still
        // animating/held) value, so back-to-back changes chain smoothly with no
        // jump back to the XAML base width.
        var anim = new DoubleAnimation(target, duration)
        {
            // Smooth glide with a hair of overshoot — the "liquid" feel.
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 },
        };
        Pill.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>Tiny scale pop so a track change feels alive.</summary>
    private void SettleBounce()
    {
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromPercent(0.4),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // ======================================================================
    //  Idle / collapsed dot + cat
    // ======================================================================

    private void SetIdle(bool idle, bool animate = true)
    {
        if (idle == _isIdle && animate) return;
        bool wasIdle = _isIdle;
        _isIdle = idle;

        var fade = TimeSpan.FromMilliseconds(220);
        ExpandedContent.BeginAnimation(OpacityProperty,
            new DoubleAnimation(idle ? 0 : 1, fade));
        _morph.BeginAnimation(OpacityProperty,
            new DoubleAnimation(idle ? 1 : 0, fade));

        if (idle)
            AnimatePillWidth(Pill.Height, TimeSpan.FromMilliseconds(360)); // collapse to a circle
        else
            AnimateToContentWidth();

        // Dot-field background + audio capture follow the active state.
        if (idle) { _audio.Stop(); FadeViz(false); }
        else { _audio.Start(); FadeViz(true); }

        if (!idle)
            _cat.StopIdle();
        else if (!wasIdle && _settings.CatEnabled)
            _cat.StartIdle(GetPillScreenAnchor);
    }

    /// <summary>
    /// Pill and monitor geometry in physical pixels. The cat is a different
    /// per-monitor-aware HWND, so exchanging WPF DIPs between the windows would
    /// be wrong when they currently have different DPI scales.
    /// </summary>
    private CatAnchor? GetPillScreenAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return null;

        var topLeft = Pill.PointToScreen(new Point(0, 0));
        var bottomRight = Pill.PointToScreen(
            new Point(Pill.ActualWidth, Pill.ActualHeight));
        var pill = new PixelRect(
            (int)Math.Floor(topLeft.X),
            (int)Math.Floor(topLeft.Y),
            (int)Math.Ceiling(bottomRight.X),
            (int)Math.Ceiling(bottomRight.Y));
        var center = new NativeMethods.POINT
        {
            X = pill.Left + pill.Width / 2,
            Y = pill.Top + pill.Height / 2,
        };
        IntPtr monitor = NativeMethods.MonitorFromPoint(
            center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!pill.IsValid || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return null;

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        return new CatAnchor(
            pill,
            new PixelRect(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right, info.rcMonitor.Bottom),
            dpi);
    }

    // ======================================================================
    //  Volume (scroll)
    // ======================================================================

    private void OnVolumeScroll(int notches)
    {
        float level = _volume.Nudge((float)(notches * _settings.VolumeStep));
        if (float.IsNaN(level)) return;
        ShowVolumeOverlay(level);
    }

    private void ShowVolumeOverlay(float level)
    {
        double track = Math.Max(0, Pill.ActualWidth - 20);
        VolumeFill.Width = track * level;
        VolumeText.Text = $"{Math.Round(level * 100)}%";
        FadeVolumeOverlay(true);
        _volumeHide.Stop();
        _volumeHide.Start();
    }

    private void FadeVolumeOverlay(bool show) =>
        VolumeOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 90 : 280)));

    // ======================================================================
    //  Transport buttons
    // ======================================================================

    private async void OnPrev(object s, RoutedEventArgs e) { if (_media != null) await _media.PreviousAsync(); }
    private async void OnNext(object s, RoutedEventArgs e) { if (_media != null) await _media.NextAsync(); }
    private async void OnPlayPause(object s, RoutedEventArgs e) { if (_media != null) await _media.TogglePlayPauseAsync(); }

    // ======================================================================
    //  Dragging (any monitor) + placement
    // ======================================================================

    // Manual drag instead of Window.DragMove(): DragMove runs a modal OS move
    // loop that fires LocationChanged in a storm; combined with SnapToTaskbar it
    // re-entered layout and crashed. This moves along the taskbar's long axis
    // using the absolute cursor position, so it is smooth and DPI-stable.
    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;

        var src = PresentationSource.FromVisual(this);
        _dragDipX = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        _dragDipY = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        _dragLastCursorX = p.X;
        _dragLastCursorY = p.Y;
        _dragProposedLeft = Left;
        _dragProposedTop = Top;
        _fsWatch.Stop();          // don't let auto-hide steal capture mid-drag
        _dragging = Pill.CaptureMouse(); // if capture fails, don't get stuck "dragging"
        if (!_dragging) _fsWatch.Start();
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;

        // Refresh the px->DIP factor in case we crossed into a different-DPI monitor.
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _dragDipX = src.CompositionTarget.TransformFromDevice.M11;
            _dragDipY = src.CompositionTarget.TransformFromDevice.M22;
        }

        // Apply incremental deltas. Re-scaling the entire drag distance with the
        // destination monitor's DPI causes a jump when crossing a mixed-DPI seam.
        if (_taskbarEdge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            _dragProposedTop += (p.Y - _dragLastCursorY) * _dragDipY;
            _dragLastCursorY = p.Y;
            Top = ClampTop(ApplyVerticalMonitorResistance(_dragProposedTop));
        }
        else
        {
            _dragProposedLeft += (p.X - _dragLastCursorX) * _dragDipX;
            _dragLastCursorX = p.X;
            Left = ClampLeft(ApplyMonitorResistance(_dragProposedLeft));
        }
        ClipToMonitor();           // cull the part that spilled onto another monitor
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();
        UpdateAnchor();     // re-pick side from the new position
        SnapToTaskbar();    // one snap now that dragging is done
        PersistPosition();
        _fsWatch.Start();
    }

    // Real resistance at monitor seams: the pill stays FULLY inside its current
    // monitor (a sticky "dead zone") until the cursor pushes >resist past the edge,
    // then it slides across smoothly (no jump). Keeping the pill fully inside means
    // its centre never sits on the seam, so the current-monitor detection stays
    // stable (the old version let the centre reach the seam, which flipped the
    // monitor immediately and killed the resistance).
    private double ApplyMonitorResistance(double proposedLeft)
    {
        const double resist = 85; // DIP the cursor must overshoot to cross
        if (!TryGetMonitorDip(out double monL, out double monR, out _, out _)) return proposedLeft;

        double w = ActualWidth;
        if (monR - monL <= w) return proposedLeft; // pill wider than monitor: don't fight

        if (proposedLeft < monL)
        {
            double over = monL - proposedLeft;
            return over < resist ? monL : proposedLeft + resist;
        }
        if (proposedLeft + w > monR)
        {
            double over = (proposedLeft + w) - monR;
            return over < resist ? monR - w : proposedLeft - resist;
        }
        return proposedLeft;
    }

    private double ApplyVerticalMonitorResistance(double proposedTop)
    {
        const double resist = 85;
        if (!TryGetMonitorDip(out _, out _, out double monT, out double monB))
            return proposedTop;

        double height = ActualHeight;
        if (monB - monT <= height) return proposedTop;

        if (proposedTop < monT)
        {
            double over = monT - proposedTop;
            return over < resist ? monT : proposedTop + resist;
        }
        if (proposedTop + height > monB)
        {
            double over = proposedTop + height - monB;
            return over < resist ? monB - height : proposedTop - resist;
        }
        return proposedTop;
    }

    /// <summary>Monitor rect (DIP) under the pill's centre. False if unavailable.</summary>
    private bool TryGetMonitorDip(out double left, out double right, out double top, out double bottom)
    {
        left = right = top = bottom = 0;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return false;

        var c = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        var pt = new NativeMethods.POINT { X = (int)c.X, Y = (int)c.Y };
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var windowRect)) return false;

        double dx = src.CompositionTarget.TransformFromDevice.M11;
        double dy = src.CompositionTarget.TransformFromDevice.M22;
        left = DpiCoordinates.ScreenPixelToDip(mi.rcMonitor.Left, windowRect.Left, Left, dx);
        right = DpiCoordinates.ScreenPixelToDip(mi.rcMonitor.Right, windowRect.Left, Left, dx);
        top = DpiCoordinates.ScreenPixelToDip(mi.rcMonitor.Top, windowRect.Top, Top, dy);
        bottom = DpiCoordinates.ScreenPixelToDip(mi.rcMonitor.Bottom, windowRect.Top, Top, dy);
        return true;
    }

    /// <summary>Clip the window content to the monitor under the pill's centre, so a
    /// pill straddling a seam is cut off instead of showing on both monitors.</summary>
    private void ClipToMonitor()
    {
        if (!TryGetMonitorDip(out double mL, out double mR, out double mT, out double mB))
        {
            RootGrid.Clip = null;
            return;
        }
        double visL = Math.Max(Left, mL), visR = Math.Min(Left + ActualWidth, mR);
        double visT = Math.Max(Top, mT), visB = Math.Min(Top + ActualHeight, mB);
        if (visR <= visL || visB <= visT) { RootGrid.Clip = null; return; }

        // Fully inside -> full-rect clip (no visible effect). Straddling -> cut.
        RootGrid.Clip = new RectangleGeometry(
            new Rect(visL - Left, visT - Top, visR - visL, visB - visT));
    }

    private static bool IsWithinButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button) return true;
            d = GetAnyParent(d);
        }
        return false;
    }

    // Walk BOTH trees safely:
    //  - Visuals (incl. a Button's templated ContentPresenter/TextBlock) -> visual
    //    parent, so button clicks are detected (LogicalTreeHelper can't reach a
    //    templated parent -> that's what broke the transport buttons).
    //  - Content elements (Run in the title/artist) aren't Visuals and would make
    //    VisualTreeHelper.GetParent throw -> use their content parent instead.
    private static DependencyObject? GetAnyParent(DependencyObject d)
    {
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            return System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is FrameworkContentElement fce)
            return fce.Parent ?? LogicalTreeHelper.GetParent(d);
        return LogicalTreeHelper.GetParent(d);
    }

    private void PlaceWindow()
    {
        var wa = SystemParameters.WorkArea;
        if (double.IsFinite(_settings.Left) && double.IsFinite(_settings.Top))
        {
            Left = _settings.Left;
            Top = _settings.Top;
            if (OnAnyScreen()) return;
        }

        // Default: bottom-right, just above the taskbar area.
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 8;
    }

    // Reject a saved position that no longer lands on a real monitor (unplugged
    // display, changed layout) so the pill can't be lost off-screen.
    private bool OnAnyScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        var pt = new NativeMethods.POINT
        {
            X = rect.Left + Math.Min(20, Math.Max(1, rect.Right - rect.Left - 1)),
            Y = rect.Top + Math.Min(20, Math.Max(1, rect.Bottom - rect.Top - 1)),
        };
        return NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    private void PersistPosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        SettingsService.Save(_settings);
    }

    // ======================================================================
    //  Theme
    // ======================================================================

    private void ApplyTheme(bool light)
        => ThemeWatcher.ApplyResources(Resources, light);

    // ======================================================================
    //  Context menu
    // ======================================================================

    // Pill context-menu clicks defer to the shared actions (which also drive the tray).
    private void OnToggleStartup(object sender, RoutedEventArgs e) => ToggleStartup();
    private void OnToggleLock(object sender, RoutedEventArgs e) => ToggleLock();
    private void OnToggleCat(object sender, RoutedEventArgs e) => ToggleCat();
    private void OnResetPosition(object sender, RoutedEventArgs e) => ResetPosition();
    private void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    private void ToggleStartup()
    {
        StartupService.SetEnabled(!StartupService.IsEnabled());
        SyncMenus();
    }

    private void ToggleLock()
    {
        _settings.Locked = !_settings.Locked;
        SettingsService.Save(_settings);
        SyncMenus();
    }

    private void ToggleCat()
    {
        _settings.CatEnabled = !_settings.CatEnabled;
        SettingsService.Save(_settings);
        if (!_settings.CatEnabled) _cat.StopIdle();
        SyncMenus();
    }

    private void ResetPosition()
    {
        _settings.Left = double.NaN;
        _settings.Top = double.NaN;
        PlaceWindow();
        SnapToTaskbar();
        PersistPosition();
    }

    /// <summary>Mirror the current state into both the pill menu and the tray menu.</summary>
    private void SyncMenus()
    {
        bool startup = StartupService.IsEnabled();
        MenuStartup.IsChecked = startup;
        MenuLock.IsChecked = _settings.Locked;
        MenuCat.IsChecked = _settings.CatEnabled;
        _tray?.SetStates(startup, _settings.Locked, _settings.CatEnabled);
    }

    private void ExitApp()
    {
        Cleanup();
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        if (_placed) PersistPosition();
        _topmost.Stop();
        _fsWatch.Stop();
        _volumeHide.Stop();
        _saveDebounce.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _cat.StopIdle();          // stop before the window it depends on goes away
        _tray?.Dispose();
        _tray = null;
        _scrollHook?.Dispose();
        _scrollHook = null;
        if (_media is not null) _media.Changed -= OnMediaChanged;
        _media?.Dispose();
        _media = null;
        _audio.Dispose();
        _volume.Dispose();
    }
}
