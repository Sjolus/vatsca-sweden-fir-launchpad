using System.Globalization;

namespace VatscaUpdateChecker.Models;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    public static WindowBounds Fit(WindowBounds saved, WindowBounds workArea, double minWidth, double minHeight)
    {
        if (!workArea.IsValid) throw new ArgumentException("A valid work area is required.");
        if (!saved.IsValid) saved = new(workArea.Left, workArea.Top, minWidth, minHeight);
        double width = Math.Min(workArea.Width, Math.Max(minWidth, saved.Width));
        double height = Math.Min(workArea.Height, Math.Max(minHeight, saved.Height));
        return new(Math.Clamp(saved.Left, workArea.Left, workArea.Left + workArea.Width - width),
            Math.Clamp(saved.Top, workArea.Top, workArea.Top + workArea.Height - height), width, height);
    }
}

/// <summary>Last observed placement of a Launchpad-launched browser window: its visible frame in physical screen pixels.</summary>
/// <remarks>Frame holds the invisible resize-border widths observed with Bounds, so Edge can be given its outer rectangle.</remarks>
public sealed record ExternalWindowPlacement(WindowBounds? Bounds, bool Maximized, string Monitor, WindowInsets Frame = default);

/// <summary>Distances from an outer window rectangle to its visible frame. Negative observations are treated as zero.</summary>
public readonly record struct WindowInsets(double Left, double Top, double Right, double Bottom)
{
    public bool IsValid => Plausible(Left) && Plausible(Top) && Plausible(Right) && Plausible(Bottom);
    private static bool Plausible(double value) => double.IsFinite(value) && value >= 0 && value <= 64;

    public static WindowInsets Between(WindowBounds outer, WindowBounds visible) => new(
        Math.Max(0, visible.Left - outer.Left), Math.Max(0, visible.Top - outer.Top),
        Math.Max(0, outer.Left + outer.Width - visible.Left - visible.Width),
        Math.Max(0, outer.Top + outer.Height - visible.Top - visible.Height));

    public WindowBounds Expand(WindowBounds visible) => IsValid
        ? new(visible.Left - Left, visible.Top - Top, visible.Width + Left + Right, visible.Height + Top + Bottom) : visible;
}

/// <summary>Physical-pixel display; <paramref name="Monitor"/> is the full monitor rectangle and Scale its effective DPI / 96.</summary>
public readonly record struct DisplayArea(string Name, WindowBounds WorkArea, bool Primary, WindowBounds? Monitor = null, double Scale = 1);

/// <summary>Where a browser window should open: visible physical bounds for verification and Chromium DIP outer bounds for its launch arguments.</summary>
public sealed record ExternalWindowTarget(WindowBounds Bounds, bool Maximize, DisplayArea Display, WindowBounds EdgeBounds);

public static class ExternalWindowLayout
{
    public const double MinimumWidth = 320, MinimumHeight = 240;

    /// <summary>
    /// Null when the saved display is no longer connected, so Edge opens with its own default placement:
    /// a normal window needs a display containing its centre, a maximized window its saved display name.
    /// </summary>
    public static ExternalWindowTarget? Resolve(ExternalWindowPlacement saved, IReadOnlyList<DisplayArea> displays)
    {
        if (ResolvePhysical(saved, displays) is not { } target) return null;
        // Chromium's window bounds are the outer rectangle, including invisible resize borders.
        return new(target.Bounds, target.Maximize, target.Display, ToChromiumDip(saved.Frame.Expand(target.Bounds), target.Display, displays));
    }

    /// <summary>Edge launch arguments for a resolved target; none when there is no target.</summary>
    /// <remarks>
    /// Chromium resets the show state to normal whenever --window-position or --window-size parses, so
    /// --start-maximized cannot be combined with a display choice. A maximized target therefore opens normally
    /// on its saved display and the caller maximizes that exact window once it appears.
    /// Values are formatted invariantly (sv-SE uses U+2212 for minus).
    /// </remarks>
    public static IReadOnlyList<string> EdgeArguments(ExternalWindowTarget? target)
    {
        if (target is null) return [];
        var edge = target.EdgeBounds;
        return [string.Create(CultureInfo.InvariantCulture, $"--window-position={(int)edge.Left},{(int)edge.Top}"),
            string.Create(CultureInfo.InvariantCulture, $"--window-size={(int)edge.Width},{(int)edge.Height}")];
    }

    /// <summary>
    /// Chromium's --window-position/--window-size use screen DIPs. With one scale factor this is an exact division.
    /// Mixed scales approximate Chromium's display layout from the primary display and may open a few pixels off.
    /// </summary>
    public static WindowBounds ToChromiumDip(WindowBounds physical, DisplayArea display, IReadOnlyList<DisplayArea> displays)
    {
        static double Valid(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
        double scale = Valid(display.Scale);
        var monitor = display.Monitor ?? display.WorkArea;
        var primaryDisplay = displays.FirstOrDefault(d => d.Primary);
        var primary = primaryDisplay.Monitor ?? primaryDisplay.WorkArea;
        double primaryScale = primaryDisplay.Name is null ? scale : Valid(primaryDisplay.Scale);
        double originX, originY;
        if (displays.All(d => Valid(d.Scale) == scale) || display.Primary || primaryDisplay.Name is null)
        {
            originX = monitor.Left / scale;
            originY = monitor.Top / scale;
        }
        else
        {
            originX = monitor.Left + monitor.Width <= primary.Left
                ? (monitor.Left + monitor.Width) / primaryScale - monitor.Width / scale : monitor.Left / primaryScale;
            originY = monitor.Top + monitor.Height <= primary.Top
                ? (monitor.Top + monitor.Height) / primaryScale - monitor.Height / scale : monitor.Top / primaryScale;
        }
        return new(Math.Round(originX + (physical.Left - monitor.Left) / scale), Math.Round(originY + (physical.Top - monitor.Top) / scale),
            Math.Round(physical.Width / scale), Math.Round(physical.Height / scale));
    }

    private static (WindowBounds Bounds, bool Maximize, DisplayArea Display)? ResolvePhysical(ExternalWindowPlacement saved, IReadOnlyList<DisplayArea> displays)
    {
        var bounds = saved.Bounds is { IsValid: true } valid ? valid : (WindowBounds?)null;
        if (bounds is null && !saved.Maximized) return null;
        var usable = displays.Where(display => display.WorkArea.IsValid).ToArray();
        DisplayArea? Containing(WindowBounds b)
        {
            double x = b.Left + b.Width / 2, y = b.Top + b.Height / 2;
            foreach (var d in usable)
                if (x >= d.WorkArea.Left && x < d.WorkArea.Left + d.WorkArea.Width && y >= d.WorkArea.Top && y < d.WorkArea.Top + d.WorkArea.Height) return d;
            return null;
        }
        DisplayArea? named = null;
        foreach (var d in usable)
            if (d.Name == saved.Monitor) { named = d; break; }
        var display = saved.Maximized ? named : bounds is { } b2 ? Containing(b2) : null;
        if (display is not { } target) return null;
        var area = target.WorkArea;
        // Normal bounds remembered on another display would open the window there; use the saved display's work area.
        if (saved.Maximized && bounds is { } normal && Containing(normal)?.Name != target.Name) bounds = null;
        return (WindowBounds.Fit(bounds ?? area, area, Math.Min(MinimumWidth, area.Width), Math.Min(MinimumHeight, area.Height)), saved.Maximized, target);
    }
}

/// <summary>Decides when an observed browser placement is persisted: once, after the window has closed and saving is allowed.</summary>
/// <remarks>Launchpad's own close also saves current settings, so a placement still pending then is not lost.</remarks>
public sealed class BrowserPlacementTracker
{
    private bool _unsaved;

    /// <summary>Records an observation of the tracked window and returns true when settings should be saved now.</summary>
    /// <param name="observed">Null while the window is hidden or minimized; the previous placement is kept.</param>
    /// <param name="canSave">False while saving would also persist unrelated pending state; the save then waits.</param>
    public bool Observe(AppSettings settings, string appName, bool windowOpen, ExternalWindowPlacement? observed, bool canSave)
    {
        if (!BrowserWindowSettings.Supports(appName)) return false;
        if (windowOpen)
        {
            if (observed is { } placement && BrowserWindowSettings.Set(settings, appName, placement)) _unsaved = true;
            return false;
        }
        if (!_unsaved || !canSave) return false;
        _unsaved = false;
        return true;
    }
}

/// <summary>Only VATIRIS keeps a remembered browser-window placement.</summary>
public static class BrowserWindowSettings
{
    public static bool Supports(string appName) => appName == "VATIRIS";

    public static ExternalWindowPlacement? Get(AppSettings settings, string appName)
    {
        if (!Supports(appName)) return null;
        WindowBounds? bounds = settings.VatirisWindowLeft is { } left && settings.VatirisWindowTop is { } top &&
            settings.VatirisWindowWidth is { } width && settings.VatirisWindowHeight is { } height ? new(left, top, width, height) : null;
        var frame = new WindowInsets(settings.VatirisFrameLeft ?? 0, settings.VatirisFrameTop ?? 0, settings.VatirisFrameRight ?? 0, settings.VatirisFrameBottom ?? 0);
        return bounds is null && !settings.VatirisWindowMaximized ? null
            : new(bounds, settings.VatirisWindowMaximized, settings.VatirisWindowMonitor, frame.IsValid ? frame : default);
    }

    /// <summary>Stores an observation. A maximized observation keeps the earlier normal bounds for un-maximizing.</summary>
    public static bool Set(AppSettings settings, string appName, ExternalWindowPlacement observed)
    {
        if (!Supports(appName)) return false;
        var previous = Get(settings, appName);
        bool observedBounds = observed.Bounds is { IsValid: true };
        var bounds = observedBounds ? observed.Bounds : previous?.Bounds;
        var frame = observedBounds && observed.Frame.IsValid ? observed.Frame : previous?.Frame ?? default;
        var next = new ExternalWindowPlacement(bounds, observed.Maximized, observed.Monitor ?? "", frame);
        if (previous == next) return false;
        settings.VatirisWindowLeft = bounds?.Left; settings.VatirisWindowTop = bounds?.Top;
        settings.VatirisWindowWidth = bounds?.Width; settings.VatirisWindowHeight = bounds?.Height;
        settings.VatirisWindowMaximized = next.Maximized; settings.VatirisWindowMonitor = next.Monitor;
        settings.VatirisFrameLeft = frame.Left; settings.VatirisFrameTop = frame.Top;
        settings.VatirisFrameRight = frame.Right; settings.VatirisFrameBottom = frame.Bottom;
        return true;
    }
}
