using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

/// <summary>
/// Keeps ATS-Notch's physical memory footprint low during idle and after closing heavy overlays
/// by compacting the managed heap when appropriate and trimming cold working-set pages.
/// </summary>
internal static partial class ProcessMemoryTrimmer
{
    // Total Working Set ~88 MB corresponds to ~42-45 MB Private Working Set in Task Manager.
    // Staying below this threshold avoids unnecessary periodic trims during idle.
    private const long SoftWorkingSetThresholdBytes = 88L * 1024 * 1024;
    private static readonly object Sync = new();
    private static CancellationTokenSource? _pendingCts;
    private static long _lastTrimTickMs;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize,
        uint flags);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    internal static void ScheduleTrim(TimeSpan delay, bool compactGc = false)
    {
        CancellationToken token;
        lock (Sync)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            token = _pendingCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                TrimNow(compactGc);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    internal static void TrimIfAboveBudget(bool compactGc = false)
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastTrimTickMs) < 30_000) return;

        if (Environment.WorkingSet > SoftWorkingSetThresholdBytes)
        {
            _ = Task.Run(() => TrimNow(compactGc));
        }
    }

    internal static void TrimNow(bool compactGc)
    {
        Volatile.Write(ref _lastTrimTickMs, Environment.TickCount64);
        try
        {
            if (compactGc)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            }

            IntPtr handle = Process.GetCurrentProcess().Handle;
            _ = SetProcessWorkingSetSizeEx(handle, -1, -1, 0);
            _ = EmptyWorkingSet(handle);
        }
        catch
        {
            // Memory trimming is best-effort and must never throw.
        }
    }
}

