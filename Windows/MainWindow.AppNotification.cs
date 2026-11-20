using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private AppNotificationService? _appNotificationService;
    private WindowsToastSuppressor? _windowsToastSuppressor;
    private bool _isAppNotificationBannerVisible;
    private bool _isAppNotificationBannerExiting;
    private bool _isAppNotificationHovered;
    private int _appNotificationGeneration;
    private int _appNotificationMorphGeneration;
    private double _appNotificationBaseWidth;
    private double _appNotificationBaseHeight;
    private double _appNotificationBaseCornerRadius;
    private AppNotificationInfo? _activeAppNotification;
    private AppNotificationInfo? _queuedAppNotification;
    private UiScheduledTimer? _queuedAppNotificationRetryTimer;
    private UiScheduledTimer? _appNotificationDismissTimer;
    private long _appNotificationDismissDeadline;
    private int _appNotificationRemainingMs;
    private bool _isAiQuotaNotification;
    private bool AutoDismissAppNotification => _isAiQuotaNotification || _settings.AutoDismissNotifications;

    internal bool IsAppNotificationBannerActive => _isAppNotificationBannerVisible;

    private void InitializeAppNotificationBanner()
    {
        _appNotificationService = new AppNotificationService(() => !_cleanedUp && _settings.ShowSystemNotifications);
        _appNotificationService.NotificationReceived += OnAppNotificationReceived;
        _appNotificationService.Start();

        _windowsToastSuppressor = new WindowsToastSuppressor(
            Dispatcher,
            () => !_cleanedUp && _settings.ShowSystemNotifications && _settings.HideWindowsToastBanners);
        _windowsToastSuppressor.Start();
    }

    private void ScheduleQueuedNotificationRetry()
    {
        if (_queuedAppNotificationRetryTimer == null)
        {
            _queuedAppNotificationRetryTimer = new UiScheduledTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _queuedAppNotificationRetryTimer.Tick += (_, _) =>
            {
                _queuedAppNotificationRetryTimer.Stop();
                var queued = _queuedAppNotification;
                _queuedAppNotification = null;
                if (queued != null && !_cleanedUp)
                {
                    PresentAppNotificationBanner(queued, isQuotaAlert: false);
                }
            };
        }
        _queuedAppNotificationRetryTimer.Stop();
        _queuedAppNotificationRetryTimer.Start();
    }

    private void DisposeAppNotificationBanner()
    {
        CancelAppNotificationBannerImmediate();
        _queuedAppNotificationRetryTimer?.Stop();
        _queuedAppNotificationRetryTimer?.Dispose();
        _queuedAppNotificationRetryTimer = null;
        _queuedAppNotification = null;
        var suppressor = _windowsToastSuppressor;
        _windowsToastSuppressor = null;
        suppressor?.Dispose();

        var service = _appNotificationService;
        _appNotificationService = null;
        if (service != null)
        {
            service.NotificationReceived -= OnAppNotificationReceived;
            service.Dispose();
        }
    }

    private void OnAppNotificationReceived(AppNotificationInfo notification)
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() => ShowAppNotification(notification));
    }

    internal void ShowAppNotification(AppNotificationInfo? notification)
        => PresentAppNotificationBanner(notification, isQuotaAlert: false);

    private void PresentAppNotificationBanner(AppNotificationInfo? notification, bool isQuotaAlert,
        System.Collections.Generic.IReadOnlyList<VNotch.Services.AiUsage.AiQuotaAlert>? quotaAlerts = null)
    {
        if (notification == null || _cleanedUp || (!isQuotaAlert && !_settings.ShowSystemNotifications)) return;

        if (!isQuotaAlert && IsCompactScreenshotToast(notification))
        {
            ShowCompactSystemPill("Screenshot", CompactSystemKind.Screenshot);
            return;
        }

        if (!_isNotchVisible || _isGreetingActive || _isExpanded || _isAnimating || IsCountdownCompletionVisualActive)
        {
            if (_isGreetingActive || _isExpanded || _isAnimating || IsCountdownCompletionVisualActive)
            {
                _queuedAppNotification = notification;
                ScheduleQueuedNotificationRetry();
            }
            RuntimeLog.Log("APP-NOTIFY", $"Deferred/skipped notch banner ({notification.AppName}: {notification.Title}) state visible={_isNotchVisible} greeting={_isGreetingActive} expanded={_isExpanded} animating={_isAnimating}");
            return;
        }
        _queuedAppNotification = null;
        RuntimeLog.Log("APP-NOTIFY", $"Presenting notch banner: [{notification.AppName}] {notification.Title}");

        _isAiQuotaNotification = isQuotaAlert;
        WakeFromIdle();
        int generation = ++_appNotificationGeneration;
        bool replacing = _isAppNotificationBannerVisible && !_isAppNotificationBannerExiting;
        _isAppNotificationBannerVisible = true;
        _isAppNotificationBannerExiting = false;
        _activeAppNotification = notification;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        ResetAppNotificationVisuals();

        if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None)
        {
            CancelCompactSlotImmediate(_compactPillArbiter.ActiveSlot);
            _compactPillArbiter.ForceClear();
        }
        if (_isCompactThumbnailHovered)
        {
            _isCompactThumbnailHovered = false;
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        HidePrivacyIndicators();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;

        AppNotificationContent.Visibility = isQuotaAlert ? Visibility.Collapsed : Visibility.Visible;
        AiQuotaBannerContent.Visibility = isQuotaAlert ? Visibility.Visible : Visibility.Collapsed;
        AppNotificationBanner.Focusable = !isQuotaAlert;
        AppNotificationBanner.Cursor = isQuotaAlert ? Cursors.Arrow : Cursors.Hand;
        AppNotificationBanner.Visibility = Visibility.Visible;
        int dismissDurationMs;
        if (isQuotaAlert)
        {
            AiQuotaBannerContent.Present(quotaAlerts ?? []);
            _appNotificationBaseWidth = Math.Max(380, GetCollapsedWidth());
            _appNotificationBaseCornerRadius = _settings.EnableDynamicIslandMode ? 22 : _cornerRadiusCollapsed;
            AppNotificationBanner.Width = _appNotificationBaseWidth - 36;
            double top = _settings.EnableDynamicIslandMode ? 8 : Math.Max(14, GetCollapsedHeight() - 6);
            AppNotificationBanner.Margin = new Thickness(0, top, 0, 10);
            AppNotificationBanner.Measure(new Size(AppNotificationBanner.Width, double.PositiveInfinity));
            _appNotificationBaseHeight = Math.Max(GetCollapsedHeight(), AppNotificationBanner.DesiredSize.Height);
            dismissDurationMs = 5000;
        }
        else
        {
            ApplyAppNotificationContent(notification);
            var metrics = AppNotificationService.ComputeBannerMetrics(
                _collapsedWidth > 0 ? _collapsedWidth : GetCollapsedWidth(),
                _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight(),
                _expandedWidth, _cornerRadiusCollapsed, _cornerRadiusExpanded,
                _settings.EnableDynamicIslandMode, AnimationConfig.ReduceMotion,
                _settings.NotificationDuration, AppNotificationTitleText.Text, AppNotificationBodyText.Text);
            int longestLine = Math.Max(AppNotificationTitleText.Text.Length,
                Math.Max(AppNotificationAppNameText.Text.Length + 6, Math.Min(AppNotificationBodyText.Text.Length, 44)));
            double maxBannerWidth = Math.Min(428.0, Math.Max(396.0, metrics.TargetWidth));
            _appNotificationBaseWidth = Math.Clamp(224.0 + longestLine * 7.2, 344.0, maxBannerWidth);
            _appNotificationBaseCornerRadius = metrics.TargetCornerRadius;
            AppNotificationBanner.Width = Math.Max(280, _appNotificationBaseWidth - 24);
            AppNotificationBanner.Measure(new Size(AppNotificationBanner.Width, double.PositiveInfinity));
            double collapsedH = _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight();
            _appNotificationBaseHeight = Math.Max(collapsedH + 38.0, AppNotificationBanner.DesiredSize.Height + 6.0);
            dismissDurationMs = metrics.DismissDurationMs;
        }
        AppNotificationBanner.Visibility = Visibility.Visible;
        AppNotificationProgressTrack.Visibility = Visibility.Collapsed;
        if (Height < _appNotificationBaseHeight + 32) ResizeHostWindowHeight(_appNotificationBaseHeight);

        AppNotificationBanner.IsHitTestVisible = true;
        if (!replacing && !isQuotaAlert) PlayAppNotificationSound();
        PresentAppNotification(replacing);
        AnimateAppNotificationShell(
            _appNotificationBaseWidth, _appNotificationBaseHeight,
            _appNotificationBaseCornerRadius, isQuotaAlert ? 420 : 420,
            easing: _easeSoftSpring);
        if (isQuotaAlert) AiQuotaBannerContent.PlayEntrance();
        _appNotificationRemainingMs = dismissDurationMs;
        if (!_isAppNotificationHovered && AutoDismissAppNotification)
            ScheduleAppNotificationDismiss(_appNotificationRemainingMs, generation);
    }

    private void ApplyAppNotificationContent(AppNotificationInfo notification)
    {
        string appName = string.IsNullOrWhiteSpace(notification.AppName) ? "Notification" : notification.AppName;
        string rawTitle = notification.Title ?? string.Empty;
        string rawBody = notification.Body ?? string.Empty;

        string displayTitle;
        string displayBody;
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            displayTitle = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : appName;
            displayBody = rawBody;
        }
        else if (!string.IsNullOrWhiteSpace(rawTitle) &&
                 !string.Equals(rawTitle, appName, StringComparison.OrdinalIgnoreCase))
        {
            displayTitle = appName;
            displayBody = rawTitle;
        }
        else
        {
            displayTitle = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : appName;
            displayBody = string.Empty;
        }

        AppNotificationAppNameText.Text = appName;
        AppNotificationTitleText.Text = displayTitle;
        AppNotificationBodyText.Text = displayBody;
        AppNotificationBodyText.Visibility = string.IsNullOrWhiteSpace(displayBody) ? Visibility.Collapsed : Visibility.Visible;
        AppNotificationTimeText.Text = "now";

        bool showSystemIcon = IsSystemActivityNotification(notification.AppId, appName, displayTitle);
        if (showSystemIcon)
        {
            AppNotificationIconHost.Visibility = Visibility.Visible;
            AppNotificationIconImage.Source = null;
            AppNotificationIconImage.Visibility = Visibility.Collapsed;
            AppNotificationDefaultIcon.Visibility = Visibility.Visible;
            if (AppNotificationVectorIcon != null)
            {
                AppNotificationVectorIcon.Data = Geometry.Parse(ResolveFallbackNotificationGlyph(appName, notification.AppId, displayTitle));
            }
            if (AppNotificationMetaPanel != null) AppNotificationMetaPanel.HorizontalAlignment = HorizontalAlignment.Left;
            AppNotificationTitleText.TextAlignment = TextAlignment.Left;
            AppNotificationBodyText.TextAlignment = TextAlignment.Left;
        }
        else
        {
            AppNotificationIconImage.Source = null;
            AppNotificationIconImage.Visibility = Visibility.Collapsed;
            AppNotificationDefaultIcon.Visibility = Visibility.Collapsed;
            AppNotificationIconHost.Visibility = Visibility.Collapsed;
            if (AppNotificationMetaPanel != null) AppNotificationMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
            AppNotificationTitleText.TextAlignment = TextAlignment.Center;
            AppNotificationBodyText.TextAlignment = TextAlignment.Center;
        }

        double topOffset = _settings.EnableDynamicIslandMode ? 8.0 : 10.0;
        AppNotificationBanner.Margin = new Thickness(0, topOffset, 0, 10);
    }

    private static bool IsSystemActivityNotification(string? appId, string appName, string title)
    {
        if (!string.IsNullOrWhiteSpace(appId) && appId.StartsWith("ATS-Notch.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string key = $"{appName} {title}".ToLowerInvariant();
        return key.Contains("wi-fi") || key.Contains("wifi") || key.Contains("internet") ||
               key.Contains("jaringan") || key.Contains("ethernet") || key.Contains("offline") ||
               key.Contains("bluetooth") || key.Contains("caps lock") || key.Contains("num lock") ||
               key.Contains("scroll lock") || key.Contains("usb") || key.Contains("baterai") ||
               key.Contains("charger");
    }

    private static string ResolveFallbackNotificationGlyph(string appName, string? appId, string title)
    {
        string key = $"{appName} {appId} {title}".ToLowerInvariant();
        if (key.Contains("wi-fi") || key.Contains("wifi"))
            return "M12,18c1.1,0,2,0.9,2,2s-0.9,2-2,2s-2-0.9-2-2S10.9,18,12,18z M12,12c3.04,0,5.8,1.24,7.78,3.24l-2.12,2.12C16.21,15.9,14.21,15,12,15s-4.21,0.9-5.66,2.36l-2.12-2.12C6.2,13.24,8.96,12,12,12z M12,6c4.7,0,8.95,1.91,12.02,5l-2.12,2.12C19.38,10.58,15.88,9,12,9s-7.38,1.58-9.9,4.12L0,11C3.05,7.91,7.3,6,12,6z";
        if (key.Contains("internet") || key.Contains("jaringan") || key.Contains("ethernet") || key.Contains("offline"))
            return "M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10s10-4.48,10-10S17.52,2,12,2z M11,19.93C7.05,19.44,4,16.08,4,12c0-0.62,0.08-1.21,0.21-1.79L9,15v1c0,1.1,0.9,2,2,2V19.93z M17.9,17.39c-0.26-0.81-1-1.39-1.9-1.39h-1v-3c0-0.55-0.45-1-1-1H8v-2h2c0.55,0,1-0.45,1-1V7h2c1.1,0,2-0.9,2-2V4.59C17.93,5.78,20,8.65,20,12C20,14.08,19.2,15.97,17.9,17.39z";
        if (key.Contains("usb"))
            return "M15,7v4h1v2h-3V5h2l-3-4l-3,4h2v8H8v-2.07c0.7-0.37,1.2-1.08,1.2-1.93c0-1.21-0.99-2.2-2.2-2.2S4.8,7.79,4.8,9c0,0.85,0.5,1.56,1.2,1.93V13c0,1.11,0.89,2,2,2h3v3.05c-0.71,0.37-1.2,1.1-1.2,1.95c0,1.22,0.99,2.2,2.2,2.2s2.2-0.98,2.2-2.2c0-0.85-0.49-1.58-1.2-1.95V15h3c1.11,0,2-0.89,2-2v-2h1V7H15z";
        if (key.Contains("baterai") || key.Contains("daya") || key.Contains("charger"))
            return "M16,4h-2V2h-4v2H8C6.9,4,6,4.9,6,6v14c0,1.1,0.9,2,2,2h8c1.1,0,2-0.9,2-2V6C18,4.9,17.1,4,16,4z M13,18v-5h-3l4-7v5h3L13,18z";
        return "M12,3C6.5,3,2,6.6,2,11c0,2.1,1,4.1,2.8,5.5C4.4,18.2,3.3,19.8,3.2,20c-0.2,0.2-0.2,0.5,0,0.8C3.3,20.9,3.5,21,3.7,21c2.1,0,4.1-0.9,5.4-1.8c0.9,0.2,1.9,0.3,2.9,0.3c5.5,0,10-3.6,10-8.5S17.5,3,12,3z";
    }

    private static void PlayAppNotificationSound()
    {
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
        }
    }

    internal void AnimateAppNotificationHover(bool isHovered)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        _isAppNotificationHovered = isHovered;
        if (isHovered) PauseAppNotificationDismiss();
        else if (!_appNotificationPointerDown && AutoDismissAppNotification)
            ScheduleAppNotificationDismiss(_appNotificationRemainingMs, _appNotificationGeneration);

        AnimateAppNotificationHoverFeedback();
    }

    private void AnimateAppNotificationHoverFeedback()
    {
        bool interactive = !_isAppNotificationBannerExiting;
        bool hovered = interactive && _isAppNotificationHovered;
        bool pressed = interactive && _appNotificationPointerDown && !_appNotificationDragging;
        bool dragging = interactive && _appNotificationDragging;
        int duration = pressed || hovered ? NotificationFeedbackMs : 180;
        AnimateAppNotificationOpacity(HoverGlow, 0, duration);
        AnimateAppNotificationOpacity(AppNotificationHoverSurface,
            pressed ? .065 : hovered && !dragging ? .035 : 0, duration);
        AnimateAppNotificationValue(AppNotificationPressTranslate, TranslateTransform.YProperty,
            pressed && !AnimationConfig.ReduceMotion ? 1 : 0, duration, easing: _easeAppleOut);
    }

    internal void DismissAppNotificationBanner() => BeginAppNotificationDismiss(0);

    internal void CancelAppNotificationBannerImmediate()
    {
        if (!_isAppNotificationBannerVisible && !_isAppNotificationBannerExiting) return;
        ++_appNotificationGeneration;
        ++_appNotificationMorphGeneration;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        FreezeAppNotificationShell();
        ResetAppNotificationVisuals();
        _isAppNotificationBannerVisible = false;
        _isAppNotificationBannerExiting = false;
        _isAppNotificationHovered = false;
        _activeAppNotification = null;
        AppNotificationBanner.Opacity = 0;
        AppNotificationBanner.Visibility = Visibility.Collapsed;
        AppNotificationBanner.IsHitTestVisible = false;
    }

    private void ScheduleAppNotificationDismiss(int durationMs, int generation)
    {
        if (!AutoDismissAppNotification || _isAppNotificationHovered || _appNotificationPointerDown ||
            !_isAppNotificationBannerVisible || _isAppNotificationBannerExiting || _appNotificationDismissTimer != null) return;
        int remaining = Math.Clamp(durationMs, 1, 15000);
        _appNotificationRemainingMs = remaining;
        _appNotificationDismissDeadline = Environment.TickCount64 + remaining;
        var timer = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(remaining) };
        _appNotificationDismissTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!ReferenceEquals(_appNotificationDismissTimer, timer) || generation != _appNotificationGeneration) return;
            _appNotificationDismissTimer = null;
            _appNotificationDismissDeadline = 0;
            DismissAppNotificationBanner();
        };
        timer.Start();
    }

    private void PauseAppNotificationDismiss()
    {
        if (_appNotificationDismissDeadline > 0)
            _appNotificationRemainingMs = (int)Math.Clamp(_appNotificationDismissDeadline - Environment.TickCount64, 1, 15000);
        double progress = AppNotificationProgressScale.ScaleX;
        StopAppNotificationDismissTimer();
        SetAppNotificationValue(AppNotificationProgressScale, ScaleTransform.ScaleXProperty, progress);
    }

    private void StopAppNotificationDismissTimer()
    {
        var timer = _appNotificationDismissTimer;
        _appNotificationDismissTimer = null;
        _appNotificationDismissDeadline = 0;
        timer?.Stop();
        timer?.Dispose();
    }

    private void AppNotificationBanner_MouseEnter(object sender, MouseEventArgs e) => AnimateAppNotificationHover(true);
    private void AppNotificationBanner_MouseLeave(object sender, MouseEventArgs e) => AnimateAppNotificationHover(false);

    private static bool IsCompactScreenshotToast(AppNotificationInfo notification)
    {
        bool isSnipping = notification.AppId.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase)
            || notification.AppName.Contains("Snipping", StringComparison.OrdinalIgnoreCase)
            || notification.AppName.Contains("Cuplikan", StringComparison.OrdinalIgnoreCase);
        if (!isSnipping) return false;
        return string.IsNullOrWhiteSpace(notification.Body)
            || notification.Body.Contains("clipboard", StringComparison.OrdinalIgnoreCase)
            || notification.Body.Contains("papan klip", StringComparison.OrdinalIgnoreCase)
            || notification.Title.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)
            || notification.Title.Contains("Cuplikan", StringComparison.OrdinalIgnoreCase);
    }

    private void AppNotificationBanner_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissAppNotificationBanner();
    }
}
