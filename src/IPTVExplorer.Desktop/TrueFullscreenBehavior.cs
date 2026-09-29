using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPTVExplorer.Desktop;

internal sealed class TrueFullscreenBehavior(Window window)
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private FullscreenWindowState? _savedState;

    public bool IsFullscreen => _savedState is not null;

    public bool Enter()
    {
        if (IsFullscreen) return false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == 0) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var bounds = info.Monitor;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;

        var saved = FullscreenWindowState.Capture(window);
        _savedState = saved;

        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Topmost = true;

        // GetMonitorInfo and SetWindowPos both use device pixels. Keeping this transition in
        // Win32 coordinates avoids mixing physical monitor bounds with WPF DIPs at non-100% DPI.
        if (SetWindowPos(
                handle,
                HwndTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow)) return true;

        Restore(saved, handle);
        _savedState = null;
        return false;
    }

    public bool Exit()
    {
        if (_savedState is not { } saved) return false;

        _savedState = null;
        var handle = new WindowInteropHelper(window).Handle;
        Restore(saved, handle);
        return true;
    }

    private void Restore(FullscreenWindowState saved, nint handle)
    {
        window.WindowState = WindowState.Normal;
        window.WindowStyle = saved.Style;
        window.ResizeMode = saved.ResizeMode;
        window.Topmost = saved.Topmost;

        var bounds = saved.State == WindowState.Normal ? saved.Bounds : saved.RestoreBounds;
        if (IsUsable(bounds))
        {
            window.Left = bounds.Left;
            window.Top = bounds.Top;
            window.Width = bounds.Width;
            window.Height = bounds.Height;
        }

        window.WindowState = saved.State;
        if (handle != 0)
        {
            _ = SetWindowPos(
                handle,
                0,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    private static bool IsUsable(Rect bounds) =>
        !bounds.IsEmpty &&
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width > 0 &&
        bounds.Height > 0;

    private sealed record FullscreenWindowState(
        Rect Bounds,
        Rect RestoreBounds,
        WindowState State,
        WindowStyle Style,
        ResizeMode ResizeMode,
        bool Topmost)
    {
        public static FullscreenWindowState Capture(Window source)
        {
            var width = source.ActualWidth > 0 ? source.ActualWidth : source.Width;
            var height = source.ActualHeight > 0 ? source.ActualHeight : source.Height;
            return new(
                new Rect(source.Left, source.Top, width, height),
                source.RestoreBounds,
                source.WindowState,
                source.WindowStyle,
                source.ResizeMode,
                source.Topmost);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public PixelRect Monitor;
        public PixelRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
