using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IPTVExplorer.Desktop;

internal static class TrueFullscreenBehavior
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void Attach(Window window)
    {
        window.StateChanged += (_, _) => ApplyIfFullscreen(window);
        window.LocationChanged += (_, _) => ApplyIfFullscreen(window);
    }

    private static void ApplyIfFullscreen(Window window)
    {
        if (window.WindowStyle != WindowStyle.None ||
            window.ResizeMode != ResizeMode.NoResize ||
            window.WindowState != WindowState.Maximized) return;

        window.Dispatcher.BeginInvoke(() =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            var bounds = info.Monitor;
            SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                SwpNoActivate | SwpFrameChanged);
        }, DispatcherPriority.ApplicationIdle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
