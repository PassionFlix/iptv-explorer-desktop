using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IPTVExplorer.Desktop;

public sealed class NativeVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private nint _handle;

    public event EventHandler? HandleReady;
    public nint NativeHandle => _handle;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _handle = CreateWindowExW(0, "STATIC", string.Empty, WsChild | WsVisible | WsClipSiblings | WsClipChildren,
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
}
