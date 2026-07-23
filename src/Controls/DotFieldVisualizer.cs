using System.Windows;
using System.Windows.Media;
using TaskbarMusic.Services;

namespace TaskbarMusic.Controls;

/// <summary>
/// The pill's living background: a halftone field of palette-tinted dots plus a
/// few twinkling sparkles (inspired by the Gemini prompt box). It's brightest in
/// the centre band and fades toward the corners, and its overall intensity +
/// dot size ride the audio level. Colours ease toward the current track's
/// palette. Retained-mode <see cref="OnRender"/> drawing with a frozen-brush
/// cache keeps it cheap despite hundreds of dots.
/// </summary>
public sealed class DotFieldVisualizer : FrameworkElement
{
    private const double Spacing = 11;     // px between dots
    private const double BaseRadius = 1.25;
    private const int SparkCount = 9;

    // Externally driven each frame.
    public float Level { get; set; }        // 0..1 audio energy
    public Color Target1 { get; set; } = Color.FromRgb(0x7B, 0x5C, 0xF6);
    public Color Target2 { get; set; } = Color.FromRgb(0xB0, 0x4C, 0xE0);

    // Colours we actually render (eased toward the targets).
    private Color _c1 = Color.FromRgb(0x7B, 0x5C, 0xF6);
    private Color _c2 = Color.FromRgb(0xB0, 0x4C, 0xE0);

    private double _t;
    private readonly Random _rng = new();
    private Spark[] _sparks = Array.Empty<Spark>();
    private double _sparkW, _sparkH;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    private struct Spark { public double X, Y, Phase, Speed, Size; }

    // ---- Random colour splash blobs (only visible while audio is playing) -----
    private const int BlobCount = 6;
    private static readonly Color[] BlobPalette =
    {
        Color.FromRgb(0x4F, 0xC3, 0xF7), // sky blue
        Color.FromRgb(0x34, 0xC7, 0xA7), // teal
        Color.FromRgb(0x7D, 0xD8, 0x7D), // green
        Color.FromRgb(0xFF, 0xB8, 0x4D), // amber
        Color.FromRgb(0xFF, 0x7A, 0x59), // coral
        Color.FromRgb(0x45, 0xD9, 0xE8), // cyan
        Color.FromRgb(0x6C, 0x8C, 0xFF), // periwinkle
        Color.FromRgb(0x6F, 0xE3, 0xB0), // mint
        Color.FromRgb(0xF6, 0xC1, 0x77), // gold
        Color.FromRgb(0xFF, 0x9E, 0x64), // warm orange
    };

    private struct Blob { public double X, Y, R, Life, Speed, Delay; public int Ci; }
    private Blob[] _blobs = Array.Empty<Blob>();
    private readonly Dictionary<int, RadialGradientBrush> _blobBrush = new();

    /// <summary>Advance time by dt seconds and request a repaint.</summary>
    public void Tick(double dt)
    {
        _t += dt;
        // Ease rendered colours toward the track palette (smooth recolour).
        double k = Math.Clamp(dt * 3.0, 0, 1);
        _c1 = ColorHelper.Lerp(_c1, Target1, k);
        _c2 = ColorHelper.Lerp(_c2, Target2, k);

        // Advance the splash blobs; respawn (with a random gap) when they finish.
        for (int i = 0; i < _blobs.Length; i++)
        {
            if (_blobs[i].Delay > 0) { _blobs[i].Delay -= dt; continue; }
            _blobs[i].Life += _blobs[i].Speed * dt;
            if (_blobs[i].Life >= 1)
                _blobs[i] = NewBlob(_sparkW, _sparkH, 0.2 + _rng.NextDouble() * 2.5);
        }

        InvalidateVisual();
    }

    private void EnsureBlobs(double w, double h)
    {
        if (_blobs.Length == BlobCount && _sparkW == w && _sparkH == h) return;
        _blobs = new Blob[BlobCount];
        for (int i = 0; i < BlobCount; i++)
            _blobs[i] = NewBlob(w, h, _rng.NextDouble() * 3.0); // staggered first appearance
    }

    private Blob NewBlob(double w, double h, double delay) => new()
    {
        X = _rng.NextDouble() * w,
        Y = _rng.NextDouble() * h,
        R = Math.Max(10, h) * (0.45 + _rng.NextDouble() * 0.55),
        Life = 0,
        Speed = 0.35 + _rng.NextDouble() * 0.45, // ~1.4..2.9s lifetime
        Ci = _rng.Next(BlobPalette.Length),
        Delay = delay,
    };

    private RadialGradientBrush BlobBrush(int ci)
    {
        if (!_blobBrush.TryGetValue(ci, out var br))
        {
            var c = BlobPalette[ci];
            br = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xB0, c.R, c.G, c.B), 0.0),
                    new GradientStop(Color.FromArgb(0x48, c.R, c.G, c.B), 0.5),
                    new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1.0),
                },
            };
            br.Freeze();
            _blobBrush[ci] = br;
        }
        return br;
    }

    private void EnsureSparks(double w, double h)
    {
        if (_sparks.Length == SparkCount && _sparkW == w && _sparkH == h) return;
        _sparkW = w; _sparkH = h;
        _sparks = new Spark[SparkCount];
        for (int i = 0; i < SparkCount; i++)
            _sparks[i] = new Spark
            {
                X = _rng.NextDouble() * w,
                Y = _rng.NextDouble() * h,
                Phase = _rng.NextDouble() * Math.PI * 2,
                Speed = 1.5 + _rng.NextDouble() * 2.5,
                Size = 2.2 + _rng.NextDouble() * 2.0,
            };
    }

    private SolidColorBrush Brush(byte a, Color c)
    {
        // Quantise to keep the cache small; brushes are frozen and reused forever.
        a = (byte)(a & 0xF0);
        byte r = (byte)(c.R & 0xF0), g = (byte)(c.G & 0xF0), b = (byte)(c.B & 0xF0);
        uint key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        if (!_brushes.TryGetValue(key, out var br))
        {
            br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            _brushes[key] = br;
        }
        return br;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        EnsureSparks(w, h);
        EnsureBlobs(w, h);

        double cx = w / 2, cy = h / 2;
        float level = Math.Clamp(Level, 0, 1);
        double baseline = 0.12; // faint life even in silence

        // Colour splash blobs, behind the dots. Gated by level so that with NO
        // system audio they vanish entirely and the field looks exactly as before.
        double blobGate = Math.Clamp((level - 0.02) * 1.4, 0, 1);
        if (blobGate > 0.01)
        {
            foreach (var b in _blobs)
            {
                if (b.Delay > 0) continue;
                double fade = Math.Sin(Math.Clamp(b.Life, 0, 1) * Math.PI); // 0 -> 1 -> 0
                double alpha = fade * blobGate * 0.65;
                if (alpha < 0.02) continue;
                dc.PushOpacity(alpha);
                dc.DrawEllipse(BlobBrush(b.Ci), null, new Point(b.X, b.Y), b.R, b.R);
                dc.Pop();
            }
        }

        for (double y = Spacing / 2; y < h; y += Spacing)
        {
            for (double x = Spacing / 2; x < w; x += Spacing)
            {
                double nx = (x - cx) / (w / 2);
                double ny = (y - cy) / (h / 2);
                // Feather toward the edges so dots dissolve instead of being clipped.
                double d = Math.Sqrt(nx * nx * 0.92 + ny * ny * 1.5);
                double edge = 1 - d;
                if (edge <= 0) continue;
                edge = edge * edge * (3 - 2 * edge); // smoothstep -> soft "lost" fade

                double shimmer = 0.55 + 0.45 * Math.Sin(_t * 2.3 + x * 0.16 + y * 0.22);
                double intensity = edge * (baseline + (1 - baseline) * level) * shimmer;
                if (intensity < 0.03) continue;

                Color c = ColorHelper.Lerp(_c1, _c2, x / w);
                byte a = (byte)(Math.Clamp(intensity, 0, 1) * 205);
                // Extra size swing that only kicks in with audio (level==0 -> unchanged),
                // so the dots visibly "pump" more when something is playing.
                double r = BaseRadius + intensity * 1.7 + level * intensity * 1.8;
                dc.DrawEllipse(Brush(a, c), null, new Point(x, y), r, r);
            }
        }

        // Sparkles on top.
        foreach (var s in _sparks)
        {
            double tw = 0.5 + 0.5 * Math.Sin(_t * s.Speed + s.Phase);
            double intensity = tw * (0.30 + 0.70 * level);
            if (intensity < 0.06) continue;
            Color c = ColorHelper.Lerp(_c1, _c2, s.X / w);
            DrawDiamond(dc, s.X, s.Y, s.Size * (0.7 + 0.7 * intensity),
                Brush((byte)(Math.Clamp(intensity, 0, 1) * 235), c));
        }
    }

    private static void DrawDiamond(DrawingContext dc, double x, double y, double r, Brush brush)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x, y - r), true, true);
            g.LineTo(new Point(x + r, y), true, false);
            g.LineTo(new Point(x, y + r), true, false);
            g.LineTo(new Point(x - r, y), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }
}
