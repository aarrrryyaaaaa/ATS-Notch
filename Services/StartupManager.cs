using System.ComponentModel;
using System.IO;
using Microsoft.Win32;

namespace VNotch.Services;

public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "ATS-Notch";
    private const string LegacyValueName = "V-Notch";

    public bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            if (key == null) return false;
            var current = key.GetValue(AppValueName) as string;
            if (!string.IsNullOrWhiteSpace(current)) return true;
            var legacy = key.GetValue(LegacyValueName) as string;
            return !string.IsNullOrWhiteSpace(legacy);
        }
        catch
        {
            return false;
        }
    }

    public void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open Windows startup registry key.");

        if (enable)
        {
            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ATS-Notch.exe");
            key.SetValue(AppValueName, $"\"{exePath}\"", RegistryValueKind.String);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }

    internal static void EnsureChangeSucceeded(int status, int win32Error)
    {
        if (win32Error != 0) throw new Win32Exception(win32Error);
        if (status != 0) throw new InvalidOperationException($"Startup settings failed (native status {status}).");
    }
}
