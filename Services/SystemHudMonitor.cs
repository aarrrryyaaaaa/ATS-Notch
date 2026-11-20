using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

/// <summary>
/// Monitors global Windows master volume and laptop internal display brightness
/// so Fn keys, Quick Settings sliders, and taskbar sliders trigger the notch HUD.
/// </summary>
public sealed class SystemHudMonitor : IDisposable
{
    private const string LogTag = "SYS-HUD";

    private readonly VolumeService _volumeService;
    private readonly object _wmiLock = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private float _lastVolume = -1f;
    private bool _lastMuted;
    private bool _volumeSeeded;

    private int _lastBrightness = -1;
    private bool _brightnessSeeded;
    private bool _brightnessUnavailable;

    private long _suppressVolumePollUntilTicks;
    private long _suppressBrightnessPollUntilTicks;

    public event Action<float, bool>? MasterVolumeChanged;
    public event Action<int>? BrightnessChanged;

    public SystemHudMonitor()
    {
        _volumeService = new VolumeService();
    }

    public int CurrentBrightnessPercent => Math.Clamp(_lastBrightness >= 0 ? _lastBrightness : 50, 0, 100);

    public void Start()
    {
        if (_disposed || _cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunMonitorLoopAsync(token), token);
    }

    public void SuppressVolumeNotificationFor(int ms = 500)
    {
        Interlocked.Exchange(ref _suppressVolumePollUntilTicks, Environment.TickCount64 + ms);
    }

    public void SuppressBrightnessNotificationFor(int ms = 500)
    {
        Interlocked.Exchange(ref _suppressBrightnessPollUntilTicks, Environment.TickCount64 + ms);
    }

    public bool TryGetMasterVolume(out float volume, out bool isMuted)
    {
        volume = 0.5f;
        isMuted = false;
        try
        {
            if (!_volumeService.IsAvailable) return false;
            volume = Math.Clamp(_volumeService.GetVolume(), 0f, 1f);
            isMuted = _volumeService.GetMute();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetMasterVolume(float volume)
    {
        try
        {
            if (!_volumeService.IsAvailable) return false;
            float clamped = Math.Clamp(volume, 0f, 1f);
            SuppressVolumeNotificationFor(600);
            if (!_volumeService.SetVolume(clamped)) return false;
            _lastVolume = clamped;
            if (clamped > 0.005f && _volumeService.GetMute())
            {
                _volumeService.SetMute(false);
                _lastMuted = false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetBrightness(out int percent)
    {
        percent = -1;
        if (_brightnessUnavailable) return false;

        lock (_wmiLock)
        {
            try
            {
                var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType == null)
                {
                    _brightnessUnavailable = true;
                    return false;
                }

                object? locator = Activator.CreateInstance(locatorType);
                if (locator == null) return false;

                object? services = locatorType.InvokeMember(
                    "ConnectServer",
                    BindingFlags.InvokeMethod,
                    null,
                    locator,
                    new object[] { ".", @"root\WMI" });
                if (services == null) return false;

                object? instances = services.GetType().InvokeMember(
                    "InstancesOf",
                    BindingFlags.InvokeMethod,
                    null,
                    services,
                    new object[] { "WmiMonitorBrightness" });
                if (instances is not IEnumerable enumerable) return false;

                foreach (object? item in enumerable)
                {
                    if (item == null) continue;
                    object? rawVal = item.GetType().InvokeMember(
                        "CurrentBrightness",
                        BindingFlags.GetProperty,
                        null,
                        item,
                        null);
                    if (rawVal != null)
                    {
                        percent = Math.Clamp(Convert.ToInt32(rawVal), 0, 100);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TrySetBrightness(int percent)
    {
        if (_brightnessUnavailable) return false;
        int clamped = Math.Clamp(percent, 0, 100);

        lock (_wmiLock)
        {
            try
            {
                var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType == null) return false;

                object? locator = Activator.CreateInstance(locatorType);
                if (locator == null) return false;

                object? services = locatorType.InvokeMember(
                    "ConnectServer",
                    BindingFlags.InvokeMethod,
                    null,
                    locator,
                    new object[] { ".", @"root\WMI" });
                if (services == null) return false;

                object? instances = services.GetType().InvokeMember(
                    "InstancesOf",
                    BindingFlags.InvokeMethod,
                    null,
                    services,
                    new object[] { "WmiMonitorBrightnessMethods" });
                if (instances is not IEnumerable enumerable) return false;

                foreach (object? item in enumerable)
                {
                    if (item == null) continue;
                    item.GetType().InvokeMember(
                        "WmiSetBrightness",
                        BindingFlags.InvokeMethod,
                        null,
                        item,
                        new object[] { (uint)1, (byte)clamped });
                    SuppressBrightnessNotificationFor(600);
                    _lastBrightness = clamped;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        RuntimeLog.Log(LogTag, "Starting global Volume & Brightness HUD monitor.");
        SampleVolume(notifyIfChanged: false);
        SampleBrightness(notifyIfChanged: false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(220));
        int brightnessTickCounter = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Environment.TickCount64 >= Interlocked.Read(ref _suppressVolumePollUntilTicks))
                {
                    SampleVolume(notifyIfChanged: true);
                }

                // Sample WMI brightness every ~220ms (ultra light COM query).
                brightnessTickCounter++;
                if (brightnessTickCounter >= 1 &&
                    Environment.TickCount64 >= Interlocked.Read(ref _suppressBrightnessPollUntilTicks))
                {
                    brightnessTickCounter = 0;
                    SampleBrightness(notifyIfChanged: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"HUD monitor stopped: {ex.Message}");
        }
    }

    private void SampleVolume(bool notifyIfChanged)
    {
        if (!TryGetMasterVolume(out float volume, out bool isMuted)) return;

        if (!_volumeSeeded)
        {
            _lastVolume = volume;
            _lastMuted = isMuted;
            _volumeSeeded = true;
            return;
        }

        bool changed = Math.Abs(volume - _lastVolume) >= 0.008f || isMuted != _lastMuted;
        if (!changed) return;

        _lastVolume = volume;
        _lastMuted = isMuted;
        if (notifyIfChanged)
        {
            MasterVolumeChanged?.Invoke(volume, isMuted);
        }
    }

    private void SampleBrightness(bool notifyIfChanged)
    {
        if (!TryGetBrightness(out int percent)) return;

        if (!_brightnessSeeded)
        {
            _lastBrightness = percent;
            _brightnessSeeded = true;
            return;
        }

        if (percent == _lastBrightness) return;

        _lastBrightness = percent;
        if (notifyIfChanged)
        {
            BrightnessChanged?.Invoke(percent);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
        try { _volumeService.Dispose(); } catch { }
    }
}

