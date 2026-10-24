using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Orbit.App.Platform;

/// <summary>Foreground owner and physical cursor position captured before Orbit is activated.</summary>
public readonly record struct HotkeyInvocation(IntPtr ForegroundWindow, double CursorX, double CursorY);

public readonly record struct ScreenWorkArea(int Left, int Top, int Right, int Bottom, uint DpiX, uint DpiY)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public double ScaleX => DpiX / 96d;
    public double ScaleY => DpiY / 96d;
}

/// <summary>Physical-pixel monitor information. No application window is moved by these queries.</summary>
public static class NativeScreen
{
    public static HotkeyInvocation GetCursorInvocation()
    {
        var foreground = GetForegroundWindow();
        if (!GetPhysicalCursorPos(out var point) && !GetCursorPos(out point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new HotkeyInvocation(foreground, point.X, point.Y);
    }

    public static ScreenWorkArea GetWorkAreaAt(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), global::Orbit.Core.Localization.L.Get("Text_AE2FCD8FFED4"));
        }

        var point = new Point
        {
            X = (int)Math.Clamp(Math.Round(x), int.MinValue, int.MaxValue),
            Y = (int)Math.Clamp(Math.Round(y), int.MinValue, int.MaxValue)
        };
        var monitor = MonitorFromPoint(point, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Orbit's manifest is PerMonitorV2. MDT_EFFECTIVE_DPI therefore reflects this monitor.
        uint dpiX = 96;
        uint dpiY = 96;
        if (GetDpiForMonitor(monitor, 0, out var actualX, out var actualY) >= 0 && actualX > 0 && actualY > 0)
        {
            dpiX = actualX;
            dpiY = actualY;
        }

        return new ScreenWorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, dpiX, dpiY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
