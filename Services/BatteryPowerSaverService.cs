using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>
/// Automatically applies Windows 11 EcoQoS (Efficiency Mode / E-core scheduling) to ATS-Notch
/// and heavy user desktop apps when running on battery, and immediately releases all limits
/// for full performance when plugged into AC power.
/// </summary>
internal static partial class BatteryPowerSaverService
{
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);

    private static readonly HashSet<string> TargetAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Antigravity IDE",
        "Antigravity",
        "Code",
        "chrome",
        "msedge",
        "brave",
        "firefox",
        "opera",
        "Discord",
        "Spotify",
        "NVIDIA Broadcast",
        "Slack",
        "ms-teams",
        "Teams",
        "WhatsApp",
        "Telegram",
        "node",
        "electron"
    };

    private static readonly ConcurrentDictionary<int, ProcessPriorityClass> ThrottledProcesses = new();
    private static readonly object LifecycleLock = new();
    private static Timer? _batterySweepTimer;
    private static bool _started;
    private static volatile bool _isOnBattery;

    internal static bool IsOnBattery => _isOnBattery;
    internal static event Action<bool>? PowerModeUpdated;

    internal static void Start()
    {
        lock (LifecycleLock)
        {
            if (_started) return;
            _started = true;
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
            _batterySweepTimer = new Timer(_ => SweepIfOnBattery(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }

        RefreshPowerMode(force: true);
    }

    internal static void Stop()
    {
        lock (LifecycleLock)
        {
            if (!_started) return;
            _started = false;
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
            _batterySweepTimer?.Dispose();
            _batterySweepTimer = null;
        }

        ReleaseAllLimits();
    }

    private static void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.StatusChange or PowerModes.Resume)
        {
            RefreshPowerMode(force: false);
        }
    }

    internal static void RefreshPowerMode(bool force)
    {
        bool onBattery = ReadIsOnBattery();
        bool changed = force || onBattery != _isOnBattery;
        _isOnBattery = onBattery;

        if (!changed) return;

        if (onBattery)
        {
            ApplyBatteryLimits();
        }
        else
        {
            ReleaseAllLimits();
        }

        try { PowerModeUpdated?.Invoke(onBattery); } catch { }
    }

    private static bool ReadIsOnBattery()
    {
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                   System.Windows.Forms.PowerLineStatus.Offline;
        }
        catch
        {
            return false;
        }
    }

    private static void SweepIfOnBattery()
    {
        if (!_started) return;
        bool onBattery = ReadIsOnBattery();
        if (onBattery != _isOnBattery)
        {
            RefreshPowerMode(force: true);
            return;
        }
        if (onBattery)
        {
            ApplyBatteryLimits();
        }
    }

    private static void ApplyBatteryLimits()
    {
        // Throttle ATS-Notch itself cleanly into EcoQoS when on battery.
        SetSelfEcoQoS(enable: true);

        int selfPid = Environment.ProcessId;
        var seenPids = new HashSet<int>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    int pid = proc.Id;
                    if (pid == selfPid || !TargetAppNames.Contains(proc.ProcessName)) continue;
                    seenPids.Add(pid);
                    if (ThrottledProcesses.ContainsKey(pid)) continue;

                    if (TrySetProcessEcoQoS(pid, enable: true))
                    {
                        var prevPriority = ProcessPriorityClass.Normal;
                        try
                        {
                            prevPriority = proc.PriorityClass;
                            if (prevPriority == ProcessPriorityClass.Normal)
                            {
                                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                            }
                        }
                        catch
                        {
                            // Priority adjustment is optional; EcoQoS alone already biases to E-cores.
                        }
                        ThrottledProcesses.TryAdd(pid, prevPriority);
                    }
                }
                catch
                {
                    // Skip inaccessible or exited processes safely.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }

        // Prune exited PIDs from tracking dictionary.
        foreach (int trackedPid in ThrottledProcesses.Keys)
        {
            if (!seenPids.Contains(trackedPid))
            {
                ThrottledProcesses.TryRemove(trackedPid, out _);
            }
        }
    }

    private static void ReleaseAllLimits()
    {
        SetSelfEcoQoS(enable: false);

        foreach (var kvp in ThrottledProcesses.ToArray())
        {
            int pid = kvp.Key;
            ProcessPriorityClass originalPriority = kvp.Value;
            ThrottledProcesses.TryRemove(pid, out _);

            _ = TrySetProcessEcoQoS(pid, enable: false);
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited && proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                {
                    proc.PriorityClass = originalPriority;
                }
            }
            catch
            {
                // Process already exited or inaccessible.
            }
        }
    }

    private static void SetSelfEcoQoS(bool enable)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = enable ? ProcessPowerThrottlingExecutionSpeed : 0u
            };
            _ = SetProcessInformation(self.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch
        {
        }
    }

    private static bool TrySetProcessEcoQoS(int pid, bool enable)
    {
        IntPtr handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = enable ? ProcessPowerThrottlingExecutionSpeed : 0u
            };
            return SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }
}

