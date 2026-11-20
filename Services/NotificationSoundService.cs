using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using VNotch.Models;

namespace VNotch.Services;

internal static class NotificationSoundService
{
    internal const string ThemeCyberpunk = "cyberpunk";
    internal const string ThemeCozy = "cozy";
    internal const string ThemeSystem = "system";
    internal const string ThemeMute = "mute";

    private const string LogCategory = "NOTIFY-SFX";

    private static readonly string SoundsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ATS-Notch", "Sounds");

    private static readonly string BackupPath = Path.Combine(SoundsDir, "windows-defaults-backup.json");

    private static readonly (string KeyPath, string FallbackDefault, bool IsMessaging)[] WindowsSoundKeys =
    [
        (@"AppEvents\Schemes\Apps\.Default\Notification.IM\.Current", @"C:\Windows\media\Windows Notify Messaging.wav", true),
        (@"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current", @"C:\Windows\media\Windows Notify System Generic.wav", false),
        (@"AppEvents\Schemes\Apps\.Default\SystemNotification\.Current", @"C:\Windows\media\Windows Background.wav", false),
    ];

    private static readonly string[] BundledFiles =
    [
        "cyberpunk-whatsapp.wav",
        "cyberpunk-system.wav",
        "cozy-whatsapp.wav",
        "cozy-system.wav",
    ];

    private static string _lastAppliedTheme = string.Empty;

    internal static string NormalizeTheme(string? theme)
    {
        return (theme ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ThemeCyberpunk => ThemeCyberpunk,
            ThemeCozy => ThemeCozy,
            ThemeSystem => ThemeSystem,
            ThemeMute => ThemeMute,
            _ => ThemeCyberpunk
        };
    }

    internal static void EnsureSoundsExtracted()
    {
        try
        {
            Directory.CreateDirectory(SoundsDir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (string file in BundledFiles)
            {
                string dest = Path.Combine(SoundsDir, file);
                if (File.Exists(dest) && new FileInfo(dest).Length > 44) continue;

                string resName = $"VNotch.Sounds.{file}";
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) continue;
                using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.CopyTo(fs);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Failed to extract notification sound assets");
        }
    }

    internal static void ApplyTheme(string? theme, bool force = false)
    {
        string normalized = NormalizeTheme(theme);
        if (!force && string.Equals(_lastAppliedTheme, normalized, StringComparison.Ordinal))
            return;

        EnsureSoundsExtracted();
        BackupWindowsDefaultsOnce();
        SyncWindowsSoundScheme(normalized);
        _lastAppliedTheme = normalized;
    }

    internal static void PlayForNotification(AppNotificationInfo? notification, string? theme)
    {
        string normalized = NormalizeTheme(theme);
        if (normalized == ThemeMute) return;

        // Windows & WhatsApp toasts already play our synced HKCU Windows sound scheme;
        // only play directly here for ATS-Notch internal alerts (Wi-Fi, Bluetooth, Prayer, USB, Battery)
        // so there is never a duplicate/double sound.
        bool isInternal = notification == null ||
            (!string.IsNullOrWhiteSpace(notification.AppId) &&
             notification.AppId.StartsWith("ATS-Notch.", StringComparison.OrdinalIgnoreCase));
        if (!isInternal) return;

        bool isWhatsApp = IsWhatsAppNotification(notification);
        PlaySound(normalized, isWhatsApp);
    }

    internal static void Preview(string? theme, bool isWhatsApp)
    {
        string normalized = NormalizeTheme(theme);
        if (normalized == ThemeMute) return;
        EnsureSoundsExtracted();
        PlaySound(normalized, isWhatsApp);
    }

    private static void PlaySound(string normalizedTheme, bool isWhatsApp)
    {
        try
        {
            if (normalizedTheme == ThemeSystem)
            {
                SystemSounds.Asterisk.Play();
                return;
            }

            string? wavPath = GetThemeWavPath(normalizedTheme, isWhatsApp);
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
            {
                using var player = new SoundPlayer(wavPath);
                player.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Notification sound playback failed");
        }
    }

    internal static string? GetThemeWavPath(string normalizedTheme, bool isWhatsApp)
    {
        return normalizedTheme switch
        {
            ThemeCyberpunk => Path.Combine(SoundsDir, isWhatsApp ? "cyberpunk-whatsapp.wav" : "cyberpunk-system.wav"),
            ThemeCozy => Path.Combine(SoundsDir, isWhatsApp ? "cozy-whatsapp.wav" : "cozy-system.wav"),
            _ => null
        };
    }

    private static bool IsWhatsAppNotification(AppNotificationInfo? notification)
    {
        if (notification == null) return false;
        return (notification.AppName?.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (notification.AppId?.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static void BackupWindowsDefaultsOnce()
    {
        try
        {
            if (File.Exists(BackupPath)) return;
            Directory.CreateDirectory(SoundsDir);
            var backup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyPath, fallback, _) in WindowsSoundKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                string current = key?.GetValue("") as string ?? fallback;
                // Never overwrite backup with our own ATS-Notch sound path.
                if (current.Contains("ATS-Notch", StringComparison.OrdinalIgnoreCase))
                    current = fallback;
                backup[keyPath] = current;
            }
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Failed to back up Windows default notification sounds");
        }
    }

    private static Dictionary<string, string> LoadWindowsDefaultsBackup()
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupPath));
                if (parsed != null) return parsed;
            }
        }
        catch
        {
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SyncWindowsSoundScheme(string normalizedTheme)
    {
        try
        {
            var backup = normalizedTheme == ThemeSystem ? LoadWindowsDefaultsBackup() : null;
            foreach (var (keyPath, fallback, isMessaging) in WindowsSoundKeys)
            {
                string targetValue = normalizedTheme switch
                {
                    ThemeCyberpunk or ThemeCozy => GetThemeWavPath(normalizedTheme, isMessaging) ?? fallback,
                    ThemeMute => string.Empty,
                    _ => backup != null && backup.TryGetValue(keyPath, out var saved) ? saved : fallback
                };

                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                key?.SetValue("", targetValue, RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, $"Failed to sync Windows sound scheme for theme '{normalizedTheme}'");
        }
    }
}

