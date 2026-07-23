using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic;

/// <summary>
/// A whimsical idle companion: a vector cat that occasionally slides in from
/// off-screen next to the pill, does a little sleep/play bit, and slides away.
/// Click-through and never focus-stealing, so it's purely decorative.
///
/// Want a real GIF instead? Drop a MediaElement over the Canvas in
/// CatWindow.xaml and drive it from <see cref="PlayBehaviorAsync"/>.
/// </summary>
public partial class CatWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x20;

    private readonly Random _rng = new();
    private readonly DispatcherTimer _scheduler = new();
    private readonly TranslateTransform _zzzShift = new();
    private readonly TranslateTransform _hop = new();

    private Func<Rect>? _getPillRect;
    private bool _idle;
    private bool _onStage;

    public CatWindow()
    {
        InitializeComponent();

        // Compose hop (play bounce) with the XAML "breathe" scale, once.
        var group = new TransformGroup();
        group.Children.Add(_hop);
        group.Children.Add(Breathe);
        CatBody.RenderTransform = group;

        Zzz.RenderTransform = _zzzShift;
        Opacity = 0;
        _scheduler.Tick += async (_, _) => { _scheduler.Stop(); await MaybeAppearAsync(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; // click-through
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    // ---- Public control ---------------------------------------------------

    public void StartIdle(Func<Rect> getPillRect)
    {
        _getPillRect = getPillRect;
        if (_idle) return;
        _idle = true;
        ScheduleNext(firstTime: true);
    }

    public void StopIdle()
    {
        _idle = false;
        _scheduler.Stop();
        if (_onStage) _ = ExitAsync(fast: true);
    }

    // ---- Scheduling -------------------------------------------------------

    private void ScheduleNext(bool firstTime)
    {
        _scheduler.Interval = firstTime
            ? TimeSpan.FromSeconds(_rng.Next(4, 9))
            : TimeSpan.FromSeconds(_rng.Next(28, 75));
        _scheduler.Start();
    }

    private async Task MaybeAppearAsync()
    {
        if (!_idle || _onStage || _getPillRect is null) return;
        await EnterAsync();
        if (_idle) await PlayBehaviorAsync();
        await ExitAsync(fast: false);
        // One-shot: do NOT reschedule. The cat only returns after the next
        // play -> stop transition (StopIdle/StartIdle re-arm it).
    }

    // ---- Choreography -----------------------------------------------------

    private async Task EnterAsync()
    {
        _onStage = true;
        PositionBesidePill();
        Slide.X = -Width;
        Opacity = 1;
        StartBreathing();
        await AnimateAsync(Slide, TranslateTransform.XProperty, 0,
            TimeSpan.FromMilliseconds(650),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });
    }

    private async Task PlayBehaviorAsync()
    {
        // Single ~30s visit: a little intro (sleep or play), then settle down.
        var until = DateTime.UtcNow.AddSeconds(30);
        bool sleep = _rng.Next(2) == 0;

        if (sleep) { ShowZzz(true); StartTailWag(gentle: true); }
        else
        {
            StartTailWag(gentle: false);
            for (int i = 0; i < _rng.Next(2, 4) && _idle; i++) await HopAsync();
            StartTailWag(gentle: true); // calm down after the hops
            ShowZzz(true);
        }

        // Stay for the rest of the 30s (bail immediately if playback resumes).
        while (_idle && DateTime.UtcNow < until)
            await Task.Delay(250);

        ShowZzz(false);
        StopTailWag();
    }

    private async Task ExitAsync(bool fast)
    {
        if (!_onStage) return;
        ShowZzz(false);
        await AnimateAsync(Slide, TranslateTransform.XProperty, -Width,
            TimeSpan.FromMilliseconds(fast ? 260 : 520),
            new CubicEase { EasingMode = EasingMode.EaseIn });
        Opacity = 0;
        StopBreathing();
        StopTailWag();
        _onStage = false;
    }

    private void PositionBesidePill()
    {
        var r = _getPillRect!.Invoke();
        Left = r.Left - Width + 12;      // just left of the pill
        Top = r.Bottom - Height + 4;     // standing on its baseline
    }

    private async Task HopAsync()
    {
        await AnimateAsync(_hop, TranslateTransform.YProperty, -10,
            TimeSpan.FromMilliseconds(160), new SineEase { EasingMode = EasingMode.EaseOut });
        await AnimateAsync(_hop, TranslateTransform.YProperty, 0,
            TimeSpan.FromMilliseconds(220), new BounceEase { Bounces = 1, Bounciness = 3 });
    }

    // ---- Ambient loops (direct property animations) -----------------------

    private static DoubleAnimation Forever(double from, double to, int ms) => new(from, to, TimeSpan.FromMilliseconds(ms))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    private void StartBreathing()
    {
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, Forever(1.0, 1.045, 1600));
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, Forever(1.0, 1.012, 1600));
    }

    private void StopBreathing()
    {
        Breathe.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Breathe.BeginAnimation(ScaleTransform.ScaleXProperty, null);
    }

    private void StartTailWag(bool gentle) =>
        TailRot.BeginAnimation(RotateTransform.AngleProperty,
            Forever(gentle ? -4 : -14, gentle ? 4 : 14, gentle ? 1400 : 460));

    private void StopTailWag() => TailRot.BeginAnimation(RotateTransform.AngleProperty, null);

    private void ShowZzz(bool on)
    {
        if (!on)
        {
            Zzz.BeginAnimation(OpacityProperty, null);
            _zzzShift.BeginAnimation(TranslateTransform.YProperty, null);
            Zzz.Opacity = 0;
            return;
        }
        Zzz.Opacity = 1;
        Zzz.BeginAnimation(OpacityProperty, Forever(0.25, 1.0, 900));
        _zzzShift.BeginAnimation(TranslateTransform.YProperty, Forever(4, -6, 1800));
    }

    // ---- Animation plumbing ----------------------------------------------

    private static Task AnimateAsync(IAnimatable target, DependencyProperty prop, double to,
        TimeSpan dur, IEasingFunction ease)
    {
        var tcs = new TaskCompletionSource();
        var anim = new DoubleAnimation(to, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        anim.Completed += (_, _) => tcs.TrySetResult();
        target.BeginAnimation(prop, anim);
        return tcs.Task;
    }
}
