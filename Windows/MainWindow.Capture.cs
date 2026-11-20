using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    internal Func<bool>? ScreenCaptureActivityProbe { get; set; }
    private readonly ScreenCaptureActivity _screenCaptureActivity = new();
    private bool IsScreenCaptureActive => ScreenCaptureActivityProbe?.Invoke() ?? _screenCaptureActivity.IsActive();

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Apply the user's Invisible mode before the first visible frame.
        WindowAnimationPolicy.Attach(this);
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | WsExNoActivate);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateWndProc);
        }
    }

    private IntPtr NoActivateWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate && !IsPointerOverTextInput())
        {
            handled = true;
            return (IntPtr)MaNoActivate;
        }
        return IntPtr.Zero;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject? node) =>
        node switch
        {
            null => null,
            Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
            _ => LogicalTreeHelper.GetParent(node)
        };

    private static bool IsPointerOverTextInput()
    {
        var current = Mouse.DirectlyOver as DependencyObject;
        while (current != null)
        {
            if (current is TextBoxBase) return true;
            current = GetVisualOrLogicalParent(current);
        }
        return false;
    }
}
