using System.Runtime.InteropServices;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

public static class OverlayNative
{
    public const long RequiredStyles = 0x20 | 0x08000000 | 0x80; // TRANSPARENT, NOACTIVATE, TOOLWINDOW
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    public static DesktopPixelRect Bounds(nint hwnd) => GetWindowRect(hwnd, out var r)
        ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;
    public static void Configure(nint hwnd) => SetWindowLongPtr(hwnd, -20, (nint)((long)GetWindowLongPtr(hwnd, -20) | RequiredStyles));
    public static bool Position(nint hwnd, DesktopPixelRect b) => SetWindowPos(hwnd, new nint(-1), b.X, b.Y, b.Width, b.Height, 0x10 | 0x40);
    public static bool IsCurrentForeground(WeChatWindowSnapshot w) => w.IsVisible && !w.IsMinimized &&
        IsWindowVisible(w.Handle) && !IsIconic(w.Handle) && GetForegroundWindow() == w.Handle &&
        Bounds(w.RenderHandle ?? w.Handle) == (w.RenderBounds ?? w.TopLevelBounds) && GetDpiForWindow(w.Handle) == w.Dpi.X;
}
