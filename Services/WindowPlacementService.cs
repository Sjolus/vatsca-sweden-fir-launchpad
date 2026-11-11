using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Positions only the Launchpad window. Launchpad-launched browser windows are observed here and placed by
/// Edge's launch arguments. Persisted bounds are physical screen pixels.</summary>
public static class WindowPlacementService
{
    private sealed record Display(string Name, WindowBounds WorkArea, bool Primary, WindowBounds Monitor, double Scale);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private static MonitorInfo Info(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        GetMonitorInfo(monitor, ref info);
        return info;
    }
    private static WindowBounds Bounds(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static List<Display> Displays()
    {
        var displays = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect rect, IntPtr data) =>
        {
            var info = Info(monitor);
            double scale = 1;
            try { if (GetDpiForMonitor(monitor, 0 /* effective */, out var dpi, out uint _dpiY) == 0 && dpi > 0) scale = dpi / 96d; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            if (Bounds(info.Work).IsValid) displays.Add(new(info.Device, Bounds(info.Work), (info.Flags & 1) != 0, Bounds(info.Monitor), scale));
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    public static void Restore(Window window, AppSettings settings)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var displays = Displays();
        var display = displays.FirstOrDefault(x => x.Name == settings.WindowMonitor) ?? displays.FirstOrDefault(x => x.Primary) ?? displays.FirstOrDefault();
        if (display == null) return;
        WindowBounds? saved = settings.WindowLeft is { } left && settings.WindowTop is { } top &&
            settings.WindowWidth is { } width && settings.WindowHeight is { } height ? new(left, top, width, height) : null;
        if (saved.HasValue || settings.WindowMaximized)
        {
            double minimumWidth = window.MinWidth, minimumHeight = window.MinHeight;
            var requested = saved ?? new WindowBounds(display.WorkArea.Left, display.WorkArea.Top, window.Width, window.Height);
            // Move first so GetDpiForWindow describes the destination. Temporarily release
            // WPF's old monitor minima; a smaller work area must still keep controls reachable.
            window.MinWidth = window.MinHeight = 0;
            try
            {
                Position(hwnd, WindowBounds.Fit(requested, display.WorkArea, 1, 1));
                var dpi = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
                minimumWidth = Math.Min(minimumWidth, display.WorkArea.Width / dpi);
                minimumHeight = Math.Min(minimumHeight, display.WorkArea.Height / dpi);
                window.MinWidth = minimumWidth;
                window.MinHeight = minimumHeight;
                Position(hwnd, WindowBounds.Fit(requested, display.WorkArea, minimumWidth * dpi, minimumHeight * dpi));
            }
            finally
            {
                window.MinWidth = minimumWidth;
                window.MinHeight = minimumHeight;
            }
        }
        if (settings.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    private static void Position(IntPtr hwnd, WindowBounds bounds) =>
        SetWindowPos(hwnd, IntPtr.Zero, (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height,
            0x0014 /* NOZORDER | NOACTIVATE */);

    public static void Capture(Window window, AppSettings settings)
    {
        if (window.WindowState == WindowState.Minimized) return;
        settings.WindowMaximized = window.WindowState == WindowState.Maximized;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        settings.WindowMonitor = Info(MonitorFromWindow(hwnd, 2 /* nearest */)).Device;
        if (settings.WindowMaximized || !GetWindowRect(hwnd, out var rect)) return;
        var bounds = Bounds(rect);
        if (!bounds.IsValid) return;
        settings.WindowLeft = bounds.Left; settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
    }

    // Launchpad itself is system DPI aware. Read other processes' windows and the display layout in
    // physical pixels so mixed-DPI monitor layouts are described exactly; unsupported Windows builds
    // keep the virtualized coordinates, which are still consistent between capture and launch.
    private static T PhysicalPixels<T>(Func<T> action)
    {
        IntPtr previous;
        try { previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return action(); }
        try { return action(); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    // Windows 10/11 frames carry invisible resize borders (about 7 px) outside the visible edge. A snapped
    // window's outer rectangle therefore extends past the work area and would be clipped by fitting.
    // External placements save the visible DWM frame and the border widths separately.
    private static WindowBounds? VisibleBounds(IntPtr hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<Rect>()) == 0 && Bounds(frame).IsValid)
                return Bounds(frame);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return GetWindowRect(hwnd, out var rect) && Bounds(rect).IsValid ? Bounds(rect) : null;
    }

    /// <summary>Null while the window is hidden or minimized, so the previous placement is kept.</summary>
    public static ExternalWindowPlacement? CaptureExternal(IntPtr hwnd) => hwnd == IntPtr.Zero ? null : PhysicalPixels(() =>
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        bool maximized = IsZoomed(hwnd);
        var monitor = Info(MonitorFromWindow(hwnd, 2 /* nearest */)).Device ?? "";
        if (maximized) return new ExternalWindowPlacement(null, true, monitor);
        if (VisibleBounds(hwnd) is not { } visible) return null;
        var frame = GetWindowRect(hwnd, out var rect) && Bounds(rect).IsValid ? WindowInsets.Between(Bounds(rect), visible) : default;
        return new ExternalWindowPlacement(visible, false, monitor, frame);
    });

    /// <summary>Maximizes a shown external window once. False while it is not yet visible, so the caller can wait.</summary>
    public static bool MaximizeExternal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        if (!IsZoomed(hwnd)) ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
        return true;
    }

    /// <summary>Resolves the saved placement against the connected displays, or null to leave Edge's default.</summary>
    public static ExternalWindowTarget? PlanExternal(ExternalWindowPlacement saved) => PhysicalPixels(() =>
        ExternalWindowLayout.Resolve(saved, Displays().Select(display =>
            new DisplayArea(display.Name, display.WorkArea, display.Primary, display.Monitor, display.Scale)).ToArray()));
}
