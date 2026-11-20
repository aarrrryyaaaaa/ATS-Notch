using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Networking.Connectivity;

namespace VNotch.Services;

public sealed record LaptopActivityEvent(string Category, string Title, string Body);

/// <summary>
/// Captures laptop &amp; Windows activities across lock/function/media keys, Wi-Fi/LAN changes,
/// USB drives &amp; peripherals, and power/charger state changes for display in ATS-Notch.
/// </summary>
public sealed class SystemActivityMonitor : IDisposable
{
    private const string LogTag = "SYS-ACTIVITY";
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmDeviceChange = 0x0219;
    private const int PbtApmPowerStatusChange = 0x000A;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 0x00000002;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, long> _keyDebounceTicks = new();
    private LowLevelKeyboardProc? _kbProc;
    private IntPtr _kbHook = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private string _lastNetSignature = string.Empty;
    private bool _netSeeded;
    private byte? _lastAcStatus;
    private bool _notifiedFullBattery;
    private long _lastGenericUsbTicks;
    private bool _lastCapsState;
    private bool _lastNumState;
    private bool _lastScrollState;
    private bool _lockStatesSeeded;
    private long _lastKbEmitLockTicks;

    public event Action<LaptopActivityEvent>? ActivityDetected;
    public event Action? VolumeKeyPressed;

    public SystemActivityMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(Window ownerWindow)
    {
        if (_disposed) return;

        InstallKeyboardHook();
        AttachWindowHooks(ownerWindow);
        SeedPowerState();
        SeedLockStates();

        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunNetworkMonitorLoopAsync(token), token);
    }

    private void InstallKeyboardHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = KeyboardHookCallback;
        // On .NET 8 single-file apps, WH_KEYBOARD_LL succeeds reliably with IntPtr.Zero or GetModuleHandle(null).
        _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc, IntPtr.Zero, 0);
        if (_kbHook == IntPtr.Zero)
        {
            _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc, GetModuleHandle(null), 0);
        }
        if (_kbHook == IntPtr.Zero)
        {
            using var proc = Process.GetCurrentProcess();
            using var mod = proc.MainModule;
            if (mod != null)
            {
                _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc, GetModuleHandle(mod.ModuleName), 0);
            }
        }
    }

    private void SeedLockStates()
    {
        _lastCapsState = (GetKeyState(0x14) & 0x0001) != 0;
        _lastNumState = (GetKeyState(0x90) & 0x0001) != 0;
        _lastScrollState = (GetKeyState(0x91) & 0x0001) != 0;
        _lockStatesSeeded = true;
    }

    private void AttachWindowHooks(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProcHook);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"HwndSource attach failed: {ex.Message}");
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && lParam != IntPtr.Zero &&
            (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int scanCode = Marshal.ReadInt32(lParam, 4);
            bool isWinShiftS = vkCode == 0x53 &&
                ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0) &&
                (GetKeyState(0x10) & 0x8000) != 0;
            bool isVendorFn = vkCode == 0xFF && scanCode != 0;

            if (isWinShiftS || isVendorFn || IsTrackedSpecialKey(vkCode))
            {
                long now = Environment.TickCount64;
                int keyBucket = isWinShiftS ? 0x2C : vkCode;
                bool isToggleLockKey = keyBucket is 0x14 or 0x90 or 0x91;
                long debounceMs = isToggleLockKey ? 120 : 450;

                if (!_keyDebounceTicks.TryGetValue(keyBucket, out long lastTick) || (now - lastTick) >= debounceMs)
                {
                    _keyDebounceTicks[keyBucket] = now;
                    int capturedVk = keyBucket;
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandleSpecialKeyAsync(capturedVk));
                }
            }
        }

        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static bool IsTrackedSpecialKey(int vk) =>
        vk == 0x14 || // Caps Lock
        vk == 0x90 || // Num Lock
        vk == 0x91 || // Scroll Lock
        vk == 0x2C || // Print Screen
        (vk >= 0x70 && vk <= 0x87) || // F1..F24
        (vk >= 0xA6 && vk <= 0xB7) || // Browser, Volume, Media, Launch keys
        vk == 0x5F;   // Sleep

    private async void HandleSpecialKeyAsync(int vk)
    {
        try
        {
            if (_disposed) return;

            if (vk == 0xFF)
            {
                Emit("Tombol Fungsi", "Fn Lock", "Mode tombol fungsi");
                return;
            }

            if (vk == 0xAD) // VK_VOLUME_MUTE
            {
                await Task.Delay(35).ConfigureAwait(false);
                VolumeKeyPressed?.Invoke();
                return;
            }
            if (vk is 0xAE or 0xAF) // Volume Down / Up handled by SystemHudMonitor
            {
                return;
            }

            if (vk is 0x14 or 0x90 or 0x91)
            {
                await Task.Delay(35).ConfigureAwait(false);
                if (_disposed) return;
                _ = _dispatcher.BeginInvoke(() => HandleLockKeyFromHook(vk));
                return;
            }

            if (vk == 0x2C)
            {
                Emit("Layar", "Tangkapan Layar", "Tombol Print Screen ditekan");
                return;
            }

            // Skip F1..F12 if held with Ctrl/Alt/Shift so IDE/browser shortcuts aren't noisy;
            // allow Win+Fn, bare Fn/F1..F12, F13..F24, and dedicated hardware function keys.
            if (vk >= 0x70 && vk <= 0x7B)
            {
                bool modHeld =
                    (GetKeyState(0x11) & 0x8000) != 0 || // Ctrl
                    (GetKeyState(0x12) & 0x8000) != 0;   // Alt
                if (modHeld) return;
                int fNum = vk - 0x70 + 1;
                Emit("Tombol Fungsi", $"Tombol Fungsi F{fNum}", $"F{fNum} ditekan");
                return;
            }

            if (vk >= 0x7C && vk <= 0x87)
            {
                int fNum = vk - 0x70 + 1;
                string desc = fNum switch
                {
                    20 => "Mikrofon / Mic Mute (Fn)",
                    21 or 24 => "Touchpad Toggle (Fn)",
                    23 => "Layar / Proyeksi",
                    _ => $"Tombol Khusus F{fNum}"
                };
                Emit("Tombol Fungsi", desc, $"Fungsi laptop F{fNum}");
                return;
            }

            string? label = vk switch
            {
                0xB0 => "Trek Berikutnya",
                0xB1 => "Trek Sebelumnya",
                0xB2 => "Media Dihentikan",
                0xB3 => "Play / Pause Media",
                0xB4 => "Aplikasi Mail",
                0xB5 => "Pemutar Media",
                0xB6 => "Aplikasi Cepat 1",
                0xB7 => "Kalkulator",
                0xAC => "Browser Home",
                0xAA => "Pencarian Cepat",
                0x5F => "Tombol Mode Tidur",
                _ => null
            };

            if (!string.IsNullOrEmpty(label))
            {
                Emit("Tombol Fungsi", label, "Tombol fungsi laptop ditekan");
            }
        }
        catch
        {
            // Ignore transient key hook errors.
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;

        if (msg == WmDeviceChange)
        {
            int evt = wParam.ToInt32();
            if (evt is DbtDeviceArrival or DbtDeviceRemoveComplete)
            {
                bool arrived = evt == DbtDeviceArrival;
                HandleDeviceChange(arrived, lParam);
            }
        }
        else if (msg == WmPowerBroadcast && wParam.ToInt32() == PbtApmPowerStatusChange)
        {
            CheckPowerStatusChange();
        }

        return IntPtr.Zero;
    }

    private void HandleDeviceChange(bool arrived, IntPtr lParam)
    {
        try
        {
            if (lParam != IntPtr.Zero)
            {
                int devType = Marshal.ReadInt32(lParam, 4);
                if (devType == DbtDevTypVolume)
                {
                    uint unitMask = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                    string drives = DecodeDriveLetters(unitMask);
                    if (!string.IsNullOrEmpty(drives))
                    {
                        string title = arrived ? "USB Drive Terpasang" : "USB Drive Dilepas";
                        string body = arrived ? $"Penyimpanan {drives} siap digunakan" : $"Penyimpanan {drives} dicabut";
                        Emit("USB Storage", title, body);
                        return;
                    }
                }
            }

            long now = Environment.TickCount64;
            if (now - _lastGenericUsbTicks < 1800) return;
            _lastGenericUsbTicks = now;

            Emit("Perangkat USB",
                arrived ? "Perangkat Terhubung" : "Perangkat Dilepas",
                arrived ? "Perangkat USB / eksternal tersambung" : "Perangkat USB / eksternal dilepas");
        }
        catch
        {
            // Ignore malformed device broadcast structs.
        }
    }

    private static string DecodeDriveLetters(uint unitMask)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
            {
                char letter = (char)('A' + i);
                string root = $"{letter}:\\";
                string label = string.Empty;
                try
                {
                    var di = new DriveInfo(root);
                    if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                    {
                        label = $" ({di.VolumeLabel})";
                    }
                }
                catch { }
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{letter}:{label}");
            }
        }
        return sb.ToString();
    }

    private void SeedPowerState()
    {
        if (GetSystemPowerStatus(out var status))
        {
            _lastAcStatus = status.AcLineStatus;
            _notifiedFullBattery = status.BatteryLifePercent == 100 && status.AcLineStatus == 1;
        }
    }

    private void CheckPowerStatusChange()
    {
        if (!GetSystemPowerStatus(out var status)) return;

        int pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
        string pctLabel = pct >= 0 ? $" ({pct}%)" : string.Empty;

        if (_lastAcStatus.HasValue && status.AcLineStatus != _lastAcStatus.Value && status.AcLineStatus is 0 or 1)
        {
            bool plugged = status.AcLineStatus == 1;
            _lastAcStatus = status.AcLineStatus;
            _notifiedFullBattery = false;
            Emit("Daya Laptop",
                plugged ? "Charger Terhubung" : "Menggunakan Baterai",
                plugged ? $"Mengisi daya laptop{pctLabel}" : $"Adaptor dilepas{pctLabel}");
            return;
        }

        if (status.AcLineStatus == 1 && pct == 100 && !_notifiedFullBattery)
        {
            _notifiedFullBattery = true;
            Emit("Baterai", "Baterai Penuh 100%", "Pengisian daya selesai");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        CheckNetworkState(notifyIfChanged: true);
    }

    private async Task RunNetworkMonitorLoopAsync(CancellationToken ct)
    {
        CheckNetworkState(notifyIfChanged: false);
        try { await Task.Delay(3500, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        RuntimeLog.Log(LogTag, $"Laptop activity monitor active (kbHook={_kbHook != IntPtr.Zero}, hwnd={_hwndSource != null}).");

        int tickCounter = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(85));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_disposed && _lockStatesSeeded)
                {
                    _ = _dispatcher.BeginInvoke(CheckLockStatesTransition);
                }

                tickCounter++;
                if (tickCounter >= 18) // ~1.5s network SSID check
                {
                    tickCounter = 0;
                    CheckNetworkState(notifyIfChanged: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private static bool ReadAccurateToggleState(int vk)
    {
        IntPtr fg = GetForegroundWindow();
        uint curTid = GetCurrentThreadId();
        uint fgTid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        bool attached = fgTid != 0 && fgTid != curTid && AttachThreadInput(curTid, fgTid, true);
        try
        {
            return (GetKeyState(vk) & 0x0001) != 0;
        }
        finally
        {
            if (attached) AttachThreadInput(curTid, fgTid, false);
        }
    }

    private void HandleLockKeyFromHook(int vk)
    {
        if (_disposed) return;
        if (!_lockStatesSeeded) SeedLockStates();

        long now = Environment.TickCount64;
        _lastKbEmitLockTicks = now;

        bool osBit = ReadAccurateToggleState(vk);
        if (vk == 0x14)
        {
            _lastCapsState = osBit;
            Emit("Keyboard", $"Caps Lock {(osBit ? "ON" : "OFF")}",
                osBit ? "HURUF BESAR AKTIF" : "Huruf kecil kembali normal");
        }
        else if (vk == 0x90)
        {
            _lastNumState = osBit;
            Emit("Keyboard", $"Num Lock {(osBit ? "ON" : "OFF")}",
                osBit ? "NumPad angka aktif" : "Mode navigasi NumPad");
        }
        else if (vk == 0x91)
        {
            _lastScrollState = osBit;
            Emit("Keyboard", $"Scroll Lock {(osBit ? "ON" : "OFF")}",
                osBit ? "Mode Scroll Lock aktif" : "Mode Scroll Lock nonaktif");
        }
    }

    private void CheckLockStatesTransition()
    {
        if (_disposed || !_lockStatesSeeded) return;

        long now = Environment.TickCount64;
        if ((now - _lastKbEmitLockTicks) <= 180) return;

        // Only read current thread's GetKeyState in the periodic loop (never call AttachThreadInput
        // every 80ms, as attaching to the foreground thread disrupts user typing focus).
        bool caps = (GetKeyState(0x14) & 0x0001) != 0;
        bool num = (GetKeyState(0x90) & 0x0001) != 0;
        bool scroll = (GetKeyState(0x91) & 0x0001) != 0;

        if (caps != _lastCapsState)
        {
            _lastCapsState = caps;
            _lastKbEmitLockTicks = now;
            Emit("Keyboard", $"Caps Lock {(caps ? "ON" : "OFF")}",
                caps ? "HURUF BESAR AKTIF" : "Huruf kecil kembali normal");
        }

        if (num != _lastNumState)
        {
            _lastNumState = num;
            Emit("Keyboard", $"Num Lock {(num ? "ON" : "OFF")}",
                num ? "NumPad angka aktif" : "Mode navigasi NumPad");
        }

        if (scroll != _lastScrollState)
        {
            _lastScrollState = scroll;
            Emit("Keyboard", $"Scroll Lock {(scroll ? "ON" : "OFF")}",
                scroll ? "Mode Scroll Lock aktif" : "Mode Scroll Lock nonaktif");
        }
    }

    private void CheckNetworkState(bool notifyIfChanged)
    {
        try
        {
            string sig = "OFFLINE";
            string kind = "Offline";
            string name = string.Empty;

            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile != null && profile.GetNetworkConnectivityLevel() >= NetworkConnectivityLevel.LocalAccess)
            {
                if (profile.IsWlanConnectionProfile && profile.WlanConnectionProfileDetails != null)
                {
                    name = profile.WlanConnectionProfileDetails.GetConnectedSsid() ?? string.Empty;
                    kind = "Wi-Fi";
                    sig = $"WIFI:{name}";
                }
                else
                {
                    kind = "Ethernet";
                    name = profile.ProfileName ?? "Jaringan Kabel";
                    sig = $"ETH:{name}";
                }
            }

            if (!_netSeeded)
            {
                _lastNetSignature = sig;
                _netSeeded = true;
                return;
            }

            if (string.Equals(sig, _lastNetSignature, StringComparison.Ordinal)) return;

            string previousSig = _lastNetSignature;
            _lastNetSignature = sig;
            if (!notifyIfChanged) return;

            if (kind == "Wi-Fi")
            {
                string ssidText = string.IsNullOrWhiteSpace(name) ? "Jaringan nirkabel" : name;
                Emit("Wi-Fi", "Terhubung", $"Tersambung ke {ssidText}");
            }
            else if (kind == "Ethernet")
            {
                Emit("Jaringan", "Ethernet Terhubung", $"Tersambung via {name}");
            }
            else if (previousSig.StartsWith("WIFI:", StringComparison.Ordinal))
            {
                Emit("Wi-Fi", "Terputus", "Koneksi terputus");
            }
            else
            {
                Emit("Jaringan", "Terputus", "Koneksi internet terputus");
            }
        }
        catch
        {
            // Ignore transient WinRT network query glitches.
        }
    }

    private void Emit(string category, string title, string body)
    {
        RuntimeLog.Log(LogTag, $"[{category}] {title} — {body}");
        ActivityDetected?.Invoke(new LaptopActivityEvent(category, title, body));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;

        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        try { _hwndSource?.RemoveHook(WndProcHook); } catch { }
        _hwndSource = null;

        if (_kbHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
        }
        _kbProc = null;
    }
}

