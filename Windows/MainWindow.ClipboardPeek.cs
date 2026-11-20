using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Clipboard Peek

    private bool _isClipboardPeekActive = false;
    private bool _isCompactSystemPillActive = false;
    private int _clipboardPeekToken = 0;
    private int _compactSystemPillToken = 0;
    private int _compactSystemPillGen = 0;
    private UiScheduledTimer? _clipboardRevertTimer;
    private UiScheduledTimer? _compactSystemPillTimer;

    internal enum CompactSystemKind
    {
        Copy,
        LockOn,
        LockOff,
        FnKey,
        Screenshot,
        ChargerOn,
        ChargerOff
    }

    private void PlayClipboardPeek()
    {
        if (!_isExpanded && !_isAnimating)
        {
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var peakTime = TimeSpan.FromMilliseconds(100);
            var endTime = TimeSpan.FromMilliseconds(500);

            var bounceX = new DoubleAnimationUsingKeyFrames();
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.06,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceX, VNotch.Services.AnimationConfig.TargetFps);

            var bounceY = new DoubleAnimationUsingKeyFrames();
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.95,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceY, VNotch.Services.AnimationConfig.TargetFps);

            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        }

        if (!_isExpanded && !_isAnimating && !_isAppNotificationBannerVisible)
        {
            string copyLabel = "Teks Disalin";
            var kind = CompactSystemKind.Copy;
            try
            {
                if (System.Windows.Clipboard.ContainsImage())
                {
                    copyLabel = "Screenshot";
                    kind = CompactSystemKind.Screenshot;
                }
                else if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    copyLabel = "File Disalin";
                }
            }
            catch
            {
                // Transient clipboard lock by another process; default label is fine.
            }

            ShowCompactSystemPill(copyLabel, kind);
        }
    }

    internal void ShowCompactSystemPill(string label, CompactSystemKind kind)
    {
        if (_cleanedUp || CompactSystemPill == null || string.IsNullOrWhiteSpace(label)) return;
        if (!_isNotchVisible || _isGreetingActive || _isExpanded || _isAnimating ||
            IsCountdownCompletionVisualActive || _isAppNotificationBannerVisible)
        {
            return;
        }

        _isCompactSystemPillActive = true;
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Clipboard, out int token))
        {
            _isCompactSystemPillActive = false;
            return;
        }

        _compactSystemPillToken = token;
        int gen = ++_compactSystemPillGen;

        if (CompactSystemLabelText != null)
        {
            CompactSystemLabelText.Text = label;
        }

        ApplyCompactSystemVisualStyle(kind);

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

        double pillWidth = Math.Max(_collapsedWidth, Math.Clamp(74.0 + label.Length * 7.8, 188.0, 284.0));
        AnimateCompactWidth(pillWidth, _dur250, _easeExpOut6, token);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = _collapsedHeight;

        CompactSystemPill.Visibility = Visibility.Visible;
        CompactSystemPill.BeginAnimation(OpacityProperty, null);
        CompactSystemPill.BeginAnimation(OpacityProperty, MakeAnim(0.0, 1.0, _dur150, _easeQuadOut));
        PlayCompactSystemFluidRipple(kind);

        _compactSystemPillTimer?.Stop();
        _compactSystemPillTimer ??= new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(1350) };
        _compactSystemPillTimer.Tick -= OnCompactSystemPillTick;
        _compactSystemPillTimer.Tick += OnCompactSystemPillTick;
        _compactSystemPillTimer.Start();

        void OnCompactSystemPillTick(object? s, EventArgs e)
        {
            _compactSystemPillTimer?.Stop();
            if (gen == _compactSystemPillGen)
            {
                DismissCompactSystemPill();
            }
        }
    }

    private void ApplyCompactSystemVisualStyle(CompactSystemKind kind)
    {
        Color tint;
        string pathData;
        switch (kind)
        {
            case CompactSystemKind.LockOn:
                tint = Color.FromRgb(0x38, 0xBD, 0xF8);
                pathData = "M18,8H17V6A5,5 0 0,0 7,6V8H6A2,2 0 0,0 4,10V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V10A2,2 0 0,0 18,8ZM9,6A3,3 0 0,1 15,6V8H9V6Z";
                break;
            case CompactSystemKind.LockOff:
                tint = Color.FromRgb(0xA1, 0xA1, 0xAA);
                pathData = "M18,8H17V6A5,5 0 0,0 7,6H9A3,3 0 0,1 15,6V8H6A2,2 0 0,0 4,10V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V10A2,2 0 0,0 18,8Z";
                break;
            case CompactSystemKind.FnKey:
                tint = Color.FromRgb(0xA7, 0x8B, 0xFA);
                pathData = "M13,2L4,14H11L10,22L20,10H13L13,2Z";
                break;
            case CompactSystemKind.Screenshot:
                tint = Color.FromRgb(0xFB, 0xBF, 0x24);
                pathData = "M4,4H9V6H6V9H4V4M15,4H20V9H18V6H15V4M4,15H6V18H9V20H4V15M18,15H20V20H15V18H18V15M9,9H15V15H9V9Z";
                break;
            case CompactSystemKind.ChargerOn:
                tint = Color.FromRgb(0x34, 0xD3, 0x99);
                pathData = "M13,2L4,14H11L10,22L20,10H13L13,2Z";
                break;
            case CompactSystemKind.ChargerOff:
                tint = Color.FromRgb(0xA1, 0xA1, 0xAA);
                pathData = "M16,4h-2V2h-4v2H8C6.9,4,6,4.9,6,6v14c0,1.1,0.9,2,2,2h8c1.1,0,2-0.9,2-2V6C18,4.9,17.1,4,16,4z";
                break;
            default:
                tint = Color.FromRgb(0x38, 0xBD, 0xF8);
                pathData = "M2.5,8.5 L6,12 L13.5,4.5";
                break;
        }

        if (CompactSystemBadgeBorder != null)
        {
            CompactSystemBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(0x28, tint.R, tint.G, tint.B));
        }
        if (CompactSystemIconPath != null)
        {
            CompactSystemIconPath.Fill = new SolidColorBrush(tint);
            if (kind == CompactSystemKind.Copy)
            {
                CompactSystemIconPath.Stroke = new SolidColorBrush(tint);
                CompactSystemIconPath.StrokeThickness = 2.0;
                CompactSystemIconPath.Fill = Brushes.Transparent;
            }
            else
            {
                CompactSystemIconPath.StrokeThickness = 0;
                CompactSystemIconPath.Stroke = null;
            }
            CompactSystemIconPath.Data = Geometry.Parse(pathData);
        }

        if (CompactSystemRipple1Inner != null)
            CompactSystemRipple1Inner.Color = Color.FromArgb(0x0E, tint.R, tint.G, tint.B);
        if (CompactSystemRipple1Peak != null)
            CompactSystemRipple1Peak.Color = Color.FromArgb(0x4C, tint.R, tint.G, tint.B);
        if (CompactSystemRipple1Outer != null)
            CompactSystemRipple1Outer.Color = Color.FromArgb(0x16, tint.R, tint.G, tint.B);
        if (CompactSystemRipple2Peak != null)
            CompactSystemRipple2Peak.Color = Color.FromArgb(0x34, tint.R, tint.G, tint.B);
    }

    private void PlayCompactSystemFluidRipple(CompactSystemKind kind)
    {
        if (AnimationConfig.ReduceMotion) return;

        if (CompactSystemRipple1 != null && CompactSystemRipple1Scale != null)
        {
            var r1Scale = new DoubleAnimation(0.35, 7.0, TimeSpan.FromMilliseconds(680)) { EasingFunction = _easeQuadOut };
            var r1Op = new DoubleAnimationUsingKeyFrames();
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85))));
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680)), _easeQuadOut));
            CompactSystemRipple1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, r1Scale);
            CompactSystemRipple1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, r1Scale);
            CompactSystemRipple1.BeginAnimation(OpacityProperty, r1Op);
        }

        if (CompactSystemRipple2 != null && CompactSystemRipple2Scale != null)
        {
            var delay = TimeSpan.FromMilliseconds(130);
            var r2Scale = new DoubleAnimation(0.35, 8.0, TimeSpan.FromMilliseconds(720)) { BeginTime = delay, EasingFunction = _easeQuadOut };
            var r2Op = new DoubleAnimationUsingKeyFrames { BeginTime = delay };
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105))));
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)), _easeQuadOut));
            CompactSystemRipple2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, r2Scale);
            CompactSystemRipple2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, r2Scale);
            CompactSystemRipple2.BeginAnimation(OpacityProperty, r2Op);
        }

        if (CompactSystemWaterSheen != null && CompactSystemWaterSheenTranslate != null)
        {
            var sheenX = new DoubleAnimation(-130, 130, TimeSpan.FromMilliseconds(620)) { EasingFunction = _easeQuadOut };
            var sheenOp = new DoubleAnimationUsingKeyFrames();
            sheenOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sheenOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            sheenOp.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620))));
            CompactSystemWaterSheenTranslate.BeginAnimation(TranslateTransform.XProperty, sheenX);
            CompactSystemWaterSheen.BeginAnimation(OpacityProperty, sheenOp);
        }

        if (CompactSystemContentScale != null)
        {
            var sx = new DoubleAnimationUsingKeyFrames();
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.07, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), _easeQuadOut));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310)), _easeQuadOut));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430)), _easeQuadOut));

            var sy = new DoubleAnimationUsingKeyFrames();
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), _easeQuadOut));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.015, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310)), _easeQuadOut));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430)), _easeQuadOut));

            CompactSystemContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            CompactSystemContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }
    }

    private void DismissCompactSystemPill()
    {
        if (!_isCompactSystemPillActive || CompactSystemPill == null) return;
        _isCompactSystemPillActive = false;
        _compactSystemPillTimer?.Stop();
        int gen = ++_compactSystemPillGen;
        int token = _compactSystemPillToken;
        _compactSystemPillToken = 0;

        var fadeOut = MakeAnim(CompactSystemPill.Opacity, 0.0, _dur150, _easeQuadOut);
        fadeOut.Completed += (_, _) =>
        {
            if (gen != _compactSystemPillGen) return;
            CompactSystemPill.Visibility = Visibility.Collapsed;
            CompactSystemPill.BeginAnimation(OpacityProperty, null);
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
        };
        CompactSystemPill.BeginAnimation(OpacityProperty, fadeOut);
    }

    internal void DismissCompactSystemPillImmediate()
    {
        if (!_isCompactSystemPillActive) return;
        _isCompactSystemPillActive = false;
        _compactSystemPillTimer?.Stop();
        ++_compactSystemPillGen;
        int token = _compactSystemPillToken;
        _compactSystemPillToken = 0;
        if (CompactSystemPill != null)
        {
            CompactSystemPill.BeginAnimation(OpacityProperty, null);
            CompactSystemPill.Opacity = 0;
            CompactSystemPill.Visibility = Visibility.Collapsed;
        }
        _compactPillArbiter.Release(token);
    }
    private void ShowClipboardCopiedState()
    {
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Clipboard, out int token))
            return;

        _clipboardPeekToken = token;
        _isClipboardPeekActive = true;

        SuppressPrivacyDot();

        ClipboardCopiedText.Text = Loc.Get("clipboard.copied").TrimStart('✓', ' ');

        CancelThumbnailSwitchAnimations();

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var thumbFadeOut = MakeAnim(1.0, 0.0, _dur150, _easeQuadOut);
        thumbFadeOut.Completed += (s, e) =>
        {
            if (_isClipboardPeekActive && !IsCompactSlotStale(token))
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        };
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFadeOut);

        MusicViz.BeginAnimation(OpacityProperty, null);
        MusicViz.Opacity = 1;
        MusicViz.Visibility = Visibility.Visible;
        MusicViz.SetCopiedFeedback(true);

        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        HideCompactSurface(ClipboardCheckIcon);

        ClipboardCopiedText.Visibility = Visibility.Visible;
        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        ClipboardCopiedTranslate.X = -8;

        var textFadeIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        var textSlideIn = MakeAnim(-8.0, 0.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideIn, VNotch.Services.AnimationConfig.TargetFps);

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeIn);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideIn);

        StopClipboardRevertTimer();

        int revertToken = _clipboardPeekToken;
        var revertTimer = new UiScheduledTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _clipboardRevertTimer = revertTimer;
        revertTimer.Tick += (s, e) =>
        {
            revertTimer.Stop();
            revertTimer.Dispose();
            if (!ReferenceEquals(_clipboardRevertTimer, revertTimer) ||
                revertToken != _clipboardPeekToken)
            {
                return;
            }

            _clipboardRevertTimer = null;
            RevertClipboardCopiedState();
        };
        revertTimer.Start();
    }
    private void RevertClipboardCopiedState()
    {
        int token = _clipboardPeekToken;
        if (IsCompactSlotStale(token)) return;
        _isClipboardPeekActive = false;
        _compactPillArbiter.Release(token);
        _clipboardPeekToken = 0;

        RestorePrivacyDotVisibility();

        MusicViz.SetCopiedFeedback(false);
        HideCompactSurface(ClipboardCheckIcon);

        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        var textFadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        var textSlideOut = MakeAnim(0.0, -8.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideOut, VNotch.Services.AnimationConfig.TargetFps);

        textFadeOut.Completed += (s, e) =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCopiedText.Visibility = Visibility.Collapsed;
        };

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeOut);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideOut);

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Visibility = Visibility.Visible;
        CompactThumbnailBorder.Opacity = 0;
        var thumbFadeIn = MakeAnim(0.0, 1.0, _dur200, _easeQuadOut);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFadeIn);

        ShowMusicVisualizer(duration: _dur100);
    }

    private void CancelClipboardPeekImmediate()
    {
        if (!_isClipboardPeekActive) return;

        StopClipboardRevertTimer();
        MusicViz.SetCopiedFeedback(false);
        _isClipboardPeekActive = false;
        _clipboardPeekToken = 0;

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClipboardCheckIcon.Opacity = 0;
        ClipboardCheckIcon.Visibility = Visibility.Collapsed;

        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ClipboardCopiedText.Opacity = 0;
        ClipboardCopiedText.Visibility = Visibility.Collapsed;

    }

    private void StopClipboardRevertTimer()
    {
        var timer = _clipboardRevertTimer;
        _clipboardRevertTimer = null;
        timer?.Stop();
        timer?.Dispose();
        _compactSystemPillTimer?.Stop();
    }

    #endregion
}
