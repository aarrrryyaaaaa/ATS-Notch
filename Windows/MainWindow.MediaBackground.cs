using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;
    private string? _lastTrackId = null;
    private bool _isFadingTrack = false;
    private int _mediaBackgroundAnimationVersion = 0;
    private int _mediaBackgroundRecoveryVersion = 0;
    private UiScheduledTimer? _mediaBackgroundRecoveryTimer;
    private DateTime _lastMediaBackgroundFadeStartUtc = DateTime.MinValue;
    private BitmapSource? _lastBackgroundThumbnail;

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        bool glass = IsLiquidGlassEnabled;

        if (info == null || (!info.IsAnyMediaPlaying && string.IsNullOrEmpty(info.CurrentTrack)))
        {
            _lastBackgroundThumbnail = null;
            _lastTrackId = null;
            HideMediaBackground();
            return;
        }

        if (info.Thumbnail == null)
        {
            ResetNotchEdgePaletteToDefault(isMusicActive: info.IsPlaying);
            return;
        }

        bool suppressBackdrop = glass || !_settings.ShowMediaArtBackground || !_settings.EnableBlurEffects;

        var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(info.Thumbnail);
        var dominantColor = palette.Main;
        var subColor = palette.Sub;

        UpdateNotchEdgePaletteFromAlbum(dominantColor, subColor, info.IsPlaying);

        if (glass)
        {
            UpdateGlassMediaTint(dominantColor);
        }

        string currentTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
        bool isNewTrack = _lastTrackId != null && _lastTrackId != currentTrackId;
        _lastTrackId = currentTrackId;

        if (isNewTrack && !forceRefresh && !_isFadingTrack && _isExpanded && !suppressBackdrop)
        {
            _isFadingTrack = true;
            FadeToBlackThenUpdate(info);
            return;
        }

        if (!forceRefresh && _hasVolumeHudMediaTint && dominantColor == _lastDominantColor && !isNewTrack &&
            ReferenceEquals(info.Thumbnail, _lastBackgroundThumbnail) &&
            (suppressBackdrop || !_isExpanded || MediaBackground.Opacity > 0.49)) return;

        _lastDominantColor = dominantColor;
        _lastBackgroundThumbnail = info.Thumbnail;

        if (!suppressBackdrop)
        {
            if (!_isExpanded)
            {
                if (isNewTrack)
                {
                    ClearBlurredBackgroundSources();
                    _suppressNextBlurDissolve = true;
                }
            }
            else
            {
                UpdateBlurredBackgroundAsync(info.Thumbnail, allowInterimThumbnail: forceRefresh || isNewTrack).SafeFireAndForget("MEDIA-BG-BLUR");
            }
        }

        if (suppressBackdrop || !_isExpanded)
            HideMediaBackgroundOverlay();

        double brightnessDimOpacity = suppressBackdrop ? 0 : DynamicIslandColorExtractor.GetBrightnessDimOverlay(info.Thumbnail);
        var dimOverlayAnim = new DoubleAnimation
        {
            To = brightnessDimOpacity,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = _easeQuadOut
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(dimOverlayAnim, VNotch.Services.AnimationConfig.TargetFps);
        BrightnessDimOverlay.BeginAnimation(OpacityProperty, dimOverlayAnim);
        BrightnessDimOverlay2.BeginAnimation(OpacityProperty, dimOverlayAnim);

        var liftedSub = LiftDarkColor(subColor);
        var vibrantTargetColor = Color.FromRgb(liftedSub.R, liftedSub.G, liftedSub.B);
        double dominantLuminance = (0.2126 * dominantColor.R + 0.7152 * dominantColor.G + 0.0722 * dominantColor.B) / 255.0;

        var uiColorAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        var iconInkAnim = uiColorAnim.Clone();
        iconInkAnim.To = UiPalette.PrimaryColor;
        Timeline.SetDesiredFrameRate(iconInkAnim, AnimationConfig.TargetFps);

        double targetOpacity = 0;
        if (!suppressBackdrop && _isExpanded && (!_isAnimating || forceRefresh))
        {
            targetOpacity = DynamicIslandColorExtractor.GetAdaptiveBlurOpacity(dominantLuminance, _settings.MediaBlurBrightnessBoost);
        }
        if (targetOpacity > 0 && dominantLuminance < 0.25)
        {
            double darknessBoost = 1.0 + (0.25 - dominantLuminance) * 1.4;
            targetOpacity = Math.Min(targetOpacity * darknessBoost, 0.95);
        }
        int animationVersion = ++_mediaBackgroundAnimationVersion;

        var opacityAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        if (targetOpacity > 0)
        {
            MediaBackground.Visibility = Visibility.Visible;
            MediaBackground2.Visibility = Visibility.Visible;
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                if (animationVersion != _mediaBackgroundAnimationVersion)
                {
                    return;
                }

                if (MediaBackground.Opacity <= 0.001 && (!_isExpanded || _isAnimating))
                {
                    MediaBackground.Visibility = Visibility.Collapsed;
                    MediaBackground2.Visibility = Visibility.Collapsed;
                }
            };
        }

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);

        var ipb = EnsureUnfrozenBrush(MediaPlayback.IndeterminateProgress.Background, VNotch.Services.UiPalette.PrimaryColor);
        MediaPlayback.IndeterminateProgress.Background = ipb;
        var ctf = EnsureUnfrozenBrush(MediaPlayback.CurrentTimeText.Foreground, UiPalette.SecondaryColor);
        MediaPlayback.CurrentTimeText.Foreground = ctf;
        var rtf = EnsureUnfrozenBrush(MediaPlayback.RemainingTimeText.Foreground, UiPalette.SecondaryColor);
        MediaPlayback.RemainingTimeText.Foreground = rtf;
        var cmt = EnsureUnfrozenBrush(CompactTitleMarquee.Foreground, VNotch.Services.UiPalette.PrimaryColor);
        CompactTitleMarquee.Foreground = cmt;

        var progressDarkColor = Color.FromArgb(
            vibrantTargetColor.A,
            (byte)(vibrantTargetColor.R * 0.65),
            (byte)(vibrantTargetColor.G * 0.65),
            (byte)(vibrantTargetColor.B * 0.65));

        var darkColorAnim = new ColorAnimation
        {
            To = progressDarkColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(uiColorAnim, VNotch.Services.AnimationConfig.TargetFps);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(darkColorAnim, VNotch.Services.AnimationConfig.TargetFps);

        MediaPlayback.ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, uiColorAnim);
        MediaPlayback.ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, darkColorAnim);

        ipb.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        ctf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        rtf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        cmt.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        AnimateTitleGradient(vibrantTargetColor);

        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        var volIconBrush = EnsureUnfrozenBrush(VolumeIcon.Foreground, UiPalette.TertiaryColor);
        VolumeIcon.Foreground = volIconBrush;
        volIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, iconInkAnim);

        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, uiColorAnim);
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, darkColorAnim);
        SetVolumeHudMediaTint(vibrantTargetColor, true);

        AnimateShapeFill(InlinePrevArrow0, iconInkAnim);
        AnimateShapeFill(InlinePrevArrow1, iconInkAnim);
        AnimateShapeFill(InlinePrevArrow2, iconInkAnim);
        AnimateShapeFill(InlinePauseIconPath, iconInkAnim);
        AnimateShapeFill(InlinePlayIconPath, iconInkAnim);
        AnimateShapeFill(InlineNextArrow0, iconInkAnim);
        AnimateShapeFill(InlineNextArrow1, iconInkAnim);
        AnimateShapeFill(InlineNextArrow2, iconInkAnim);
    }

    private static SolidColorBrush EnsureUnfrozenBrush(Brush? brush, Color fallback)
    {
        if (brush is SolidColorBrush sb)
        {
            return sb.IsFrozen ? new SolidColorBrush(sb.Color) : sb;
        }
        return new SolidColorBrush(fallback);
    }

    private static void AnimateShapeFill(System.Windows.Shapes.Shape shape, ColorAnimation anim)
    {
        var fill = EnsureUnfrozenBrush(shape.Fill, VNotch.Services.UiPalette.PrimaryColor);
        shape.Fill = fill;
        fill.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static readonly CubicEase BlurTrackFadeOutEase = CreateBlurTrackFadeOutEase();

    private static CubicEase CreateBlurTrackFadeOutEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        ease.Freeze();
        return ease;
    }

    private static Color LiftDarkColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double v = Math.Max(r, Math.Max(g, b));

        if (v >= 0.50) return c;

        const double targetV = 0.55;

        double scale = targetV / Math.Max(v, 0.01);
        byte newR = (byte)Math.Clamp(c.R * scale, 0, 255);
        byte newG = (byte)Math.Clamp(c.G * scale, 0, 255);
        byte newB = (byte)Math.Clamp(c.B * scale, 0, 255);

        return Color.FromRgb(newR, newG, newB);
    }

    private void FadeToBlackThenUpdate(MediaInfo info)
    {
        _suppressNextBlurDissolve = false;

        int version = ++_blurCrossfadeVersion;
        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        var activeImg2 = _blurBackIsActive ? MediaBackgroundImageBack2 : MediaBackgroundImage2;

        double currentOpacity = activeImg.Opacity;
        activeImg.BeginAnimation(OpacityProperty, null);
        activeImg2.BeginAnimation(OpacityProperty, null);
        activeImg.Opacity = currentOpacity;
        activeImg2.Opacity = currentOpacity;

        var fadeOut = new DoubleAnimation
        {
            From = currentOpacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = BlurTrackFadeOutEase
        };
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (_, _) =>
        {
            _isFadingTrack = false;
            if (version != _blurCrossfadeVersion) return;
            activeImg.BeginAnimation(OpacityProperty, null);
            activeImg2.BeginAnimation(OpacityProperty, null);
            activeImg.Opacity = 0.0;
            activeImg2.Opacity = 0.0;
            activeImg.Source = null;
            activeImg2.Source = null;
            _lastBlurThumbnailRef = null;
            UpdateMediaBackground(info, forceRefresh: true);
        };

        activeImg.BeginAnimation(OpacityProperty, fadeOut);
        activeImg2.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void HideMediaBackground()
    {
        ClearGlassMediaTint();
        HideMediaBackgroundOverlay();
        ResetNotchEdgePaletteToDefault(isMusicActive: false);

        if (_isTimerView || _isAudioView || _isSecondaryView || _isCameraView || _isPrayerView || _isAiUsageView) return;
        _lastDominantColor = Colors.Transparent;

        var defaultColorAnim = new ColorAnimation
        {
            To = VNotch.Services.UiPalette.PrimaryColor,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        var defaultTextAnim = new ColorAnimation
        {
            To = UiPalette.SecondaryColor,
            Duration = TimeSpan.FromMilliseconds(400)
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultColorAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaPlayback.ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultGradientEndAnim = new ColorAnimation
        {
            To = UiPalette.SecondaryColor,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultGradientEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaPlayback.ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultGradientEndAnim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultTextAnim, VNotch.Services.AnimationConfig.TargetFps);
        if (MediaPlayback.IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen) ipb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (MediaPlayback.CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (MediaPlayback.RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        var defaultTitleAnim = defaultColorAnim.Clone();
        defaultTitleAnim.To = VNotch.Services.UiPalette.PrimaryColor;
        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen) cmt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTitleAnim);

        ResetTitleGradientToWhite();

        var defaultIconAnim = defaultTextAnim.Clone();
        defaultIconAnim.To = UiPalette.PrimaryColor;
        if (VolumeIcon.Foreground is SolidColorBrush volIco && !volIco.IsFrozen) volIco.BeginAnimation(SolidColorBrush.ColorProperty, defaultIconAnim);
        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultVolEndAnim = new ColorAnimation
        {
            To = UiPalette.SecondaryColor,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultVolEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultVolEndAnim);
        SetVolumeHudMediaTint(Colors.White, false);


        void ResetUnfrozenFill(System.Windows.Shapes.Shape shape)
        {
            if (shape.Fill is SolidColorBrush brush && !brush.IsFrozen)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, defaultIconAnim);
        }

        ResetUnfrozenFill(InlinePrevArrow0);
        ResetUnfrozenFill(InlinePrevArrow1);
        ResetUnfrozenFill(InlinePrevArrow2);
        ResetUnfrozenFill(InlinePauseIconPath);
        ResetUnfrozenFill(InlinePlayIconPath);
        ResetUnfrozenFill(InlineNextArrow0);
        ResetUnfrozenFill(InlineNextArrow1);
        ResetUnfrozenFill(InlineNextArrow2);
    }

    private void HideMediaBackgroundOverlay()
    {
        if (MediaBackground.Opacity == 0 && MediaBackground.Visibility != Visibility.Visible)
        {
            if (_currentMediaInfo?.IsAnyMediaPlaying != true &&
                (MediaBackgroundImage.Source != null || MediaBackgroundImageBack.Source != null || _lastBlurThumbnailRef != null))
            {
                ClearBlurredBackgroundSources();
            }
            return;
        }


        int animationVersion = ++_mediaBackgroundAnimationVersion;
        var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = _easePowerIn2
        };

        opacityAnim.Completed += (s, e) =>
        {
            if (animationVersion != _mediaBackgroundAnimationVersion)
            {
                return;
            }

            if (MediaBackground.Opacity <= 0.001)
            {
                MediaBackground.Visibility = Visibility.Collapsed;
                MediaBackground2.Visibility = Visibility.Collapsed;
                if (_currentMediaInfo?.IsAnyMediaPlaying != true)
                {
                    ClearBlurredBackgroundSources();
                }
                if (_currentMediaInfo == null || !_currentMediaInfo.IsAnyMediaPlaying)
                {
                    _lastBackgroundThumbnail = null;
                }
            }
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void ShowMediaBackground()
    {
        if (!_isExpanded || _isAnimating || _currentMediaInfo == null) return;

        // Re-derive accent colors after view switches in Liquid Glass mode to
        // restore music brushes from fallback defaults.
        if (IsLiquidGlassEnabled)
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
            return;
        }

        if (!_settings.ShowMediaArtBackground) return;
        if (!_settings.EnableBlurEffects) return;

        if (_currentMediaInfo.Thumbnail != null && !ReferenceEquals(_currentMediaInfo.Thumbnail, _lastBlurThumbnailRef))
        {
            ClearBlurredBackgroundSources();
            _suppressNextBlurDissolve = true;
        }

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        _lastMediaBackgroundFadeStartUtc = DateTime.UtcNow;
        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        ScheduleMediaBackgroundRecovery();
    }

    private void ScheduleMediaBackgroundRecovery()
    {
        int recoveryVersion = Interlocked.Increment(ref _mediaBackgroundRecoveryVersion);

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            EnsureMediaBackgroundVisible(recoveryVersion);
        }));

        _mediaBackgroundRecoveryTimer?.Stop();
        _mediaBackgroundRecoveryTimer?.Dispose();
        var recoveryTimer = new UiScheduledTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _mediaBackgroundRecoveryTimer = recoveryTimer;

        recoveryTimer.Tick += (s, e) =>
        {
            recoveryTimer.Stop();
            recoveryTimer.Dispose();
            if (ReferenceEquals(_mediaBackgroundRecoveryTimer, recoveryTimer))
                _mediaBackgroundRecoveryTimer = null;
            if (_cleanedUp) return;
            EnsureMediaBackgroundVisible(recoveryVersion);
        };

        recoveryTimer.Start();
    }

    private void EnsureMediaBackgroundVisible(int recoveryVersion)
    {
        if (IsLiquidGlassEnabled || !_settings.ShowMediaArtBackground)
        {
            return;
        }

        if (!_settings.EnableBlurEffects)
        {
            return;
        }

        if (recoveryVersion != _mediaBackgroundRecoveryVersion)
        {
            return;
        }

        if (!_isExpanded || _isAnimating || _currentMediaInfo?.Thumbnail == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            return;
        }

        bool fadeInProgress = (DateTime.UtcNow - _lastMediaBackgroundFadeStartUtc).TotalMilliseconds < 600;

        bool visibilityFailed =
            MediaBackground.Visibility != Visibility.Visible ||
            MediaBackground2.Visibility != Visibility.Visible;

        bool opacityFailed = !fadeInProgress &&
            (MediaBackground.Opacity < 0.05 || MediaBackground2.Opacity < 0.05);

        if (!visibilityFailed && !opacityFailed)
        {
            return;
        }

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        _lastMediaBackgroundFadeStartUtc = DateTime.UtcNow;
        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
    }

    private const string BlurCrossfadeLogTag = "BLUR-CROSSFADE";
    private int _blurCrossfadeVersion = 0;
    private int _blurTaskVersion = 0;
    private BitmapSource? _lastBlurThumbnailRef;
    private bool _suppressNextBlurDissolve = false;
    private UiScheduledTimer? _blurDissolveDebounce;
    private BitmapSource? _pendingBlurResult;

    private sealed record BlurRequest(BitmapSource Thumbnail, bool AllowInterimThumbnail, int Version);
    private BlurRequest? _pendingBlurRequest;
    private bool _isBlurWorkerRunning = false;
    private readonly object _blurWorkerLock = new();

    private async Task UpdateBlurredBackgroundAsync(BitmapSource thumbnail, bool allowInterimThumbnail = false)
    {
        try
        {
            if (_cleanedUp || IsLiquidGlassEnabled || !_settings.ShowMediaArtBackground || !_settings.EnableBlurEffects)
            {
                return;
            }

            if (ReferenceEquals(thumbnail, _lastBlurThumbnailRef))
            {
                return;
            }

            bool hasExistingBlur = MediaBackgroundImage.Source != null || MediaBackgroundImageBack.Source != null;
            if (!allowInterimThumbnail && hasExistingBlur && thumbnail.PixelWidth < 200 && thumbnail.PixelHeight < 200)
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"SKIP-SMALL thumb={thumbnail.PixelWidth}x{thumbnail.PixelHeight} (waiting for better)");
                return;
            }

            _lastBlurThumbnailRef = thumbnail;

            int taskVersion = ++_blurTaskVersion;

            lock (_blurWorkerLock)
            {
                _pendingBlurRequest = new BlurRequest(thumbnail, allowInterimThumbnail, taskVersion);
                if (_isBlurWorkerRunning)
                {
                    return;
                }
                _isBlurWorkerRunning = true;
            }

            await RunBlurWorkerLoopAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-BG-BLUR", ex.ToString());
        }
    }

    private async Task RunBlurWorkerLoopAsync()
    {
        while (true)
        {
            BlurRequest? req;
            lock (_blurWorkerLock)
            {
                req = _pendingBlurRequest;
                _pendingBlurRequest = null;
            }

            if (req == null)
            {
                lock (_blurWorkerLock)
                {
                    if (_pendingBlurRequest == null)
                    {
                        _isBlurWorkerRunning = false;
                        break;
                    }
                    continue;
                }
            }

            if (req.Version != _blurTaskVersion)
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale before start) taskVer={req.Version} current={_blurTaskVersion}");
                continue;
            }

            await ProcessBlurRequestAsync(req);
        }
    }

    private async Task ProcessBlurRequestAsync(BlurRequest req)
    {
        if (!CanProcessBlurRequest(req)) return;
        RuntimeLog.Log(BlurCrossfadeLogTag, $"START taskVer={req.Version} thumb={req.Thumbnail.PixelWidth}x{req.Thumbnail.PixelHeight} subjectBlur={_settings.EnableSubjectBlur}");

        BitmapSource? blurredImage = null;

        if (_settings.EnableSubjectBlur)
        {
            if (!CanProcessBlurRequest(req))
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale before inference) taskVer={req.Version} current={_blurTaskVersion}");
                return;
            }

            SubjectBounds? subject = null;
            try
            {
                subject = await Task.Run(() => _mediaService.ArtworkService.GetDominantSubjectBounds(req.Thumbnail), CancellationToken.None);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-BG-SUBJECT", ex.ToString());
            }

            if (!CanProcessBlurRequest(req))
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale after subject / before blur) taskVer={req.Version} current={_blurTaskVersion}");
                return;
            }

            RuntimeLog.Log(BlurCrossfadeLogTag, $"subject={subject?.CenterX:F2},{subject?.CenterY:F2} w={subject?.Width:F2} h={subject?.Height:F2}");

            blurredImage = await SubjectAwareBlurService.GetSubjectBlurredAsync(req.Thumbnail, subject);
        }
        else
        {
            if (!CanProcessBlurRequest(req))
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale before blur) taskVer={req.Version} current={_blurTaskVersion}");
                return;
            }

            blurredImage = await FastBlurService.GetBlurredImageAsync(req.Thumbnail);
        }

        if (!CanProcessBlurRequest(req))
        {
            RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale after blur) taskVer={req.Version} current={_blurTaskVersion}");
            return;
        }

        if (blurredImage == null) return;

        RuntimeLog.Log(BlurCrossfadeLogTag, $"RESULT taskVer={req.Version} blurSize={blurredImage.PixelWidth}x{blurredImage.PixelHeight} suppress={_suppressNextBlurDissolve} frontNull={MediaBackgroundImage.Source == null} backNull={MediaBackgroundImageBack.Source == null}");

        if (_suppressNextBlurDissolve || !_isExpanded || (MediaBackgroundImage.Source == null && MediaBackgroundImageBack.Source == null))
        {
            _suppressNextBlurDissolve = false;
            _pendingBlurResult = null;
            _blurDissolveDebounce?.Stop();
            RuntimeLog.Log(BlurCrossfadeLogTag, $"APPLY-IMMEDIATE taskVer={req.Version}");
            ApplyBlurredBackgroundImmediate(blurredImage);
            return;
        }

        RuntimeLog.Log(BlurCrossfadeLogTag, $"SCHEDULE-DISSOLVE taskVer={req.Version}");
        ScheduleBlurredBackgroundDissolve(blurredImage);
    }

    private bool CanProcessBlurRequest(BlurRequest req)
    {
        if (req.Version != _blurTaskVersion) return false;
        if (!_cleanedUp && !IsLiquidGlassEnabled &&
            _settings.ShowMediaArtBackground && _settings.EnableBlurEffects) return true;
        // Allow the same artwork to be requested again on the next expansion.
        _lastBlurThumbnailRef = null;
        return false;
    }

    private void ClearBlurredBackgroundSources()
    {
        ++_blurCrossfadeVersion;
        _pendingBlurResult = null;
        _blurDissolveDebounce?.Stop();
        _lastBlurThumbnailRef = null;

        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

        _blurBackIsActive = false;
        MediaBackgroundImage.Source = null;
        MediaBackgroundImage2.Source = null;
        MediaBackgroundImage.Opacity = 0.0;
        MediaBackgroundImage2.Opacity = 0.0;
        MediaBackgroundImageBack.Source = null;
        MediaBackgroundImageBack2.Source = null;
        MediaBackgroundImageBack.Opacity = 0.0;
        MediaBackgroundImageBack2.Opacity = 0.0;
    }

    private void ApplyBlurredBackgroundImmediate(BitmapSource blurred)
    {
        ++_blurCrossfadeVersion;
        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

        _blurBackIsActive = false;
        MediaBackgroundImage.Source = blurred;
        MediaBackgroundImage2.Source = blurred;
        MediaBackgroundImage.Opacity = 1.0;
        MediaBackgroundImage2.Opacity = 1.0;
        MediaBackgroundImageBack.Source = null;
        MediaBackgroundImageBack2.Source = null;
        MediaBackgroundImageBack.Opacity = 0.0;
        MediaBackgroundImageBack2.Opacity = 0.0;
    }

    private void ScheduleBlurredBackgroundDissolve(BitmapSource blurred)
    {
        _pendingBlurResult = blurred;

        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        if (activeImg.Opacity < 0.05 || activeImg.Source == null)
        {
            _pendingBlurResult = null;
            _blurDissolveDebounce?.Stop();
            RuntimeLog.Log(BlurCrossfadeLogTag, $"CROSSFADE-START (immediate, old gone) blurSize={blurred.PixelWidth}x{blurred.PixelHeight} backIsActive={_blurBackIsActive}");
            CrossfadeBlurredBackground(blurred);
            return;
        }

        if (_blurDissolveDebounce == null)
        {
            _blurDissolveDebounce = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _blurDissolveDebounce.Tick += (_, _) =>
            {
                _blurDissolveDebounce.Stop();
                var pending = _pendingBlurResult;
                _pendingBlurResult = null;
                if (pending != null)
                {
                    RuntimeLog.Log(BlurCrossfadeLogTag, $"CROSSFADE-START blurSize={pending.PixelWidth}x{pending.PixelHeight} backIsActive={_blurBackIsActive}");
                    CrossfadeBlurredBackground(pending);
                }
            };
        }

        _blurDissolveDebounce.Stop();
        _blurDissolveDebounce.Start();
    }

    private bool _blurBackIsActive = false;

    private void CrossfadeBlurredBackground(BitmapSource blurred)
    {
        int version = ++_blurCrossfadeVersion;

        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        var activeImg2 = _blurBackIsActive ? MediaBackgroundImageBack2 : MediaBackgroundImage2;
        var targetImg = _blurBackIsActive ? MediaBackgroundImage : MediaBackgroundImageBack;
        var targetImg2 = _blurBackIsActive ? MediaBackgroundImage2 : MediaBackgroundImageBack2;

        double activeOpacity = activeImg.Opacity;

        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

        activeImg.Opacity = activeOpacity;
        activeImg2.Opacity = activeOpacity;
        targetImg.Opacity = 0.0;
        targetImg2.Opacity = 0.0;

        targetImg.Source = blurred;
        targetImg2.Source = blurred;

        bool oldAlreadyGone = activeOpacity < 0.05 || activeImg.Source == null;
        var dur = oldAlreadyGone ? TimeSpan.FromMilliseconds(380) : TimeSpan.FromMilliseconds(550);
        var ease = new CubicEase { EasingMode = oldAlreadyGone ? EasingMode.EaseOut : EasingMode.EaseInOut };

        var fadeIn = new DoubleAnimation { From = 0.0, To = 1.0, Duration = dur, EasingFunction = ease };
        var fadeOut = new DoubleAnimation { From = activeOpacity, To = 0.0, Duration = dur, EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeIn.Completed += (_, _) =>
        {
            if (version != _blurCrossfadeVersion) return;

            activeImg.Opacity = 0.0;
            activeImg2.Opacity = 0.0;
            targetImg.Opacity = 1.0;
            targetImg2.Opacity = 1.0;

            MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

            activeImg.Source = null;
            activeImg2.Source = null;
        };

        _blurBackIsActive = !_blurBackIsActive;

        targetImg.BeginAnimation(OpacityProperty, fadeIn);
        targetImg2.BeginAnimation(OpacityProperty, fadeIn);
        activeImg.BeginAnimation(OpacityProperty, fadeOut);
        activeImg2.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Title Gradient Animation

    private void AnimateTitleGradient(Color? vibrantColor = null)
    {
        Color titleColor = UiPalette.PrimaryColor;
        Color artistColor = UiPalette.SecondaryColor;
        if (vibrantColor is Color accent)
        {
            const double tintStrength = 0.15;
            titleColor = Color.FromRgb(
                (byte)(255 - (255 - accent.R) * tintStrength),
                (byte)(255 - (255 - accent.G) * tintStrength),
                (byte)(255 - (255 - accent.B) * tintStrength));
            artistColor = Color.FromArgb(191, titleColor.R, titleColor.G, titleColor.B);
        }

        var colorAnim = new ColorAnimation { To = titleColor, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        var artistColorAnim = new ColorAnimation { To = artistColor, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        var iconAnim = new ColorAnimation { To = titleColor, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        Timeline.SetDesiredFrameRate(colorAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(artistColorAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(iconAnim, AnimationConfig.TargetFps);

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            foreach (var stop in titleBrush.GradientStops)
            {
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
            }
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            foreach (var stop in titleNextBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
        }

        AnimateForegroundColor(MediaPlayback.TrackArtist, artistColorAnim);
        AnimateForegroundColor(MediaPlayback.TrackArtistNext, artistColorAnim);

        AnimateForegroundColor(LyricTextA, artistColorAnim);
        AnimateForegroundColor(LyricTextB, artistColorAnim);

        AnimatePathFillAndStroke(MediaPlayback.PrevArrow0, iconAnim);
        AnimatePathFillAndStroke(MediaPlayback.PrevArrow1, iconAnim);
        AnimatePathFillAndStroke(MediaPlayback.PrevArrow2, iconAnim);
        AnimatePathFillAndStroke(MediaPlayback.NextArrow0, iconAnim);
        AnimatePathFillAndStroke(MediaPlayback.NextArrow1, iconAnim);
        AnimatePathFillAndStroke(MediaPlayback.NextArrow2, iconAnim);
        AnimatePathFill(MediaPlayback.PauseIconPath, iconAnim);
        AnimatePathFill(MediaPlayback.PlayIconPath, iconAnim);
    }

    private static void AnimateForegroundColor(System.Windows.Controls.TextBlock tb, ColorAnimation anim)
    {
        if (tb.Foreground is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(tb.Foreground is SolidColorBrush sb ? sb.Color : VNotch.Services.UiPalette.PrimaryColor);
            tb.Foreground = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFill(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        if (path.Fill is SolidColorBrush fillBrush && !fillBrush.IsFrozen)
        {
            fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(path.Fill is SolidColorBrush sb ? sb.Color : VNotch.Services.UiPalette.PrimaryColor);
            path.Fill = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFillAndStroke(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        AnimatePathFill(path, anim);
        if (path.Stroke is SolidColorBrush strokeBrush && !strokeBrush.IsFrozen)
        {
            strokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else if (path.Stroke != null)
        {
            var newBrush = new SolidColorBrush(path.Stroke is SolidColorBrush sb ? sb.Color : VNotch.Services.UiPalette.PrimaryColor);
            path.Stroke = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private void StopTitleGradientShift()
    {
        StopGradientAnimations("TrackTitleGradient");
        StopGradientAnimations("TrackTitleNextGradient");
        StopForegroundAnimation(MediaPlayback.TrackArtist);
        StopForegroundAnimation(MediaPlayback.TrackArtistNext);
        StopForegroundAnimation(LyricTextA);
        StopForegroundAnimation(LyricTextB);
        StopPathAnimations(MediaPlayback.PrevArrow0);
        StopPathAnimations(MediaPlayback.PrevArrow1);
        StopPathAnimations(MediaPlayback.PrevArrow2);
        StopPathAnimations(MediaPlayback.NextArrow0);
        StopPathAnimations(MediaPlayback.NextArrow1);
        StopPathAnimations(MediaPlayback.NextArrow2);
        StopPathAnimations(MediaPlayback.PauseIconPath);
        StopPathAnimations(MediaPlayback.PlayIconPath);
    }

    private void StopGradientAnimations(string resourceKey)
    {
        if (Resources[resourceKey] is not LinearGradientBrush brush) return;

        foreach (var stop in brush.GradientStops)
            stop.BeginAnimation(GradientStop.ColorProperty, null);
    }

    private static void StopForegroundAnimation(System.Windows.Controls.TextBlock textBlock)
    {
        if (textBlock.Foreground is SolidColorBrush brush && !brush.IsFrozen)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
    }

    private static void StopPathAnimations(System.Windows.Shapes.Path path)
    {
        if (path.Fill is SolidColorBrush fill && !fill.IsFrozen)
            fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (path.Stroke is SolidColorBrush stroke && !stroke.IsFrozen)
            stroke.BeginAnimation(SolidColorBrush.ColorProperty, null);
    }

    private void ResetTitleGradientToWhite()
    {
        StopTitleGradientShift();
        AnimateTitleGradient();
    }

    #region Notch Edge Gradient & Prayer Palette

    private Color _edgePaletteLeft = Controls.PrayerNotchView.DefaultAccentLeft;
    private Color _edgePaletteMid = Controls.PrayerNotchView.DefaultAccentMid;
    private Color _edgePaletteRight = Controls.PrayerNotchView.DefaultAccentRight;
    private bool _edgeHasMusicColor;
    private bool _edgeIsMusicPlaying;
    private bool _isNotchEdgeHovered;

    internal (Color Left, Color Mid, Color Right) ActiveNotchAccentPalette =>
        (_edgePaletteLeft, _edgePaletteMid, _edgePaletteRight);

    private void UpdateNotchEdgePaletteFromAlbum(Color main, Color sub, bool isPlaying)
    {
        Color left = LiftEdgeVibrantColor(sub);
        Color right = LiftEdgeVibrantColor(main);

        int diff = Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
        if (diff < 65)
        {
            right = ShiftColorHue(right, 34.0);
        }
        Color mid = BlendColors(left, right, 0.5);

        bool changed = !_edgeHasMusicColor || _edgePaletteLeft != left || _edgePaletteRight != right;
        _edgeHasMusicColor = true;
        _edgeIsMusicPlaying = isPlaying;
        _edgePaletteLeft = left;
        _edgePaletteMid = mid;
        _edgePaletteRight = right;

        if (changed && !(_isExpanded && _isPrayerView) && !_isPrayerReminderVisible)
        {
            AnimateNotchEdgeStops(left, mid, right);
        }
        UpdateNotchEdgeOpacity();
    }

    private void ResetNotchEdgePaletteToDefault(bool isMusicActive)
    {
        Color left = Controls.PrayerNotchView.DefaultAccentLeft;
        Color mid = Controls.PrayerNotchView.DefaultAccentMid;
        Color right = Controls.PrayerNotchView.DefaultAccentRight;

        bool changed = _edgeHasMusicColor || _edgePaletteLeft != left || _edgePaletteRight != right;
        _edgeHasMusicColor = isMusicActive;
        _edgeIsMusicPlaying = isMusicActive;
        _edgePaletteLeft = left;
        _edgePaletteMid = mid;
        _edgePaletteRight = right;

        if (changed)
        {
            AnimateNotchEdgeStops(left, mid, right);
        }
        UpdateNotchEdgeOpacity();
    }

    private void AnimateNotchEdgeStops(Color left, Color mid, Color right)
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(450));
        AnimateStopColor(EdgeAmbientStopLeft, left, dur);
        AnimateStopColor(EdgeAmbientStopMid, mid, dur);
        AnimateStopColor(EdgeAmbientStopRight, right, dur);
    }

    internal void UpdateNotchEdgeOpacity()
    {
        if (EdgeAmbientGlow == null) return;
        bool glass = IsLiquidGlassEnabled;

        // Prayer tab and prayer arrival reminder always stay in Nusa non-green aurora colors.
        if ((_isExpanded && _isPrayerView) || _isPrayerReminderVisible)
        {
            AnimateNotchEdgeStops(
                Controls.PrayerNotchView.DefaultAccentLeft,
                Controls.PrayerNotchView.DefaultAccentMid,
                Controls.PrayerNotchView.DefaultAccentRight);
        }
        else if (_edgeHasMusicColor)
        {
            AnimateNotchEdgeStops(_edgePaletteLeft, _edgePaletteMid, _edgePaletteRight);
        }
        else
        {
            AnimateNotchEdgeStops(
                Controls.PrayerNotchView.DefaultAccentLeft,
                Controls.PrayerNotchView.DefaultAccentMid,
                Controls.PrayerNotchView.DefaultAccentRight);
        }

        double targetAmbient;
        if (_isPrayerReminderVisible)
        {
            targetAmbient = glass ? 0.36 : 0.60;
        }
        else if (!_isExpanded && !_isNotchEdgeHovered)
        {
            double idleBoost = _edgeIsMusicPlaying ? 0.10 : 0.0;
            targetAmbient = (glass ? 0.18 : 0.34) + idleBoost;
        }
        else if (_isExpanded)
        {
            double boost = (_edgeIsMusicPlaying && !_isPrayerView) ? 0.10 : 0.0;
            targetAmbient = (glass ? 0.24 : 0.44) + boost;
        }
        else
        {
            double boost = _edgeIsMusicPlaying ? 0.08 : 0.0;
            targetAmbient = (glass ? 0.30 : 0.50) + boost;
        }

        if (AnimationConfig.ReduceMotion)
        {
            EdgeAmbientGlow.BeginAnimation(OpacityProperty, null);
            EdgeAmbientGlow.Opacity = targetAmbient;
            return;
        }

        var animAmb = new DoubleAnimation(targetAmbient, TimeSpan.FromMilliseconds(260)) { EasingFunction = _easeQuadOut };
        Timeline.SetDesiredFrameRate(animAmb, AnimationConfig.TargetFps);
        EdgeAmbientGlow.BeginAnimation(OpacityProperty, animAmb);
    }

    private static void AnimateStopColor(GradientStop? stop, Color target, Duration duration)
    {
        if (stop == null) return;
        if (AnimationConfig.ReduceMotion)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = target;
            return;
        }
        var anim = new ColorAnimation(target, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    private static Color LiftEdgeVibrantColor(Color c)
    {
        RgbToHsv(c, out double h, out double s, out double v);
        if (s < 0.22) s = 0.55;
        else s = Math.Min(1.0, s * 1.18);
        if (v < 0.62) v = 0.78;
        else if (v > 0.96 && s > 0.2) v = 0.92;
        return HsvToRgb(h, s, v);
    }

    private static Color ShiftColorHue(Color c, double degrees)
    {
        RgbToHsv(c, out double h, out double s, out double v);
        h = (h + degrees) % 360.0;
        if (h < 0) h += 360.0;
        return HsvToRgb(h, Math.Max(s, 0.58), Math.Max(v, 0.72));
    }

    private static Color BlendColors(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
        (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
        (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        v = max;
        s = max <= 0.0001 ? 0 : delta / max;
        if (delta <= 0.0001) { h = 210.0; return; }
        if (max == r) h = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g) h = 60.0 * ((b - r) / delta + 2.0);
        else h = 60.0 * ((r - g) / delta + 4.0);
        if (h < 0) h += 360.0;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255.0, 0, 255),
            (byte)Math.Clamp((g + m) * 255.0, 0, 255),
            (byte)Math.Clamp((b + m) * 255.0, 0, 255));
    }

    #endregion

    #endregion
}
