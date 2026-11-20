using System;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private SystemActivityMonitor? _systemActivityMonitor;

    private void InitializeSystemActivityMonitor()
    {
        if (_systemActivityMonitor != null) return;
        _systemActivityMonitor = new SystemActivityMonitor(Dispatcher);
        _systemActivityMonitor.ActivityDetected += OnLaptopActivityDetected;
        _systemActivityMonitor.VolumeKeyPressed += OnSystemVolumeKeyPressed;
        _systemActivityMonitor.Start(this);
    }

    private void DisposeSystemActivityMonitor()
    {
        var mon = _systemActivityMonitor;
        _systemActivityMonitor = null;
        if (mon != null)
        {
            mon.ActivityDetected -= OnLaptopActivityDetected;
            mon.VolumeKeyPressed -= OnSystemVolumeKeyPressed;
            mon.Dispose();
        }
    }

    private void OnLaptopActivityDetected(LaptopActivityEvent evt)
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp) return;

            if (string.Equals(evt.Category, "Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                bool isOn = evt.Title.EndsWith("ON", StringComparison.OrdinalIgnoreCase);
                ShowCompactSystemPill(evt.Title, isOn ? CompactSystemKind.LockOn : CompactSystemKind.LockOff);
                return;
            }

            if (string.Equals(evt.Category, "Tombol Fungsi", StringComparison.OrdinalIgnoreCase))
            {
                ShowCompactSystemPill(evt.Title, CompactSystemKind.FnKey);
                return;
            }

            if (string.Equals(evt.Category, "Layar", StringComparison.OrdinalIgnoreCase))
            {
                ShowCompactSystemPill("Screenshot", CompactSystemKind.Screenshot);
                return;
            }

            if (string.Equals(evt.Category, "Daya Laptop", StringComparison.OrdinalIgnoreCase))
            {
                bool plugged = evt.Title.Contains("Terhubung", StringComparison.OrdinalIgnoreCase);
                ShowCompactSystemPill(plugged ? "Charger Terhubung" : "Charger Terputus",
                    plugged ? CompactSystemKind.ChargerOn : CompactSystemKind.ChargerOff);
                return;
            }

            if (string.Equals(evt.Category, "Baterai", StringComparison.OrdinalIgnoreCase))
            {
                ShowCompactSystemPill("Baterai 100%", CompactSystemKind.ChargerOn);
                return;
            }

            ShowAppNotification(new AppNotificationInfo
            {
                Id = unchecked((uint)($"{evt.Category}:{evt.Title}:{DateTime.Now.Ticks % 10000}".GetHashCode())),
                AppId = $"ATS-Notch.{evt.Category}",
                AppName = evt.Category,
                Title = evt.Title,
                Body = evt.Body,
                CreationTime = DateTimeOffset.Now
            });
        });
    }

    private void OnSystemVolumeKeyPressed()
    {
        if (_cleanedUp || _systemHudMonitor == null) return;
        if (_systemHudMonitor.TryGetMasterVolume(out float vol, out bool muted))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_cleanedUp) return;
                _currentVolume = vol;
                _volumeSynced = true;
                ShowVolumeIndicator(muted ? 0f : vol, isBrightness: false, isMutedOverride: muted);
            });
        }
    }
}

