using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GearHub.App;

/// <summary>Work area (excluding the taskbar) of the monitor the window is on.</summary>
public static class ScreenHelper
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rect GetWorkArea(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

                if (GetMonitorInfo(monitor, ref info))
                {
                    return new Rect(
                        info.Work.Left,
                        info.Work.Top,
                        info.Work.Right - info.Work.Left,
                        info.Work.Bottom - info.Work.Top);
                }
            }
        }
        catch
        {
            // Fall through to the fallback below.
        }

        var fallback = SystemParameters.WorkArea;
        return new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    /// <summary>Cursor position in screen pixels — needed to place the flyout near the tray icon.</summary>
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>Work area (excluding the taskbar) of the monitor that contains the screen point.</summary>
    public static Rect GetWorkAreaAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                return new Rect(
                    info.Work.Left,
                    info.Work.Top,
                    info.Work.Right - info.Work.Left,
                    info.Work.Bottom - info.Work.Top);
            }
        }
        catch
        {
            // Fall through to the fallback below.
        }

        var fallback2 = SystemParameters.WorkArea;
        return new Rect(fallback2.Left, fallback2.Top, fallback2.Width, fallback2.Height);
    }

    private static readonly string[] OverflowFlyoutClasses =
    [
        "TopLevelWindowForOverflowXamlIsland", // background window (Win11)
        "NotifyIconOverflowWindow",            // background window (Win10)
    ];

    /// <summary>Bounds of the open hidden-icons tray overflow if it is visible on screen.</summary>
    public static bool TryGetOverflowFlyoutBounds(out Rect bounds)
    {
        foreach (var className in OverflowFlyoutClasses)
        {
            var handle = FindWindow(className, null);
            if (handle == IntPtr.Zero || !IsWindowVisible(handle))
            {
                continue;
            }

            if (GetWindowRect(handle, out var rect))
            {
                bounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                return true;
            }
        }

        bounds = Rect.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
