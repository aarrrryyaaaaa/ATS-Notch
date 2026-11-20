using System;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _isPrayerView;
    private bool _isPrayerReminderVisible;
    private int _prayerReminderToken;
    private int _prayerReminderGen;
    private UiScheduledTimer? _prayerReminderDismissTimer;
    private const double PrayerViewWidth = 480;
    private const double PrayerViewHeight = 186;

    private void InitializePrayerView()
    {
        if (PrayerViewControl == null) return;
        PrayerViewControl.UpdateLocationAndRefresh(
            _settings.PrayerCity,
            _settings.PrayerLatitude,
            _settings.PrayerLongitude,
            _settings.PrayerTzOffset);
        PrayerViewControl.ResetAccentPalette();

        PrayerViewControl.LocationSelected += preset =>
        {
            _settings.PrayerCity = preset.Name;
            _settings.PrayerLatitude = preset.Latitude;
            _settings.PrayerLongitude = preset.Longitude;
            _settings.PrayerTzOffset = preset.TzOffsetHours;
            _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
        };

        PrayerViewControl.LocationInputModeRequested += active =>
        {
            if (_cleanedUp) return;
            if (active) EnableKeyboardInput();
            else DisableKeyboardInput();
        };
    }

    private void PrayerIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SwitchToPrayerView();
    }

    internal void SwitchToPrayerView()
    {
        if (!_isPrayerView && !_isAnimating)
        {
            _transitionCoordinator.RequestView(NotchView.Prayer, "SwitchToPrayerView");
        }
    }

    private double MeasurePrayerViewHeight()
    {
        if (PrayerContent == null || PrayerViewControl == null) return PrayerViewHeight;
        PrayerViewControl.RefreshNow();
        double contentWidth = Math.Max(0, PrayerViewWidth - PrayerContent.Margin.Left - PrayerContent.Margin.Right);
        PrayerViewControl.Measure(new Size(contentWidth, double.PositiveInfinity));
        return Math.Max(PrayerViewHeight, Math.Ceiling(
            PrayerViewControl.DesiredSize.Height + PrayerContent.Margin.Top + PrayerContent.Margin.Bottom));
    }

    private void FitPrayerView()
    {
        if (PrayerContent == null || _cleanedUp) return;
        PrayerViewControl?.ResetAccentPalette();
        double height = MeasurePrayerViewHeight();
        PrayerContent.Width = PrayerViewWidth - PrayerContent.Margin.Left - PrayerContent.Margin.Right;
        PrayerContent.Height = Math.Max(0, height - PrayerContent.Margin.Top - PrayerContent.Margin.Bottom);
        ResizeHostWindowHeight(height);
        NotchBorder.Height = height;
        PrayerViewControl?.PlayEntranceAnimation();
    }

    /// <summary>
    /// Displays Nusa-style Prayer Time arrival reminder right inside the notch capsule,
    /// using Nusa's non-green aurora gradient and pausing active media playback.
    /// </summary>
    internal void ShowPrayerArrivalReminder(string prayerName, string timeLabel, string locationName)
    {
        if (_cleanedUp || PrayerReminderBanner == null) return;
        if (!_isNotchVisible || _isGreetingActive || _isExpanded || _isAnimating || IsCountdownCompletionVisualActive) return;

        if (!TryAcquireCompactSlot(CompactPillSlot.Charging, out int token))
        {
            return;
        }

        // Pause playing media on prayer time arrival (matching Nusa behavior).
        if (_currentMediaInfo?.IsPlaying == true)
        {
            _ = _mediaService.PlayPauseAsync();
        }
        try { SystemSounds.Asterisk.Play(); } catch { }

        _prayerReminderToken = token;
        int gen = ++_prayerReminderGen;
        _isPrayerReminderVisible = true;
        UpdateNotchEdgeOpacity();

        Color auroraLeft = Controls.PrayerNotchView.DefaultAccentLeft;
        Color auroraMid = Controls.PrayerNotchView.DefaultAccentMid;
        Color auroraRight = Controls.PrayerNotchView.DefaultAccentRight;

        PrayerReminderTitleText.Text = prayerName;
        PrayerReminderSubtitleText.Text = $"Waktunya sholat · {locationName}";
        if (PrayerReminderTimeText != null)
        {
            PrayerReminderTimeText.Text = timeLabel;
            PrayerReminderTimeText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xCF, 0xFF));
        }

        if (PrayerReminderHaloStop != null)
            PrayerReminderHaloStop.Color = Color.FromArgb(0x66, auroraLeft.R, auroraLeft.G, auroraLeft.B);
        if (PrayerReminderWaveStopLeft != null) PrayerReminderWaveStopLeft.Color = auroraLeft;
        if (PrayerReminderWaveStopMid != null) PrayerReminderWaveStopMid.Color = auroraMid;
        if (PrayerReminderWaveStopRight != null) PrayerReminderWaveStopRight.Color = auroraRight;

        if (CollapsedContent != null)
        {
            CollapsedContent.BeginAnimation(OpacityProperty, null);
            CollapsedContent.Opacity = 0;
            CollapsedContent.Visibility = Visibility.Collapsed;
        }
        if (MusicCompactContent != null)
        {
            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = 0;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }

        double targetWidth = Math.Max(_collapsedWidth + 96, 326);
        double targetHeight = Math.Max(_collapsedHeight + 18, 56);
        double targetRadius = _settings.EnableDynamicIslandMode ? targetHeight * 0.5 : Math.Max(_cornerRadiusCollapsed, 18.0);

        AnimateCompactWidth(targetWidth, _dur350, _easeExpOut6, token);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, MakeAnim(targetHeight, _dur350, _easeExpOut6, AnimationConfig.TargetFps));
        AnimateCornerRadius(targetRadius, _dur350.TimeSpan);

        PrayerReminderBanner.Visibility = Visibility.Visible;
        PrayerReminderBanner.BeginAnimation(OpacityProperty, null);
        PrayerReminderBanner.BeginAnimation(OpacityProperty, MakeAnim(0, 1, _dur250, _easeExpOut6));

        if (!AnimationConfig.ReduceMotion)
        {
            if (PrayerReminderMoonRotate != null && PrayerReminderMoonScale != null)
            {
                PrayerReminderMoonRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                PrayerReminderMoonScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PrayerReminderMoonScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                var rot = new DoubleAnimation(-150, 0, TimeSpan.FromMilliseconds(950))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var scale = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(900))
                {
                    EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(rot, AnimationConfig.TargetFps);
                Timeline.SetDesiredFrameRate(scale, AnimationConfig.TargetFps);
                PrayerReminderMoonRotate.BeginAnimation(RotateTransform.AngleProperty, rot);
                PrayerReminderMoonScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
                PrayerReminderMoonScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            }
            if (PrayerReminderRingScale != null)
            {
                var ring = new DoubleAnimation(0.9, 1.4, TimeSpan.FromMilliseconds(1050))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(ring, 30);
                PrayerReminderRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, ring);
                PrayerReminderRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, ring);
            }
            if (PrayerReminderWaveBar != null)
            {
                var wave = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(1400))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(wave, 30);
                PrayerReminderWaveBar.BeginAnimation(OpacityProperty, wave);
            }
            if (PrayerReminderProgressScale != null)
            {
                PrayerReminderProgressScale.ScaleX = 1.0;
                var countdown = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(9))
                {
                    EasingFunction = null
                };
                Timeline.SetDesiredFrameRate(countdown, 30);
                PrayerReminderProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, countdown);
            }
        }

        _prayerReminderDismissTimer?.Stop();
        _prayerReminderDismissTimer ??= new UiScheduledTimer { Interval = TimeSpan.FromSeconds(9) };
        _prayerReminderDismissTimer.Tick -= OnPrayerReminderTimerTick;
        _prayerReminderDismissTimer.Tick += OnPrayerReminderTimerTick;
        _prayerReminderDismissTimer.Start();

        void OnPrayerReminderTimerTick(object? s, EventArgs e)
        {
            _prayerReminderDismissTimer?.Stop();
            if (gen == _prayerReminderGen)
            {
                DismissPrayerArrivalReminder();
            }
        }
    }

    internal void DismissPrayerArrivalReminder()
    {
        if (!_isPrayerReminderVisible || PrayerReminderBanner == null) return;
        _isPrayerReminderVisible = false;
        _prayerReminderDismissTimer?.Stop();
        int gen = ++_prayerReminderGen;
        int token = _prayerReminderToken;
        _prayerReminderToken = 0;

        PrayerReminderRingScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PrayerReminderRingScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PrayerReminderWaveBar?.BeginAnimation(OpacityProperty, null);
        UpdateNotchEdgeOpacity();

        var fadeOut = MakeAnim(PrayerReminderBanner.Opacity, 0.0, _dur200, _easeQuadOut);
        fadeOut.Completed += (_, _) =>
        {
            if (gen != _prayerReminderGen) return;
            PrayerReminderBanner.Visibility = Visibility.Collapsed;
            PrayerReminderBanner.BeginAnimation(OpacityProperty, null);
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
        };
        PrayerReminderBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    internal void DismissPrayerArrivalReminderImmediate()
    {
        if (!_isPrayerReminderVisible) return;
        _isPrayerReminderVisible = false;
        _prayerReminderDismissTimer?.Stop();
        ++_prayerReminderGen;
        int token = _prayerReminderToken;
        _prayerReminderToken = 0;

        PrayerReminderRingScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PrayerReminderRingScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PrayerReminderMoonRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
        PrayerReminderMoonScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PrayerReminderMoonScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PrayerReminderWaveBar?.BeginAnimation(OpacityProperty, null);
        PrayerReminderProgressScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (PrayerReminderBanner != null)
        {
            PrayerReminderBanner.BeginAnimation(OpacityProperty, null);
            PrayerReminderBanner.Opacity = 0;
            PrayerReminderBanner.Visibility = Visibility.Collapsed;
        }
        _compactPillArbiter.Release(token);
        UpdateNotchEdgeOpacity();
    }

    private void PrayerReminderBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissPrayerArrivalReminder();
    }
}
