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

    private readonly DispatcherTimer _volumeHide;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _topmost;
    private readonly DispatcherTimer _fsWatch;
    private bool _isIdle = true;
    private bool _fsHidden;    // hidden because a fullscreen app is on our monitor

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
    private int _dragStartCursorX;
    private double _dragStartLeft;
    private double _dragDip = 1;      // physical px -> DIP factor
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

        // Manual, horizontal-only drag (see OnPillMouseDown for why not DragMove).
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
        if (_adjusting || _dragging) return;
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
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return;

        var c = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        var pt = new NativeMethods.POINT { X = (int)c.X, Y = (int)c.Y };
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

        double toDip = src.CompositionTarget.TransformFromDevice.M11;
        double monCenterDip = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2.0 * toDip;
        double pillCenter = Left + ActualWidth / 2;

        _anchorSide = pillCenter < monCenterDip ? AnchorSide.Left : AnchorSide.Right;
        _anchorX = _anchorSide == AnchorSide.Left ? Left : Left + ActualWidth;
    }

    /// <summary>Pin the pill's vertical centre to the taskbar of the monitor it's over.</summary>
    private void SnapToTaskbar()
    {
        if (!_placed || _fsHidden || !IsVisible || _dragging) return;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return;

        var center = Pill.PointToScreen(new Point(Pill.ActualWidth / 2, Pill.ActualHeight / 2));
        var pt = new NativeMethods.POINT { X = (int)center.X, Y = (int)center.Y };
        if (!TaskbarHelper.TryGetTaskbar(pt, out var strip)) return;

        double toDip = src.CompositionTarget.TransformFromDevice.M22; // device px -> DIP
        double tbHeightDip = (strip.Bottom - strip.Top) * toDip;

        // Fit inside the taskbar, a touch smaller so it reads as part of it.
        double desiredH = Math.Clamp(tbHeightDip - 8, 34, 60);
        ApplyPillHeight(desiredH);

        double centerYDip = ((strip.Top + strip.Bottom) / 2.0) * toDip;
        double newTop = centerYDip - desiredH / 2; // window height == pill height (no margin)
        if (Math.Abs(newTop - Top) > 0.5)
        {
            _adjusting = true;
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
        double min = SystemParameters.VirtualScreenLeft + 4;
        double max = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 4;
        return max < min ? min : Math.Max(min, Math.Min(left, max));
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

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
        _scrollHook = new GlobalScrollHook(this, OnVolumeScroll);
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
            WindowChromeHelper.ReassertTopmost(this);
            SnapToTaskbar();
            if (_isIdle && _settings.CatEnabled) { _cat.Show(); _cat.StartIdle(GetPillScreenRectDip); }

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

        if (_settings.CatEnabled)
        {
            if (idle) _cat.StartIdle(GetPillScreenRectDip);
            else _cat.StopIdle();
        }
    }

    /// <summary>Pill rectangle in DIP screen coordinates, for cat placement.</summary>
    private Rect GetPillScreenRectDip()
    {
        // Check the source BEFORE PointToScreen: this delegate is invoked later
        // by the cat, possibly after the window's HwndSource is gone.
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return Rect.Empty;
        var tl = Pill.PointToScreen(new Point(0, 0));
        double sx = src.CompositionTarget.TransformToDevice.M11;
        double sy = src.CompositionTarget.TransformToDevice.M22;
        return new Rect(tl.X / sx, tl.Y / sy, Pill.ActualWidth, Pill.ActualHeight);
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

    private async void OnPrev(object s, RoutedEventArgs e)      { if (_media != null) await _media.PreviousAsync(); }
    private async void OnNext(object s, RoutedEventArgs e)      { if (_media != null) await _media.NextAsync(); }
    private async void OnPlayPause(object s, RoutedEventArgs e) { if (_media != null) await _media.TogglePlayPauseAsync(); }

    // ======================================================================
    //  Dragging (any monitor) + placement
    // ======================================================================

    // Manual drag instead of Window.DragMove(): DragMove runs a modal OS move
    // loop that fires LocationChanged in a storm; combined with SnapToTaskbar it
    // re-entered layout and crashed. This moves the window on X only (Y is owned
    // by SnapToTaskbar) using the absolute cursor position, so it's smooth and
    // DPI-stable and never touches layout per move.
    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;

        var src = PresentationSource.FromVisual(this);
        _dragDip = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        _dragStartCursorX = p.X;
        _dragStartLeft = Left;
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
        if (src?.CompositionTarget != null) _dragDip = src.CompositionTarget.TransformFromDevice.M11;

        double newLeft = _dragStartLeft + (p.X - _dragStartCursorX) * _dragDip;
        newLeft = ApplyMonitorResistance(newLeft);
        Left = ClampLeft(newLeft); // X only; Y stays put
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

        double dx = src.CompositionTarget.TransformFromDevice.M11;
        double dy = src.CompositionTarget.TransformFromDevice.M22;
        left = mi.rcMonitor.Left * dx; right = mi.rcMonitor.Right * dx;
        top = mi.rcMonitor.Top * dy; bottom = mi.rcMonitor.Bottom * dy;
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
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) && OnAnyScreen(_settings.Left, _settings.Top))
        {
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else
        {
            // Default: bottom-right, just above the taskbar area.
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 8;
        }
    }

    // Reject a saved position that no longer lands on a real monitor (unplugged
    // display, changed layout) so the pill can't be lost off-screen.
    private static bool OnAnyScreen(double left, double top)
    {
        var pt = new NativeMethods.POINT { X = (int)(left + 20), Y = (int)(top + 20) };
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
    {
        void Set(string key, Color c) => Resources[key] = new SolidColorBrush(c);

        if (light)
        {
            Set("GlassBrush",    Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            Set("StrokeBrush",   Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            Set("TextPrimary",   Color.FromRgb(0x10, 0x10, 0x10));
            Set("TextSecondary", Color.FromArgb(0xB0, 0x10, 0x10, 0x10));
            Set("IconHover",     Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            Set("IconPressed",   Color.FromArgb(0x28, 0x00, 0x00, 0x00));
            Set("AccentBrush",   Color.FromRgb(0x00, 0x67, 0xC0));
        }
        // Dark defaults already live in App.xaml.
    }

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
        else if (_isIdle) _cat.StartIdle(GetPillScreenRectDip);
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
        PersistPosition();
        _topmost.Stop();
        _fsWatch.Stop();
        _volumeHide.Stop();
        _saveDebounce.Stop();
        CompositionTarget.Rendering -= OnRendering;
        _cat.StopIdle();          // stop before the window it depends on goes away
        _tray?.Dispose();
        _scrollHook?.Dispose();
        _media?.Dispose();
        _audio.Dispose();
        _volume.Dispose();
        Application.Current.Shutdown();
    }
}
