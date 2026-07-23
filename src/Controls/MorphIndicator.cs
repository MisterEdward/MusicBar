using System.Windows;
using System.Windows.Media;
using TaskbarMusic.Services;

namespace TaskbarMusic.Controls;

/// <summary>
/// The idle indicator: a small blob that morphs between shapes
/// (circle → triangle → square → pentagon → hexagon → star → …) with liquid
/// easing, drifting through a curated, tasteful palette. Driven per-frame by the
/// window's render loop while idle.
/// </summary>
public sealed class MorphIndicator : FrameworkElement
{
    private const int N = 72;          // boundary sample count
    private const double StepDur = 2.6; // seconds per shape
    private const double Dwell = 1.3;   // hold before morphing to the next

    // Hand-picked, pleasant colours (no eggplant/hot-pink).
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x5A, 0xC8, 0xFA), // sky blue
        Color.FromRgb(0x34, 0xC7, 0xA7), // teal
        Color.FromRgb(0x7D, 0xD8, 0x7D), // fresh green
        Color.FromRgb(0xFF, 0xB8, 0x4D), // amber
        Color.FromRgb(0xFF, 0x8A, 0x65), // coral
        Color.FromRgb(0x6C, 0x8C, 0xFF), // periwinkle
        Color.FromRgb(0x4F, 0xC3, 0xF7), // light blue
        Color.FromRgb(0xF6, 0xC1, 0x77), // soft gold
    };

    private static readonly Func<double, double>[] Shapes =
    {
        _ => 1.0,                         // circle
        th => Polygon(th, 3),             // triangle
        th => Polygon(th, 4),             // square/diamond
        th => Polygon(th, 5),             // pentagon
        th => Polygon(th, 6),             // hexagon
        th => Star(th, 5),                // star
    };

    private double _t;

    public void Tick(double dt)
    {
        _t += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double phase = _t / StepDur;
        int idx = (int)phase;
        double frac = phase - idx;
        double dwellFrac = Dwell / StepDur;
        double morph = frac <= dwellFrac ? 0 : Smooth((frac - dwellFrac) / (1 - dwellFrac));

        var shapeA = Shapes[idx % Shapes.Length];
        var shapeB = Shapes[(idx + 1) % Shapes.Length];
        Color colA = Palette[idx % Palette.Length];
        Color colB = Palette[(idx + 1) % Palette.Length];
        Color color = ColorHelper.Lerp(colA, colB, morph);

        double cx = w / 2, cy = h / 2;
        double radius = Math.Min(w, h) * 0.34;
        double rot = _t * 0.35; // slow spin

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i <= N; i++)
            {
                double baseA = i / (double)N * Math.PI * 2;
                double r = Lerp(shapeA(baseA), shapeB(baseA), morph) * radius;
                double a = baseA + rot;
                var p = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
                if (i == 0) g.BeginFigure(p, true, true);
                else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();

        // Soft outer glow + solid core for a glassy feel.
        var glow = Frozen(Color.FromArgb(0x40, color.R, color.G, color.B));
        var core = Frozen(Color.FromArgb(0xF0, color.R, color.G, color.B));
        dc.PushTransform(new ScaleTransform(1.18, 1.18, cx, cy));
        dc.DrawGeometry(glow, null, geo);
        dc.Pop();
        dc.DrawGeometry(core, null, geo);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>Radius (0..~1) of a regular n-gon boundary at angle th.</summary>
    private static double Polygon(double th, int n)
    {
        double a = 2 * Math.PI / n;
        double m = ((th % a) + a) % a;
        return Math.Cos(a / 2) / Math.Cos(m - a / 2);
    }

    /// <summary>Radius of a star with the given point count.</summary>
    private static double Star(double th, int points)
    {
        double a = 2 * Math.PI / points;
        double m = ((th % a) + a) % a;
        double frac = m / a;                 // 0..1 across one point
        double tri = Math.Abs(2 * frac - 1);  // 1 at vertex, 0 at valley
        return 0.5 + 0.5 * tri;               // inner 0.5 .. outer 1.0
    }
}
