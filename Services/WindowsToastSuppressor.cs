using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>
/// Hides Windows' native bottom-right toast banner popups while keeping Windows
/// notifications active in WPN so notifications appear exclusively inside ATS-Notch.
/// </summary>
public sealed class WindowsToastSuppressor : IDisposable
{
    private const string LogTag = "TOAST-SUPPRESS";
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int SwHide = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpHideWindow = 0x0080;
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const int DwmwaCloak = 13;
    private const string PushNotificationsRegPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly Func<bool> _isEnabled;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<uint, string> _pidNameCache = new();
    private WinEventDelegate? _winEventDelegate;
    private IntPtr _showHook;
    private IntPtr _locHook;
    private DispatcherTimer? _fallbackTimer;
    private bool _loggedFirstSuppression;
    private bool _disposed;
    public WindowsToastSuppressor(Dispatcher dispatcher, Func<bool> isEnabled)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    public void Start()
    {
        if (_disposed || _showHook != IntPtr.Zero) return;

        // Ensure Windows ToastEnabled remains enabled (1) so macOS 27 Finder Bar
        // and Windows Action Center retain full notification history, while we cloak
        // floating banners from both Windows bottom-right toast and WhFinderTopBarIsland.
        EnsureToastHistoryEnabled();

        void InstallOnUi()
        {
            if (_disposed || _showHook != IntPtr.Zero) return;
            _winEventDelegate = OnWinEvent;
            _showHook = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero, _winEventDelegate, 0, 0, WineventOutOfContext);
            _locHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _winEventDelegate, 0, 0, WineventOutOfContext);

            _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            _fallbackTimer.Tick += (_, _) => ScanAndSuppress();
            _fallbackTimer.Start();
            RuntimeLog.Log(LogTag, $"Windows toast banner suppressor started (showHook={_showHook != IntPtr.Zero}, locHook={_locHook != IntPtr.Zero}).");
        }

        if (_dispatcher.CheckAccess()) InstallOnUi();
        else _dispatcher.BeginInvoke(InstallOnUi);
    }

    private static void EnsureToastHistoryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PushNotificationsRegPath, writable: true);
            if (key == null) return;
            object? raw = key.GetValue("ToastEnabled");
            int current = raw is int iv ? iv : 1;
            if (current != 1)
            {
                key.SetValue("ToastEnabled", 1, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // Ignore HKCU write error.
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || idObject != 0 || !_isEnabled()) return;
        TrySuppressWindow(hwnd);
    }

    private void ScanAndSuppress()
    {
        if (_disposed || !_isEnabled()) return;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                TrySuppressWindow(hwnd);
            }
            return true;
        }, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void TrySuppressWindow(IntPtr hwnd)
    {
        try
        {
            if (!IsWindowVisible(hwnd)) return;

            var clsBuf = new StringBuilder(64);
            if (GetClassName(hwnd, clsBuf, clsBuf.Capacity) <= 0) return;
            string clsName = clsBuf.ToString();

            // Suppress macOS 27 Finder Bar descending notice pills ("WhFinderTopBarIsland")
            // without touching the main top menu bar so notifications only appear in ATS-Notch.
            if (string.Equals(clsName, "WhFinderTopBarIsland", StringComparison.Ordinal))
            {
                int cloakPill = 1;
                _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloakPill, sizeof(int));
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
                ShowWindow(hwnd, SwHide);
                if (!_loggedFirstSuppression)
                {
                    _loggedFirstSuppression = true;
                    RuntimeLog.Log(LogTag, "Suppressed Finder Bar Notice Pill window (WhFinderTopBarIsland).");
                }
                return;
            }

            if (!string.Equals(clsName, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
                return;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            string procName = ResolveProcessName(pid);
            if (!string.Equals(procName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                return;

            var titleBuf = new StringBuilder(128);
            GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
            string title = titleBuf.ToString();

            bool isExplicitToastTitle =
                title.Contains("New notification", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Pemberitahuan baru", StringComparison.OrdinalIgnoreCase);

            // Windows 11 native toast banners are unfocused CoreWindows of ShellExperienceHost
            // docked on the bottom-right corner of the work area (Action Center & Quick Settings gain focus).
            bool isBottomRightToastRect = false;
            if (!isExplicitToastTitle && GetForegroundWindow() != hwnd && GetWindowRect(hwnd, out var rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                double dpi = GetDpiScaleFallback();
                double workRight = SystemParameters.WorkArea.Right * dpi;
                double workBottom = SystemParameters.WorkArea.Bottom * dpi;
                isBottomRightToastRect =
                    width >= 220 && width <= (int)(520 * dpi) &&
                    height >= 60 && height <= (int)(480 * dpi) &&
                    rect.Right >= workRight - (120 * dpi) &&
                    rect.Bottom >= workBottom - (120 * dpi);
            }

            if (!isExplicitToastTitle && !isBottomRightToastRect) return;

            int exStyle = GetWindowLong32(hwnd, GwlExStyle);
            if ((exStyle & WsExLayered) == 0)
            {
                SetWindowLong32(hwnd, GwlExStyle, exStyle | WsExLayered);
            }
            int cloak = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int));
            SetLayeredWindowAttributes(hwnd, 0, 0, LwaAlpha);
            SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpHideWindow);
            ShowWindow(hwnd, SwHide);

            if (!_loggedFirstSuppression)
            {
                _loggedFirstSuppression = true;
                RuntimeLog.Log(LogTag, $"Suppressed Windows native toast window (title='{title}').");
            }
        }
        catch
        {
            // Ignore race if window destroyed mid-check.
        }
    }

    private static double GetDpiScaleFallback()
    {
        try
        {
            var mainWin = Application.Current?.MainWindow;
            if (mainWin == null) return 1.0;
            var src = PresentationSource.FromVisual(mainWin);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private string ResolveProcessName(uint pid)
    {
        if (_pidNameCache.TryGetValue(pid, out var cached)) return cached;
        if (_pidNameCache.Count > 256) _pidNameCache.Clear();
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            _pidNameCache[pid] = name;
            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        void StopOnUi()
        {
            _fallbackTimer?.Stop();
            _fallbackTimer = null;
            if (_showHook != IntPtr.Zero)
            {
                UnhookWinEvent(_showHook);
                _showHook = IntPtr.Zero;
            }
            if (_locHook != IntPtr.Zero)
            {
                UnhookWinEvent(_locHook);
                _locHook = IntPtr.Zero;
            }
            _winEventDelegate = null;
        }

        if (_dispatcher.CheckAccess()) StopOnUi();
        else _dispatcher.BeginInvoke(StopOnUi);
    }
}
