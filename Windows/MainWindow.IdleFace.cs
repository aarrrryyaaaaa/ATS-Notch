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
    private enum IdleRobotMood
    {
        Normal,
        Greet,
        Smile,
        Sleep
    }

    private DispatcherTimer? _idleClockTimer;
    private DispatcherTimer? _idleBlinkTimer;
    private DispatcherTimer? _idleMoodTimer;
    private DispatcherTimer? _idleSoundVizTimer;
    private double _idleSoundPhase;
    private double _idleSoundSmoothedPeak;
    private int _idleAudioMeterTickCount;
    private bool _idleSoundBarsAtRest;
    private IAudioMeterInformation? _idleAudioMeterMulti;
    private IAudioMeterInformation? _idleAudioMeterComm;
    private readonly Random _idleFaceRng = new();
    private bool _isIdleFaceHovered;
    private IdleRobotMood _idleRobotMood = IdleRobotMood.Normal;
    private int _idleMoodCycleIndex;
    private string _lastAdhanNotifiedKey = string.Empty;

    private static readonly IdleRobotMood[] IdleMoodSequence =
    [
        IdleRobotMood.Greet,
        IdleRobotMood.Normal,
        IdleRobotMood.Smile,
        IdleRobotMood.Normal,
        IdleRobotMood.Sleep,
        IdleRobotMood.Normal
    ];

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

        _idleMoodTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _idleMoodTimer.Tick += OnIdleMoodTick;
        _idleMoodTimer.Start();

        StartIdleBuddyBreathingLoop();
        StartIdleSoundVisualizerLoop();
        BatteryPowerSaverService.PowerModeUpdated += OnBatteryPowerModeUpdated;
        BatteryPowerSaverService.Start();

        if (NotchBorder != null)
        {
            NotchBorder.MouseEnter += (_, _) => SetIdleFaceHover(true);
            NotchBorder.MouseLeave += (_, _) => SetIdleFaceHover(false);
            NotchBorder.MouseMove += (_, e) => UpdateIdleFaceMouseReaction(e.GetPosition(NotchBorder));
        }
    }

    private void OnBatteryPowerModeUpdated(bool onBattery)
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp) return;
            if (_idleRobotMood == IdleRobotMood.Normal)
            {
                StartIdleBuddyBreathingLoop();
            }
        });
    }

    private void StartIdleBuddyBreathingLoop()
    {
        if (IdleBuddyBob == null) return;
        // On battery during plain Normal idle, avoid running a 24/7 infinite animation
        // so WPF/DWM can let the CPU enter deep C-states; mood animations still play smoothly.
        if (AnimationConfig.ReduceMotion || BatteryPowerSaverService.IsOnBattery)
        {
            IdleBuddyBob.BeginAnimation(TranslateTransform.YProperty, null);
            IdleBuddyBob.Y = 0;
            return;
        }
        var bobAnim = new DoubleAnimation(-1.3, 1.3, TimeSpan.FromMilliseconds(1700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(bobAnim, 18);
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
        if ((_idleAudioMeterMulti == null && _idleAudioMeterComm == null) || _idleAudioMeterTickCount >= 300)
        {
            _idleAudioMeterTickCount = 0;
            InitIdleAudioMeter();
        }

        float peakMulti = 0f, peakComm = 0f;
        try { _idleAudioMeterMulti?.GetPeakValue(out peakMulti); } catch { _idleAudioMeterMulti = null; }
        try { _idleAudioMeterComm?.GetPeakValue(out peakComm); } catch { _idleAudioMeterComm = null; }
        float rawPeak = Math.Max(peakMulti, peakComm);

        if (_idleSoundVizTimer != null)
        {
            var desiredInterval = (rawPeak > 0.005f || _idleSoundSmoothedPeak > 0.03)
                ? TimeSpan.FromMilliseconds(55)
                : TimeSpan.FromMilliseconds(260);
            if (_idleSoundVizTimer.Interval != desiredInterval)
                _idleSoundVizTimer.Interval = desiredInterval;
        }

        // When completely silent, freeze equalizer bars at resting state so WPF/DWM
        // does not re-render the notch window continuously while idle.
        if (rawPeak < 0.004f && _idleSoundSmoothedPeak < 0.015)
        {
            _idleSoundSmoothedPeak = 0;
            if (!_idleSoundBarsAtRest)
            {
                _idleSoundBarsAtRest = true;
                SetIdleBarScale(IdleSoundBar1Scale, 0.22);
                SetIdleBarScale(IdleSoundBar2Scale, 0.28);
                SetIdleBarScale(IdleSoundBar3Scale, 0.34);
                SetIdleBarScale(IdleSoundBar4Scale, 0.28);
                SetIdleBarScale(IdleSoundBar5Scale, 0.22);
            }
            return;
        }
        _idleSoundBarsAtRest = false;

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
        BatteryPowerSaverService.PowerModeUpdated -= OnBatteryPowerModeUpdated;
        BatteryPowerSaverService.Stop();
        _idleSoundVizTimer?.Stop();
        _idleSoundVizTimer = null;
        ReleaseIdleAudioMeters();
        _idleClockTimer?.Stop();
        _idleClockTimer = null;
        _idleBlinkTimer?.Stop();
        _idleBlinkTimer = null;
        _idleMoodTimer?.Stop();
        _idleMoodTimer = null;
        StopIdleSleepZzz();
        StopIdleWaveHand();
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

        if (!_isExpanded && !_isAnimating && _activeSettingsWindow == null && !_isAppNotificationBannerVisible)
        {
            ProcessMemoryTrimmer.TrimIfAboveBudget(compactGc: false);
        }
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
            _idleRobotMood != IdleRobotMood.Normal ||
            CollapsedContent == null || CollapsedContent.Visibility != Visibility.Visible)
        {
            return;
        }

        int roll = _idleFaceRng.Next(0, 7);
        PlayIdleFaceBlink(doubleBlink: roll == 0, winkLeftOnly: roll == 1);
    }

    private void OnIdleMoodTick(object? sender, EventArgs e)
    {
        if (_idleMoodTimer == null || _cleanedUp) return;

        if (AnimationConfig.ReduceMotion || _isExpanded || _isMusicCompactMode ||
            CollapsedContent == null || CollapsedContent.Visibility != Visibility.Visible)
        {
            if (_idleRobotMood != IdleRobotMood.Normal)
            {
                ApplyIdleRobotMood(IdleRobotMood.Normal);
            }
            _idleMoodTimer.Interval = TimeSpan.FromMilliseconds(2500);
            return;
        }

        if (_isIdleFaceHovered)
        {
            _idleMoodTimer.Interval = TimeSpan.FromMilliseconds(2500);
            return;
        }

        var nextMood = IdleMoodSequence[_idleMoodCycleIndex % IdleMoodSequence.Length];
        _idleMoodCycleIndex++;
        ApplyIdleRobotMood(nextMood);

        _idleMoodTimer.Interval = nextMood switch
        {
            IdleRobotMood.Greet => TimeSpan.FromMilliseconds(3800),
            IdleRobotMood.Smile => TimeSpan.FromMilliseconds(3800),
            IdleRobotMood.Sleep => TimeSpan.FromMilliseconds(9000),
            _ => TimeSpan.FromMilliseconds(4500)
        };
    }

    private void ApplyIdleRobotMood(IdleRobotMood mood)
    {
        if (_idleRobotMood == mood) return;
        _idleRobotMood = mood;

        var fadeDur = TimeSpan.FromMilliseconds(220);

        bool showNormalEyes = mood == IdleRobotMood.Normal;
        bool showHappyEyes = mood is IdleRobotMood.Smile or IdleRobotMood.Greet;
        bool showSleepEyes = mood == IdleRobotMood.Sleep;
        bool showSmileMouth = mood is IdleRobotMood.Smile or IdleRobotMood.Greet;

        AnimateOpacity(IdleEyeLeft, showNormalEyes ? 1.0 : 0.0, fadeDur);
        AnimateOpacity(IdleEyeRight, showNormalEyes ? 1.0 : 0.0, fadeDur);
        AnimateOpacity(IdleHappyEyeLeft, showHappyEyes ? 1.0 : 0.0, fadeDur);
        AnimateOpacity(IdleHappyEyeRight, showHappyEyes ? 1.0 : 0.0, fadeDur);
        AnimateOpacity(IdleSleepEyeLeft, showSleepEyes ? 1.0 : 0.0, fadeDur);
        AnimateOpacity(IdleSleepEyeRight, showSleepEyes ? 1.0 : 0.0, fadeDur);

        AnimateOpacity(IdleFaceMouth, showSmileMouth ? 0.0 : (showSleepEyes ? 0.65 : 1.0), fadeDur);
        AnimateOpacity(IdleSmileMouth, showSmileMouth ? 1.0 : 0.0, fadeDur);

        double cheekOpacity = mood switch
        {
            IdleRobotMood.Greet => 0.85,
            IdleRobotMood.Smile => 0.88,
            IdleRobotMood.Sleep => 0.12,
            _ => 0.28
        };
        AnimateOpacity(IdleCheekLeft, cheekOpacity, fadeDur);
        AnimateOpacity(IdleCheekRight, cheekOpacity, fadeDur);

        if (IdleSmileMouthScale != null)
        {
            double smileScale = showSmileMouth ? 1.15 : 0.85;
            IdleSmileMouthScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(smileScale, fadeDur));
            IdleSmileMouthScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(smileScale, fadeDur));
        }

        if (mood == IdleRobotMood.Greet)
        {
            StopIdleSleepZzz();
            StartIdleBuddyBreathingLoop();
            StartIdleWaveHand();
            PlayHeadTiltKeyframes(
                (0.0, 0), (8.0, 260), (-4.0, 700), (6.5, 1150), (0.0, 1650));
            CenterIdleLookOffset(fadeDur);
        }
        else if (mood == IdleRobotMood.Smile)
        {
            StopIdleSleepZzz();
            StopIdleWaveHand();
            StartIdleBuddyBreathingLoop();
            PlayHeadTiltKeyframes(
                (0.0, 0), (-6.5, 280), (6.5, 760), (-3.0, 1200), (0.0, 1600));
            CenterIdleLookOffset(fadeDur);
        }
        else if (mood == IdleRobotMood.Sleep)
        {
            StopIdleWaveHand();
            StartIdleSleepBreathingLoop();
            StartIdleSleepZzz();
            if (IdleBuddyRotate != null)
            {
                var droop = new DoubleAnimation(-10.0, TimeSpan.FromMilliseconds(600))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                IdleBuddyRotate.BeginAnimation(RotateTransform.AngleProperty, droop);
            }
            if (IdleFaceLookOffset != null)
            {
                IdleFaceLookOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.5, TimeSpan.FromMilliseconds(500)));
                IdleFaceLookOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(1.5, TimeSpan.FromMilliseconds(500)));
            }
        }
        else
        {
            StopIdleSleepZzz();
            StopIdleWaveHand();
            StartIdleBuddyBreathingLoop();
            if (IdleBuddyRotate != null)
            {
                var straighten = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                IdleBuddyRotate.BeginAnimation(RotateTransform.AngleProperty, straighten);
            }
            if (!_isIdleFaceHovered)
            {
                CenterIdleLookOffset(fadeDur);
            }
        }
    }

    private static void AnimateOpacity(UIElement? element, double target, TimeSpan duration)
    {
        if (element == null) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(target, duration));
    }

    private void CenterIdleLookOffset(TimeSpan duration)
    {
        if (IdleFaceLookOffset != null)
        {
            IdleFaceLookOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration));
            IdleFaceLookOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration));
        }
        if (IdlePupilLeftOffset != null && IdlePupilRightOffset != null)
        {
            IdlePupilLeftOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration));
            IdlePupilLeftOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration));
            IdlePupilRightOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration));
            IdlePupilRightOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration));
        }
    }

    private void PlayHeadTiltKeyframes(params (double Angle, int TimeMs)[] frames)
    {
        if (IdleBuddyRotate == null || AnimationConfig.ReduceMotion) return;
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var (angle, ms) in frames)
        {
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        Timeline.SetDesiredFrameRate(anim, 30);
        IdleBuddyRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void StartIdleSleepBreathingLoop()
    {
        if (AnimationConfig.ReduceMotion || IdleBuddyBob == null) return;
        var slowBob = new DoubleAnimation(-0.5, 1.8, TimeSpan.FromMilliseconds(2400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(slowBob, 24);
        IdleBuddyBob.BeginAnimation(TranslateTransform.YProperty, slowBob);
    }

    private void StartIdleWaveHand()
    {
        if (IdleWaveHand == null || IdleWaveHandRotate == null || IdleWaveHandScale == null) return;
        var showDur = TimeSpan.FromMilliseconds(200);
        IdleWaveHand.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, showDur));
        IdleWaveHandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, showDur));
        IdleWaveHandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, showDur));

        var wave = new DoubleAnimation(-26.0, 24.0, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(wave, 30);
        IdleWaveHandRotate.BeginAnimation(RotateTransform.AngleProperty, wave);
    }

    private void StopIdleWaveHand()
    {
        if (IdleWaveHand == null) return;
        var hideDur = TimeSpan.FromMilliseconds(180);
        IdleWaveHand.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, hideDur));
        if (IdleWaveHandScale != null)
        {
            IdleWaveHandScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, hideDur));
            IdleWaveHandScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, hideDur));
        }
        IdleWaveHandRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void StartIdleSleepZzz()
    {
        StartSingleSleepZ(IdleSleepZ1, IdleSleepZ1Offset, beginDelayMs: 0);
        StartSingleSleepZ(IdleSleepZ2, IdleSleepZ2Offset, beginDelayMs: 650);
        StartSingleSleepZ(IdleSleepZ3, IdleSleepZ3Offset, beginDelayMs: 1300);
    }

    private static void StartSingleSleepZ(UIElement? zElement, TranslateTransform? offset, int beginDelayMs)
    {
        if (zElement == null || offset == null) return;

        var opacityAnim = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(beginDelayMs),
            RepeatBehavior = RepeatBehavior.Forever
        };
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1950))));
        Timeline.SetDesiredFrameRate(opacityAnim, 24);
        zElement.BeginAnimation(OpacityProperty, opacityAnim);

        var yAnim = new DoubleAnimation(2.0, -7.5, TimeSpan.FromMilliseconds(1950))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginDelayMs),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        };
        var xAnim = new DoubleAnimation(-1.0, 3.5, TimeSpan.FromMilliseconds(1950))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginDelayMs),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(yAnim, 24);
        Timeline.SetDesiredFrameRate(xAnim, 24);
        offset.BeginAnimation(TranslateTransform.YProperty, yAnim);
        offset.BeginAnimation(TranslateTransform.XProperty, xAnim);
    }

    private void StopIdleSleepZzz()
    {
        StopSingleSleepZ(IdleSleepZ1, IdleSleepZ1Offset);
        StopSingleSleepZ(IdleSleepZ2, IdleSleepZ2Offset);
        StopSingleSleepZ(IdleSleepZ3, IdleSleepZ3Offset);
    }

    private static void StopSingleSleepZ(UIElement? zElement, TranslateTransform? offset)
    {
        if (zElement != null)
        {
            zElement.BeginAnimation(OpacityProperty, null);
            zElement.Opacity = 0.0;
        }
        if (offset != null)
        {
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            offset.X = 0;
            offset.Y = 0;
        }
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

        if (hovered)
        {
            // Wake up immediately if sleeping, and show a friendly smile on hover.
            ApplyIdleRobotMood(IdleRobotMood.Smile);
            return;
        }

        // Return smoothly to normal when pointer leaves, then resume idle mood cycle.
        ApplyIdleRobotMood(IdleRobotMood.Normal);
        if (_idleMoodTimer != null)
        {
            _idleMoodTimer.Interval = TimeSpan.FromMilliseconds(3500);
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
