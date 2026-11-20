using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Bluetooth Notification

    private bool _isBluetoothNotificationVisible = false;
    private bool _currentBluetoothConnected = true;
    private int _bluetoothNotificationToken = 0;

    private void InitializeBluetoothNotificationController()
    {
        _bluetoothController.ShowRequested += BluetoothController_ShowRequested;
        _bluetoothController.DismissRequested += BluetoothController_DismissRequested;
    }

    private readonly long _bluetoothStartupTicks = Environment.TickCount64;

    private void BluetoothModule_DeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool shown = _bluetoothController.TryShow(info, connected: true,
                _isNotchVisible, _isAnimating, _isExpanded, _isMusicExpanded);
            if (!shown && (Environment.TickCount64 - _bluetoothStartupTicks) > 6000)
            {
                ShowAppNotification(new VNotch.Models.AppNotificationInfo
                {
                    Id = unchecked((uint)($"BT-ON:{info.Name}:{DateTime.Now.Ticks % 1000}".GetHashCode())),
                    AppId = "ATS-Notch.Bluetooth",
                    AppName = "Bluetooth",
                    Title = $"{info.Name} Terhubung",
                    Body = "Perangkat Bluetooth tersambung ke laptop",
                    CreationTime = DateTimeOffset.Now
                });
            }
        });
    }

    private void BluetoothModule_DeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool shown = _bluetoothController.TryShow(info, connected: false,
                _isNotchVisible, _isAnimating, _isExpanded, _isMusicExpanded);
            if (!shown && (Environment.TickCount64 - _bluetoothStartupTicks) > 6000)
            {
                ShowAppNotification(new VNotch.Models.AppNotificationInfo
                {
                    Id = unchecked((uint)($"BT-OFF:{info.Name}:{DateTime.Now.Ticks % 1000}".GetHashCode())),
                    AppId = "ATS-Notch.Bluetooth",
                    AppName = "Bluetooth",
                    Title = $"{info.Name} Terputus",
                    Body = "Koneksi perangkat Bluetooth dilepas",
                    CreationTime = DateTimeOffset.Now
                });
            }
        });
    }

    private void BluetoothController_ShowRequested(BluetoothDeviceInfo device, bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _currentBluetoothConnected = connected;

            if (connected)
            {
                BluetoothDeviceName.Text = device.Name;
                BluetoothIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothStatusText.Text = Loc.Get("bluetooth.connected");
            }
            else
            {
                BluetoothDisconnectDeviceName.Text = device.Name;
                BluetoothDisconnectIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothDisconnectStatusText.Text = Loc.Get("bluetooth.disconnected");
            }

            AnimateBluetoothNotificationIn(connected);
        });
    }

    private void BluetoothController_DismissRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnimateBluetoothNotificationOut();
        });
    }

    private Grid ActiveBluetoothGrid => _currentBluetoothConnected ? BluetoothNotification : BluetoothDisconnectNotification;
    private TranslateTransform ActiveBluetoothTranslate => _currentBluetoothConnected ? BluetoothNotificationTranslate : BluetoothDisconnectNotificationTranslate;
    private ScaleTransform ActiveBluetoothIconScale => _currentBluetoothConnected ? BluetoothIconScale : BluetoothDisconnectIconScale;

    private void AnimateBluetoothNotificationIn(bool connected)
    {
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Bluetooth, out int token))
        {
            return;
        }

        _bluetoothNotificationToken = token;
        _isBluetoothNotificationVisible = true;

        SuppressPrivacyDot();

        var otherGrid = connected ? BluetoothDisconnectNotification : BluetoothNotification;
        otherGrid.Visibility = Visibility.Collapsed;
        otherGrid.Opacity = 0;

        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        var activeIconScale = ActiveBluetoothIconScale;

        activeGrid.Visibility = Visibility.Visible;
        activeGrid.Opacity = 0;

        PlayBluetoothBounce();

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        if (MusicCompactContent.Visibility == Visibility.Visible)
        {
            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = 0;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }

        var fadeIn = MakeCompactNotificationEntrance(0, 1);
        activeGrid.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = MakeCompactNotificationEntrance(6, 0);
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideUp, HandoffBehavior.SnapshotAndReplace);

        var iconScale = MakeAnim(0.8d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        activeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        activeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);

        PlayBluetoothFluidRipple(connected);
    }

    private void PlayBluetoothFluidRipple(bool connected)
    {
        if (AnimationConfig.ReduceMotion) return;

        var ripple1 = connected ? BluetoothRipple1 : BluetoothDisconnectRipple1;
        var ripple1Scale = connected ? BluetoothRipple1Scale : BluetoothDisconnectRipple1Scale;
        var ripple2 = connected ? BluetoothRipple2 : BluetoothDisconnectRipple2;
        var ripple2Scale = connected ? BluetoothRipple2Scale : BluetoothDisconnectRipple2Scale;
        var contentScale = connected ? BluetoothContentScale : BluetoothDisconnectContentScale;

        if (ripple1 != null && ripple1Scale != null)
        {
            var r1Scale = new DoubleAnimation(0.35, 7.2, TimeSpan.FromMilliseconds(720)) { EasingFunction = _easeQuadOut };
            var r1Op = new DoubleAnimationUsingKeyFrames();
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            r1Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)), _easeQuadOut));
            ripple1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, r1Scale);
            ripple1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, r1Scale);
            ripple1.BeginAnimation(OpacityProperty, r1Op);
        }

        if (ripple2 != null && ripple2Scale != null)
        {
            var delay = TimeSpan.FromMilliseconds(140);
            var r2Scale = new DoubleAnimation(0.35, 8.2, TimeSpan.FromMilliseconds(760)) { BeginTime = delay, EasingFunction = _easeQuadOut };
            var r2Op = new DoubleAnimationUsingKeyFrames { BeginTime = delay };
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.65, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
            r2Op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760)), _easeQuadOut));
            ripple2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, r2Scale);
            ripple2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, r2Scale);
            ripple2.BeginAnimation(OpacityProperty, r2Op);
        }

        if (contentScale != null)
        {
            var sx = new DoubleAnimationUsingKeyFrames();
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), _easeQuadOut));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), _easeQuadOut));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), _easeQuadOut));

            var sy = new DoubleAnimationUsingKeyFrames();
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), _easeQuadOut));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.015, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), _easeQuadOut));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), _easeQuadOut));

            contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }
    }

    private void AnimateBluetoothNotificationOut()
    {
        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        int token = _bluetoothNotificationToken;

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (s, e) =>
        {
            if (token != _bluetoothNotificationToken) return;
            if (IsCompactSlotStale(token)) return;

            activeGrid.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
            _bluetoothController.MarkDismissed();
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _bluetoothNotificationToken = 0;

            RestorePrivacyDotVisibility();

            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                var fadeInMusic = MakeCompactContentRestore();
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                var fadeInCollapsed = MakeCompactContentRestore();
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        activeGrid.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeCompactNotificationExit(0, -4);
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideDown, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelBluetoothNotificationImmediate()
    {
        if (!_isBluetoothNotificationVisible) return;

        _isBluetoothNotificationVisible = false;
        _bluetoothNotificationToken = 0;
        _bluetoothController.MarkDismissed();

        BluetoothNotification.BeginAnimation(OpacityProperty, null);
        BluetoothNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BluetoothNotification.Opacity = 0;
        BluetoothNotification.Visibility = Visibility.Collapsed;

        BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
        BluetoothDisconnectNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BluetoothDisconnectNotification.Opacity = 0;
        BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
    }

    private void PlayBluetoothBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(140);
        var durEnd = TimeSpan.FromMilliseconds(700);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, VNotch.Services.AnimationConfig.TargetFps);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    #endregion
}
