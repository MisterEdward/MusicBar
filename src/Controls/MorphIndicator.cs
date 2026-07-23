using System.Windows;
using System.Windows.Media;
using TaskbarMusic.Services;

namespace TaskbarMusic.Controls;

/// <summary>
/// A compact motion-branding mark for the idle pill. Shapes hold briefly, then
/// morph decisively into one another, matching the cadence of the supplied
/// reference animation instead of continuously wobbling or rotating.
/// </summary>
public sealed class MorphIndicator : FrameworkElement
{
    private const int PointCount = 96;
    private const double HoldSeconds = 1.05;
    private const double MorphSeconds = 0.38;

    private static readonly ShapeFrame[] Frames =
    {
        new(TripleLobe(),     Color.FromRgb(0xB5, 0x1F, 0x4B)), // burgundy
        new(VerticalPill(),   Color.FromRgb(0x1F, 0x5A, 0xF6)), // cobalt
        new(FourLeaf(),       Color.FromRgb(0x20, 0xB2, 0xA6)), // teal
        new(RoundedDiamond(), Color.FromRgb(0xFF, 0xC3, 0x3A)), // saffron
        new(RoundedTriangle(),Color.FromRgb(0xFF, 0x5B, 0x1A)), // coral
        new(RoundedSquare(),  Color.FromRgb(0xB5, 0x1F, 0x4B)), // burgundy
        new(Circle(),         Color.FromRgb(0x1F, 0x5A, 0xF6)), // cobalt
        new(SoftDrop(),       Color.FromRgb(0x20, 0xB2, 0xA6)), // teal
    };

    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private double _time;

    public void Tick(double dt)
    {
        _time += Math.Clamp(dt, 0, 0.1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        MorphTimelineState timeline = MorphTimeline.GetState(
            _time, Frames.Length, HoldSeconds, MorphSeconds);
        double rawMorph = timeline.RawProgress;
        double morph = timeline.Progress;

        ShapeFrame current = Frames[timeline.CurrentIndex];
        ShapeFrame next = Frames[timeline.NextIndex];
        Color color = ColorHelper.Lerp(current.Color, next.Color, morph);

        // The reference briefly compresses shapes during a transition. A small
        // pulse makes the change feel intentional without the old cartoon bounce.
        double transitionPulse = Math.Sin(Math.Clamp(rawMorph, 0, 1) * Math.PI);
        double scale = 1.0 - transitionPulse * 0.045;
        var geometry = BuildGeometry(
            current.Points, next.Points, morph, scale, width, height, yOffset: 0);

        // A restrained one-pixel grounding shadow. No bloom/glow: the supplied
        // visual language is flat, crisp, and saturated.
        dc.PushTransform(new TranslateTransform(0, Math.Max(0.7, height * 0.018)));
        dc.DrawGeometry(Brush(0x2C, Colors.Black), null, geometry);
        dc.Pop();
        dc.DrawGeometry(Brush(0xF4, color), null, geometry);
    }

    private static StreamGeometry BuildGeometry(
        Point[] from,
        Point[] to,
        double progress,
        double transitionScale,
        double width,
        double height,
        double yOffset)
    {
        double radius = Math.Min(width, height) * 0.335 * transitionScale;
        double centerX = width / 2;
        double centerY = height / 2 + yOffset;
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            for (int i = 0; i <= PointCount; i++)
            {
                int pointIndex = i % PointCount;
                Point a = from[pointIndex];
                Point b = to[pointIndex];
                var point = new Point(
                    centerX + (a.X + (b.X - a.X) * progress) * radius,
                    centerY + (a.Y + (b.Y - a.Y) * progress) * radius);

                if (i == 0)
                    context.BeginFigure(point, isFilled: true, isClosed: true);
                else
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private SolidColorBrush Brush(byte alpha, Color color)
    {
        // Quantisation keeps the short colour transitions from allocating a new
        // frozen brush every frame.
        byte r = (byte)(color.R & 0xFC);
        byte g = (byte)(color.G & 0xFC);
        byte b = (byte)(color.B & 0xFC);
        uint key = ((uint)alpha << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        if (_brushCache.TryGetValue(key, out var existing))
            return existing;

        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        _brushCache[key] = brush;
        return brush;
    }

    private static Point[] Circle() =>
        Sample(theta => new Point(Math.Cos(theta), Math.Sin(theta)));

    private static Point[] VerticalPill() =>
        Sample(theta => Superellipse(theta, exponent: 5.8, scaleX: 0.68, scaleY: 1.0));

    private static Point[] RoundedSquare() =>
        Sample(theta => Superellipse(theta, exponent: 4.8, scaleX: 0.90, scaleY: 0.90));

    private static Point[] RoundedDiamond() =>
        AlignAtTop(Rotate(RoundedSquare(), Math.PI / 4));

    private static Point[] RoundedTriangle()
        => Normalize(RoundedRegularPolygon(sides: 3, rounding: 0.30), 0.94);

    private static Point[] SoftDrop()
    {
        Point[] triangle = RoundedTriangle();
        for (int i = 0; i < triangle.Length; i++)
        {
            // Broader shoulders and a softer base than the triangle.
            double y = triangle[i].Y;
            triangle[i] = new Point(
                triangle[i].X * (y < 0 ? 0.82 : 1.02),
                y * (y < 0 ? 1.03 : 0.92));
        }
        return Normalize(Smooth(triangle, passes: 2), 0.95);
    }

    private static Point[] FourLeaf()
    {
        var ellipses = new[]
        {
            new Ellipse(-0.34, -0.34, 0.67, 0.67),
            new Ellipse( 0.34, -0.34, 0.67, 0.67),
            new Ellipse( 0.34,  0.34, 0.67, 0.67),
            new Ellipse(-0.34,  0.34, 0.67, 0.67),
        };
        return Normalize(Sample(theta => EllipseUnionBoundary(theta, ellipses)), 0.96);
    }

    private static Point[] TripleLobe()
    {
        var ellipses = new[]
        {
            new Ellipse(-0.48, 0, 0.54, 0.92),
            new Ellipse( 0.00, 0, 0.54, 0.92),
            new Ellipse( 0.48, 0, 0.54, 0.92),
        };
        return Normalize(Sample(theta => EllipseUnionBoundary(theta, ellipses)), 0.96);
    }

    private static Point EllipseUnionBoundary(double theta, IReadOnlyList<Ellipse> ellipses)
    {
        double dx = Math.Cos(theta);
        double dy = Math.Sin(theta);
        double farthest = 0;

        foreach (var ellipse in ellipses)
        {
            double invX = 1.0 / (ellipse.RadiusX * ellipse.RadiusX);
            double invY = 1.0 / (ellipse.RadiusY * ellipse.RadiusY);
            double a = dx * dx * invX + dy * dy * invY;
            double b = -2 * (ellipse.CenterX * dx * invX + ellipse.CenterY * dy * invY);
            double c = ellipse.CenterX * ellipse.CenterX * invX +
                       ellipse.CenterY * ellipse.CenterY * invY - 1;
            double discriminant = b * b - 4 * a * c;
            if (discriminant < 0) continue;

            double radius = (-b + Math.Sqrt(discriminant)) / (2 * a);
            if (radius > farthest) farthest = radius;
        }

        return new Point(dx * farthest, dy * farthest);
    }

    private static Point Superellipse(
        double theta,
        double exponent,
        double scaleX,
        double scaleY)
    {
        double power = 2.0 / exponent;
        double cosine = Math.Cos(theta);
        double sine = Math.Sin(theta);
        return new Point(
            scaleX * Math.Sign(cosine) * Math.Pow(Math.Abs(cosine), power),
            scaleY * Math.Sign(sine) * Math.Pow(Math.Abs(sine), power));
    }

    private static Point[] Sample(Func<double, Point> sampler)
    {
        var points = new Point[PointCount];
        for (int i = 0; i < points.Length; i++)
            points[i] = sampler(i / (double)PointCount * Math.PI * 2 - Math.PI / 2);
        return points;
    }

    private static Point[] RoundedRegularPolygon(int sides, double rounding)
    {
        if (PointCount % (sides * 2) != 0)
            throw new InvalidOperationException("Point count must divide evenly across polygon segments.");

        var vertices = new Point[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI * 2 / sides;
            vertices[i] = new Point(Math.Cos(angle), Math.Sin(angle));
        }

        int samplesPerPart = PointCount / (sides * 2);
        var points = new List<Point>(PointCount);
        for (int i = 0; i < sides; i++)
        {
            Point previous = vertices[(i - 1 + sides) % sides];
            Point vertex = vertices[i];
            Point next = vertices[(i + 1) % sides];
            Point incoming = Lerp(vertex, previous, rounding);
            Point outgoing = Lerp(vertex, next, rounding);
            Point nextIncoming = Lerp(next, vertex, rounding);

            for (int sample = 0; sample < samplesPerPart; sample++)
            {
                double t = sample / (double)samplesPerPart;
                double inverse = 1 - t;
                points.Add(new Point(
                    inverse * inverse * incoming.X + 2 * inverse * t * vertex.X + t * t * outgoing.X,
                    inverse * inverse * incoming.Y + 2 * inverse * t * vertex.Y + t * t * outgoing.Y));
            }

            for (int sample = 0; sample < samplesPerPart; sample++)
            {
                double t = sample / (double)samplesPerPart;
                points.Add(Lerp(outgoing, nextIncoming, t));
            }
        }

        return AlignAtTop(points);
    }

    private static Point[] AlignAtTop(IReadOnlyList<Point> points)
    {
        // Align every contour at its topmost point so interpolation does not twist.
        int topIndex = 0;
        for (int i = 1; i < points.Count; i++)
            if (points[i].Y < points[topIndex].Y) topIndex = i;

        var aligned = new Point[PointCount];
        for (int i = 0; i < aligned.Length; i++)
            aligned[i] = points[(topIndex + i) % points.Count];
        return aligned;
    }

    private static Point Lerp(Point from, Point to, double progress) =>
        new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);

    private static Point[] Smooth(Point[] input, int passes)
    {
        Point[] current = input;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = new Point[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                Point previous = current[(i - 1 + current.Length) % current.Length];
                Point point = current[i];
                Point following = current[(i + 1) % current.Length];
                next[i] = new Point(
                    (previous.X + point.X * 2 + following.X) / 4,
                    (previous.Y + point.Y * 2 + following.Y) / 4);
            }
            current = next;
        }
        return current;
    }

    private static Point[] Rotate(Point[] input, double radians)
    {
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        var result = new Point[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            result[i] = new Point(
                input[i].X * cosine - input[i].Y * sine,
                input[i].X * sine + input[i].Y * cosine);
        }
        return result;
    }

    private static Point[] Normalize(Point[] input, double target)
    {
        double extent = input.Max(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y)));
        if (extent <= 0) return input;
        double scale = target / extent;
        for (int i = 0; i < input.Length; i++)
            input[i] = new Point(input[i].X * scale, input[i].Y * scale);
        return input;
    }

    private readonly record struct ShapeFrame(Point[] Points, Color Color);
    private readonly record struct Ellipse(
        double CenterX,
        double CenterY,
        double RadiusX,
        double RadiusY);
}
