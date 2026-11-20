using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private DispatcherTimer? _idleClockTimer;
    private DispatcherTimer? _idleBlinkTimer;
    private DispatcherTimer? _idleSoundVizTimer;
    private double _idleSoundPhase;
    private double _idleSoundSmoothedPeak;
    private int _idleAudioMeterTickCount;
    private IAudioMeterInformation? _idleAudioMeterMulti;
    private IAudioMeterInformation? _idleAudioMeterComm;
    private readonly Random _idleFaceRng = new();
    private bool _isIdleFaceHovered;
    private string _lastAdhanNotifiedKey = string.Empty;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumeratorLite
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceLite ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceLite
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    private void InitializeIdleFaceAndClock()
    {
        UpdateIdleClockText();

        _idleClockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _idleClockTimer.Tick += OnIdleClockTick;
        _idleClockTimer.Start();

        _idleBlinkTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(3000)
        };
        _idleBlinkTimer.Tick += OnIdleBlinkTick;
        _idleBlinkTimer.Start();

        StartIdleBuddyBreathingLoop();
        StartIdleSoundVisualizerLoop();

        if (NotchBorder != null)
        {
            NotchBorder.MouseEnter += (_, _) => SetIdleFaceHover(true);
            NotchBorder.MouseLeave += (_, _) => SetIdleFaceHover(false);
            NotchBorder.MouseMove += (_, e) => UpdateIdleFaceMouseReaction(e.GetPosition(NotchBorder));
        }
    }

    private void StartIdleBuddyBreathingLoop()
    {
        if (AnimationConfig.ReduceMotion || IdleBuddyBob == null) return;
        var bobAnim = new DoubleAnimation(-1.3, 1.3, TimeSpan.FromMilliseconds(1700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(bobAnim, 30);
        IdleBuddyBob.BeginAnimation(TranslateTransform.YProperty, bobAnim);
    }

    private void StartIdleSoundVisualizerLoop()
    {
        InitIdleAudioMeter();
        _idleSoundVizTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _idleSoundVizTimer.Tick += OnIdleSoundVizTick;
        _idleSoundVizTimer.Start();
    }

    private void InitIdleAudioMeter()
    {
        ReleaseIdleAudioMeters();
        try
        {
            var enumerator = (IMMDeviceEnumeratorLite)new MMDeviceEnumeratorCom();
            _idleAudioMeterMulti = TryActivateMeter(enumerator, role: 1); // eMultimedia
            _idleAudioMeterComm = TryActivateMeter(enumerator, role: 2);  // eCommunications (Zoom/Meet/Discord)
            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            ReleaseIdleAudioMeters();
        }
    }

    private static IAudioMeterInformation? TryActivateMeter(IMMDeviceEnumeratorLite enumerator, int role)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0, role, out var device) == 0 && device != null)
            {
                Guid iid = typeof(IAudioMeterInformation).GUID;
                IAudioMeterInformation? meter = null;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) == 0)
                {
                    meter = obj as IAudioMeterInformation;
                }
                Marshal.ReleaseComObject(device);
                return meter;
            }
        }
        catch
        {
        }
        return null;
    }

    private void ReleaseIdleAudioMeters()
    {
        if (_idleAudioMeterMulti != null)
        {
            try { Marshal.ReleaseComObject(_idleAudioMeterMulti); } catch { }
            _idleAudioMeterMulti = null;
        }
        if (_idleAudioMeterComm != null)
        {
            try { Marshal.ReleaseComObject(_idleAudioMeterComm); } catch { }
            _idleAudioMeterComm = null;
        }
    }

    private void OnIdleSoundVizTick(object? sender, EventArgs e)
    {
        if (_cleanedUp || _isExpanded || CollapsedContent == null || CollapsedContent.Visibility != Visibility.Visible)
        {
            return;
        }

        _idleAudioMeterTickCount++;
        if ((_idleAudioMeterMulti == null && _idleAudioMeterComm == null) || _idleAudioMeterTickCount >= 40)
        {
            _idleAudioMeterTickCount = 0;
            InitIdleAudioMeter();
        }

        float peakMulti = 0f, peakComm = 0f;
        try { _idleAudioMeterMulti?.GetPeakValue(out peakMulti); } catch { _idleAudioMeterMulti = null; }
        try { _idleAudioMeterComm?.GetPeakValue(out peakComm); } catch { _idleAudioMeterComm = null; }
        float rawPeak = Math.Max(peakMulti, peakComm);

        double boosted = Math.Clamp(Math.Pow(rawPeak, 0.55) * 1.35, 0.0, 1.0);
        _idleSoundSmoothedPeak = boosted > _idleSoundSmoothedPeak
            ? _idleSoundSmoothedPeak * 0.45 + boosted * 0.55
            : _idleSoundSmoothedPeak * 0.78 + boosted * 0.22;

        _idleSoundPhase += _idleSoundSmoothedPeak > 0.04 ? 0.65 : 0.16;

        double baseLevel = 0.22 + _idleSoundSmoothedPeak * 0.72;
        double amp = _idleSoundSmoothedPeak > 0.04 ? (0.14 + _idleSoundSmoothedPeak * 0.34) : 0.06;

        SetIdleBarScale(IdleSoundBar1Scale, baseLevel * 0.72 + Math.Sin(_idleSoundPhase + 0.0) * amp);
        SetIdleBarScale(IdleSoundBar2Scale, baseLevel * 0.92 + Math.Sin(_idleSoundPhase + 1.1) * amp);
        SetIdleBarScale(IdleSoundBar3Scale, baseLevel * 1.08 + Math.Sin(_idleSoundPhase + 2.2) * amp);
        SetIdleBarScale(IdleSoundBar4Scale, baseLevel * 0.90 + Math.Sin(_idleSoundPhase + 3.3) * amp);
        SetIdleBarScale(IdleSoundBar5Scale, baseLevel * 0.70 + Math.Sin(_idleSoundPhase + 4.4) * amp);
    }

    private static void SetIdleBarScale(ScaleTransform? scale, double value)
    {
        if (scale == null) return;
        scale.ScaleY = Math.Clamp(value, 0.18, 1.0);
    }

    private void DisposeIdleFaceAndClock()
    {
        _idleSoundVizTimer?.Stop();
        _idleSoundVizTimer = null;
        ReleaseIdleAudioMeters();
        _idleClockTimer?.Stop();
        _idleClockTimer = null;
        _idleBlinkTimer?.Stop();
        _idleBlinkTimer = null;
        _prayerReminderDismissTimer?.Stop();
    }

    private void OnIdleClockTick(object? sender, EventArgs e)
    {
        if (_cleanedUp) return;
        UpdateIdleClockText();

        if (_isPrayerView)
        {
            PrayerViewControl?.RefreshNow();
        }

        CheckAndDispatchAdhanNotification();
    }

    private static readonly System.Globalization.CultureInfo IndonesianHeaderCulture =
        System.Globalization.CultureInfo.GetCultureInfo("id-ID");

    private void UpdateIdleClockText()
    {
        var now = DateTime.Now;
        string nowText = now.ToString("HH:mm:ss");
        if (IdleClockText != null && !string.Equals(IdleClockText.Text, nowText, StringComparison.Ordinal))
        {
            IdleClockText.Text = nowText;
        }
        if (MusicCompactClockText != null && !string.Equals(MusicCompactClockText.Text, nowText, StringComparison.Ordinal))
        {
            MusicCompactClockText.Text = nowText;
        }
        if (HeaderDateText != null)
        {
            string dateText = now.ToString("dddd, dd MMM yyyy", IndonesianHeaderCulture);
            if (!string.Equals(HeaderDateText.Text, dateText, StringComparison.Ordinal))
            {
                HeaderDateText.Text = dateText;
            }
        }
    }

    private void OnIdleBlinkTick(object? sender, EventArgs e)
    {
        if (_idleBlinkTimer == null || _cleanedUp) return;
        _idleBlinkTimer.Interval = TimeSpan.FromMilliseconds(_idleFaceRng.Next(2200, 4800));

        if (AnimationConfig.ReduceMotion || _isExpanded || _isMusicCompactMode ||
            CollapsedContent == null || CollapsedContent.Visibility != Visibility.Visible)
        {
            return;
        }

        int roll = _idleFaceRng.Next(0, 7);
        PlayIdleFaceBlink(doubleBlink: roll == 0, winkLeftOnly: roll == 1);
    }

    private void PlayIdleFaceBlink(bool doubleBlink, bool winkLeftOnly = false)
    {
        if (IdleEyeLeftScale == null || IdleEyeRightScale == null) return;

        var blinkAnim = new DoubleAnimationUsingKeyFrames();
        blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75))));
        blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        if (doubleBlink)
        {
            blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(225))));
            blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290))));
            blinkAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(375))));
        }

        Timeline.SetDesiredFrameRate(blinkAnim, AnimationConfig.TargetFps);
        IdleEyeLeftScale.BeginAnimation(ScaleTransform.ScaleYProperty, blinkAnim);
        if (!winkLeftOnly)
        {
            IdleEyeRightScale.BeginAnimation(ScaleTransform.ScaleYProperty, blinkAnim);
        }
    }

    internal void UpdateIdleFaceMouseReaction(Point pointerInNotch)
    {
        if (_isExpanded || _isMusicCompactMode || AnimationConfig.ReduceMotion ||
            CollapsedContent == null || CollapsedContent.Visibility != Visibility.Visible ||
            NotchBorder == null || IdleFaceLookOffset == null)
        {
            return;
        }

        double w = Math.Max(1.0, NotchBorder.ActualWidth);
        double h = Math.Max(1.0, NotchBorder.ActualHeight);
        double nx = Math.Clamp((pointerInNotch.X / w - 0.5) * 2.0, -1.0, 1.0);
        double ny = Math.Clamp((pointerInNotch.Y / h - 0.5) * 2.0, -1.0, 1.0);

        IdleFaceLookOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(nx * 2.6, TimeSpan.FromMilliseconds(85)));
        IdleFaceLookOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(ny * 1.4, TimeSpan.FromMilliseconds(85)));

        if (IdlePupilLeftOffset != null && IdlePupilRightOffset != null)
        {
            var animX = new DoubleAnimation(nx * 1.45, TimeSpan.FromMilliseconds(65));
            var animY = new DoubleAnimation(ny * 1.35, TimeSpan.FromMilliseconds(65));
            IdlePupilLeftOffset.BeginAnimation(TranslateTransform.XProperty, animX);
            IdlePupilLeftOffset.BeginAnimation(TranslateTransform.YProperty, animY);
            IdlePupilRightOffset.BeginAnimation(TranslateTransform.XProperty, animX);
            IdlePupilRightOffset.BeginAnimation(TranslateTransform.YProperty, animY);
        }
    }

    internal void SetIdleFaceHover(bool hovered)
    {
        if (_isIdleFaceHovered == hovered) return;
        _isIdleFaceHovered = hovered;
        if (AnimationConfig.ReduceMotion) return;

        var dur = TimeSpan.FromMilliseconds(180);
        if (IdleCheekLeft != null)
            IdleCheekLeft.BeginAnimation(OpacityProperty, new DoubleAnimation(hovered ? 0.72 : 0.16, dur));
        if (IdleCheekRight != null)
            IdleCheekRight.BeginAnimation(OpacityProperty, new DoubleAnimation(hovered ? 0.72 : 0.16, dur));
        if (IdleFaceMouthScale != null)
        {
            IdleFaceMouthScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(hovered ? 1.22 : 1.0, dur));
            IdleFaceMouthScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(hovered ? 1.22 : 1.0, dur));
        }

        if (!hovered)
        {
            if (IdleFaceLookOffset != null)
            {
                IdleFaceLookOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur));
                IdleFaceLookOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur));
            }
            if (IdlePupilLeftOffset != null && IdlePupilRightOffset != null)
            {
                IdlePupilLeftOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur));
                IdlePupilLeftOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur));
                IdlePupilRightOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur));
                IdlePupilRightOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur));
            }
        }
    }

    private void CheckAndDispatchAdhanNotification()
    {
        if (!_settings.PrayerAdhanNotify) return;

        var now = DateTime.Now;
        var schedule = PrayerTimesService.ComputeSchedule(
            _settings.PrayerCity, now,
            _settings.PrayerLatitude, _settings.PrayerLongitude, _settings.PrayerTzOffset);

        for (int i = 0; i < schedule.Entries.Count; i++)
        {
            var entry = schedule.Entries[i];
            if (!entry.IsMainPrayer) continue;

            if (now.Hour == entry.Time.Hours && now.Minute == entry.Time.Minutes && now.Second <= 5)
            {
                string key = $"{now:yyyy-MM-dd}:{entry.Name}";
                if (string.Equals(_lastAdhanNotifiedKey, key, StringComparison.Ordinal)) return;
                _lastAdhanNotifiedKey = key;

                ShowPrayerArrivalReminder(entry.Name, $"{entry.Time.Hours:D2}:{entry.Time.Minutes:D2}", schedule.CityName);
                return;
            }
        }
    }
}
