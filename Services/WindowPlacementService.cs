using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Only the Launchpad window is positioned. Persisted bounds are physical screen pixels.</summary>
public static class WindowPlacementService
{
    private sealed record Display(string Name, WindowBounds WorkArea, bool Primary);
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

    private static MonitorInfo Info(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        GetMonitorInfo(monitor, ref info);
        return info;
    }
    private static WindowBounds Bounds(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    public static void Restore(Window window, AppSettings settings)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var displays = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect rect, IntPtr data) =>
        {
            var info = Info(monitor);
            if (Bounds(info.Work).IsValid) displays.Add(new(info.Device, Bounds(info.Work), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
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
}
