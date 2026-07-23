namespace TaskbarMusic.Interop;

/// <summary>
/// Platform-neutral screen geometry. Coordinates are physical screen pixels.
/// Keeping this logic free of WPF and Win32 makes the edge cases testable.
/// </summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

internal enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

internal readonly record struct TaskbarGeometryResult(PixelRect Bounds, TaskbarEdge Edge);

internal static class TaskbarGeometry
{
    /// <summary>
    /// Resolves a taskbar edge and an on-screen strip. The shell window is the
    /// strongest signal (including auto-hide); the work area is the fallback.
    /// </summary>
    public static bool TryResolve(
        PixelRect monitor,
        PixelRect workArea,
        PixelRect? shellTaskbar,
        int fallbackThickness,
        out TaskbarGeometryResult result,
        TaskbarEdge? knownShellEdge = null)
    {
        result = default;
        if (!monitor.IsValid) return false;

        int leftInset = Math.Max(0, workArea.Left - monitor.Left);
        int topInset = Math.Max(0, workArea.Top - monitor.Top);
        int rightInset = Math.Max(0, monitor.Right - workArea.Right);
        int bottomInset = Math.Max(0, monitor.Bottom - workArea.Bottom);

        TaskbarEdge edge;
        if (shellTaskbar is { IsValid: true } shell)
        {
            edge = knownShellEdge ?? ClosestEdge(monitor, shell);
        }
        else
        {
            int largest = Math.Max(Math.Max(leftInset, topInset), Math.Max(rightInset, bottomInset));
            if (largest <= 0) return false;

            edge = largest == leftInset ? TaskbarEdge.Left
                : largest == topInset ? TaskbarEdge.Top
                : largest == rightInset ? TaskbarEdge.Right
                : TaskbarEdge.Bottom;
        }

        int workInset = edge switch
        {
            TaskbarEdge.Left => leftInset,
            TaskbarEdge.Top => topInset,
            TaskbarEdge.Right => rightInset,
            _ => bottomInset,
        };

        int shellThickness = shellTaskbar is { IsValid: true } taskbar
            ? edge is TaskbarEdge.Left or TaskbarEdge.Right ? taskbar.Width : taskbar.Height
            : 0;

        // An auto-hidden taskbar may expose only a 1-2 px activation sliver.
        // Prefer its off-screen window's normal thickness, then the DPI-aware
        // fallback, while never allowing a nonsensical monitor-sized strip.
        int thickness = Math.Max(workInset, shellThickness);
        if (thickness < 12) thickness = Math.Max(12, fallbackThickness);
        int maximum = edge is TaskbarEdge.Left or TaskbarEdge.Right
            ? Math.Max(1, monitor.Width / 2)
            : Math.Max(1, monitor.Height / 2);
        thickness = Math.Clamp(thickness, 1, maximum);

        PixelRect bounds = edge switch
        {
            TaskbarEdge.Left => monitor with { Right = monitor.Left + thickness },
            TaskbarEdge.Top => monitor with { Bottom = monitor.Top + thickness },
            TaskbarEdge.Right => monitor with { Left = monitor.Right - thickness },
            _ => monitor with { Top = monitor.Bottom - thickness },
        };

        result = new TaskbarGeometryResult(bounds, edge);
        return true;
    }

    internal static TaskbarEdge ClosestEdge(PixelRect monitor, PixelRect taskbar)
    {
        // Taskbars span the long axis of their monitor. Restricting the choice
        // by orientation avoids a top bar being mistaken for "left" merely
        // because both happen to start at the monitor's left coordinate.
        if (taskbar.Width >= taskbar.Height)
        {
            int top = Math.Min(Math.Abs(taskbar.Top - monitor.Top),
                Math.Abs(taskbar.Bottom - monitor.Top));
            int bottom = Math.Min(Math.Abs(taskbar.Top - monitor.Bottom),
                Math.Abs(taskbar.Bottom - monitor.Bottom));
            return top <= bottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        int left = Math.Min(Math.Abs(taskbar.Left - monitor.Left),
            Math.Abs(taskbar.Right - monitor.Left));
        int right = Math.Min(Math.Abs(taskbar.Left - monitor.Right),
            Math.Abs(taskbar.Right - monitor.Right));
        return left <= right ? TaskbarEdge.Left : TaskbarEdge.Right;
    }
}

internal readonly record struct CatPlacementResult(PixelRect Bounds, bool EntersFromLeft);
internal readonly record struct CatAnchor(PixelRect Pill, PixelRect Monitor, uint Dpi);

internal static class CatPlacement
{
    /// <summary>
    /// Places the cat beside and normally above the pill. Every input and
    /// output is a physical screen pixel, so no desktop-wide DIP assumption
    /// leaks between the pill and cat HWNDs.
    /// </summary>
    public static bool TryResolve(
        PixelRect pill,
        PixelRect monitor,
        int catWidth,
        int catHeight,
        out CatPlacementResult result)
    {
        result = default;
        if (!pill.IsValid || !monitor.IsValid || catWidth <= 0 || catHeight <= 0)
            return false;

        int overlap = Math.Max(1, (int)Math.Round(catWidth * 0.08));
        int roomLeft = pill.Left - monitor.Left;
        int roomRight = monitor.Right - pill.Right;
        bool placeLeft = roomLeft >= catWidth - overlap || roomLeft >= roomRight;

        int left = placeLeft
            ? pill.Left - catWidth + overlap
            : pill.Right - overlap;
        left = Math.Clamp(left, monitor.Left, Math.Max(monitor.Left, monitor.Right - catWidth));

        int gap = Math.Max(1, (int)Math.Round(catHeight * 0.04));
        int above = pill.Bottom - catHeight + gap;
        int below = pill.Bottom + gap;
        int top = above >= monitor.Top && above + catHeight <= monitor.Bottom
            ? above
            : below;
        top = Math.Clamp(top, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - catHeight));

        result = new CatPlacementResult(
            new PixelRect(left, top, left + catWidth, top + catHeight),
            EntersFromLeft: placeLeft);
        return true;
    }
}

internal static class FullscreenGeometry
{
    public static bool CoversMonitor(PixelRect window, PixelRect monitor, int tolerance = 2)
    {
        if (!window.IsValid || !monitor.IsValid) return false;
        tolerance = Math.Max(0, tolerance);
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }
}

internal static class DpiCoordinates
{
    /// <summary>
    /// Converts one absolute screen-pixel coordinate into WPF desktop DIPs,
    /// anchored to the current HWND. This remains correct across negative
    /// origins and monitors whose scale differs from the primary monitor.
    /// </summary>
    public static double ScreenPixelToDip(
        double screenPixel, int windowPixelOrigin, double windowDipOrigin, double pixelsToDip) =>
        windowDipOrigin + (screenPixel - windowPixelOrigin) * pixelsToDip;
}

internal static class RoundedRectHitTest
{
    public static bool Contains(PixelRect rect, int x, int y, double radius)
    {
        if (!rect.Contains(x, y)) return false;

        double maxRadius = Math.Min(rect.Width, rect.Height) / 2.0;
        radius = Math.Clamp(radius, 0, maxRadius);
        if (radius <= 0) return true;

        double localX = x - rect.Left;
        double localY = y - rect.Top;
        if (localX >= radius && localX < rect.Width - radius) return true;
        if (localY >= radius && localY < rect.Height - radius) return true;

        double centerX = localX < radius ? radius : rect.Width - radius;
        double centerY = localY < radius ? radius : rect.Height - radius;
        double dx = localX - centerX;
        double dy = localY - centerY;
        return dx * dx + dy * dy <= radius * radius;
    }
}
