using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IPTVExplorer.Desktop;

public sealed class NativeVideoHost : HwndHost
{
    private const int WmMouseMove = 0x0200;
    private const int WmSetCursor = 0x0020;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;
    private nint _handle;
    private bool _cursorHidden;
    private ScreenPoint? _hiddenAt;

    public NativeVideoHost() => MessageHook += OnNativeMessage;

    public event EventHandler? HandleReady;
    public event EventHandler? PointerMoved;
    public nint NativeHandle => _handle;

    public void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;
        _cursorHidden = hidden;
        if (hidden)
        {
            _hiddenAt = GetCursorPos(out var point) ? point : null;
            _ = SetCursor(0);
        }
        else
        {
            _hiddenAt = null;
            _ = SetCursor(LoadCursorW(0, 32512));
        }
    }

    private nint OnNativeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseMove &&
            (!_cursorHidden || _hiddenAt is not { } hiddenAt || !GetCursorPos(out var current) ||
             current.X != hiddenAt.X || current.Y != hiddenAt.Y))
            PointerMoved?.Invoke(this, EventArgs.Empty);
        if (message == WmSetCursor && _cursorHidden)
        {
            _ = SetCursor(0);
            handled = true;
        }
        return 0;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _handle = CreateWindowExW(0, "STATIC", string.Empty, WsChild | WsVisible | WsClipSiblings | WsClipChildren | SsBlackRect,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_handle == 0) throw new InvalidOperationException("Impossible de créer la surface vidéo native.");
        HandleReady?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != 0) _ = DestroyWindow(hwnd.Handle);
        _handle = 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, int cursorName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }
}
