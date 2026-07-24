using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMusic.Interop;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic;

/// <summary>
/// A geometric motion-branding cat that makes one short visit after a
/// play-to-idle transition. It is click-through and never focus-stealing.
/// </summary>
public partial class CatWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private static readonly TimeSpan AppearanceDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VisitDuration = TimeSpan.FromSeconds(30);

    private readonly Random _rng = new();
    private readonly TranslateTransform _zzzShift = new();
    private readonly TranslateTransform _hop = new();

    private Func<CatAnchor?>? _getPillAnchor;
    private Func<CatAnchor?>? _pendingPillAnchor;
    private CancellationTokenSource? _visitCancellation;
    private bool _onStage;
    private double _offstageX = -150;
    private VisitState _state;

    private enum VisitState
    {
        Dormant,
        Waiting,
        Entering,
        Performing,
        Exiting,
    }

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

    internal void StartIdle(Func<CatAnchor?> getPillAnchor)
    {
        ArgumentNullException.ThrowIfNull(getPillAnchor);
        Dispatcher.VerifyAccess();

        if (_state != VisitState.Dormant)
        {
            // A new play→stop can arrive while the canceled visit is still
            // animating out. Keep exactly one replacement and start it only
            // after the old state machine has fully returned to Dormant.
            if (_visitCancellation?.IsCancellationRequested == true)
                _pendingPillAnchor = getPillAnchor;
            return;
        }

        _getPillAnchor = getPillAnchor;
        _visitCancellation = new CancellationTokenSource();
        _ = RunVisitAsync(_visitCancellation);
    }

    public void StopIdle()
    {
        Dispatcher.VerifyAccess();
        _pendingPillAnchor = null;
        _visitCancellation?.Cancel();
    }

    // ---- State machine ----------------------------------------------------

    private async Task RunVisitAsync(CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            _state = VisitState.Waiting;
            await Task.Delay(AppearanceDelay, token);

            token.ThrowIfCancellationRequested();
            if (_getPillAnchor is null || !TryPositionBesidePill())
                return;

            _state = VisitState.Entering;
            await EnterAsync(token);

            _state = VisitState.Performing;
            await PlayBehaviorAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Playback resumed or the app is shutting down.
        }
        catch (Exception ex)
        {
            App.LogException("Idle cat visit", ex);
        }
        finally
        {
            try
            {
                if (_onStage)
                {
                    _state = VisitState.Exiting;
                    await ExitAsync(fast: token.IsCancellationRequested);
                }
            }
            catch (Exception ex)
            {
                App.LogException("Idle cat exit", ex);
            }

            if (ReferenceEquals(_visitCancellation, owner))
            {
                var rearm = _pendingPillAnchor;
                _pendingPillAnchor = null;
                _visitCancellation = null;
                _getPillAnchor = null;
                _state = VisitState.Dormant;

                if (rearm is not null)
                    StartIdle(rearm);
            }
            owner.Dispose();
        }
    }

    // ---- Choreography -----------------------------------------------------

    private async Task EnterAsync(CancellationToken token)
    {
        _onStage = true;
        Slide.X = _offstageX;
        Opacity = 1;
        StartBreathing();
        await AnimateAsync(Slide, TranslateTransform.XProperty, 0,
            TimeSpan.FromMilliseconds(650),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }, token);
    }

    private async Task PlayBehaviorAsync(CancellationToken token)
    {
        // Single ~30s visit: a little intro (sleep or play), then settle down.
        bool sleep = _rng.Next(2) == 0;

        if (sleep) { ShowZzz(true); StartTailWag(gentle: true); }
        else
        {
            StartTailWag(gentle: false);
            for (int i = 0; i < _rng.Next(2, 4); i++)
                await HopAsync(token);
            StartTailWag(gentle: true); // calm down after the hops
            ShowZzz(true);
        }

        await Task.Delay(VisitDuration, token);

        ShowZzz(false);
        StopTailWag();
    }

    private async Task ExitAsync(bool fast)
    {
        if (!_onStage) return;
        ShowZzz(false);
        await AnimateAsync(Slide, TranslateTransform.XProperty, _offstageX,
            TimeSpan.FromMilliseconds(fast ? 260 : 520),
            new CubicEase { EasingMode = EasingMode.EaseIn }, CancellationToken.None);
        Opacity = 0;
        StopBreathing();
        StopTailWag();
        _onStage = false;
    }

    private bool TryPositionBesidePill()
    {
        CatAnchor? anchor = _getPillAnchor!.Invoke();
        if (anchor is null)
            return false;

        double scale = Math.Max(1, anchor.Value.Dpi) / 96.0;
        int widthPx = Math.Max(1, (int)Math.Round(Width * scale));
        int heightPx = Math.Max(1, (int)Math.Round(Height * scale));
        if (!CatPlacement.TryResolve(
            anchor.Value.Pill, anchor.Value.Monitor, widthPx, heightPx, out var placement))
            return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        PixelRect bounds = placement.Bounds;
        if (!SetWindowPos(
            hwnd, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE))
            return false;

        _offstageX = placement.EntersFromLeft ? -Width : Width;
        return true;
    }

    private async Task HopAsync(CancellationToken token)
    {
        await AnimateAsync(_hop, TranslateTransform.YProperty, -10,
            TimeSpan.FromMilliseconds(160), new SineEase { EasingMode = EasingMode.EaseOut }, token);
        await AnimateAsync(_hop, TranslateTransform.YProperty, 0,
            TimeSpan.FromMilliseconds(220), new BounceEase { Bounces = 1, Bounciness = 3 }, token);
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
        TimeSpan dur, IEasingFunction ease, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var anim = new DoubleAnimation(to, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        EventHandler? completed = null;
        CancellationTokenRegistration registration = default;

        completed = (_, _) =>
        {
            anim.Completed -= completed;
            registration.Dispose();
            tcs.TrySetResult();
        };
        anim.Completed += completed;

        if (token.CanBeCanceled)
        {
            registration = token.Register(() =>
            {
                anim.Completed -= completed;
                target.BeginAnimation(prop, null);
                tcs.TrySetCanceled(token);
            });
        }

        target.BeginAnimation(prop, anim);
        return tcs.Task;
    }
}
