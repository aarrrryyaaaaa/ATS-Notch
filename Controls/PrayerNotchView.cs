using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// Expanded notch tab styled after Nusa: split Next-Prayer summary + adaptive gradient
/// parabolic sun/moon horizon arc + staggered spring prayer cards and Enter-searchable lokasi.
/// Default palette follows Nusa aurora cyan-cobalt-violet (no green) and adapts live to playing music.
/// </summary>
public sealed class PrayerNotchView : UserControl
{
    private const double ArcWidth = 452.0;
    private const double ArcHeight = 114.0;
    private const double ArcStartX = 14.0;
    private const double ArcEndX = 438.0;
    private const double ArcBaseY = 92.0;
    private const double ArcCtrlX = 226.0;
    private const double ArcCtrlY = -18.0;
    private const double ArcStrokeThickness = 3.2;
    private const double ArcOuterGlowThickness = 11.5;
    private const double ArcInnerGlowThickness = 5.6;

    // Refined neutral celestial palette (zero neon / AI-slop)
    public static readonly Color DefaultAccentLeft = Color.FromRgb(0xE2, 0xE8, 0xF0);
    public static readonly Color DefaultAccentMid = Color.FromRgb(0x94, 0xA3, 0xB8);
    public static readonly Color DefaultAccentRight = Color.FromRgb(0xD6, 0xB8, 0x8D);

    private Color _accentLeft = DefaultAccentLeft;
    private Color _accentMid = DefaultAccentMid;
    private Color _accentRight = DefaultAccentRight;

    private readonly Border _cityPill;
    private readonly TextBlock _cityText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _heroPrayerNameText;
    private readonly TextBlock _heroPrayerTimeText;
    private readonly TextBlock _countdownText;
    private readonly Border _periodProgressFill;
    private readonly ScaleTransform _periodProgressScale;
    private readonly GradientStop _progressStopLeft;
    private readonly GradientStop _progressStopMid;
    private readonly GradientStop _progressStopRight;

    private readonly Canvas _skyCanvas;
    private readonly Path _domeFillPath;
    private readonly GradientStop _domeStopTop;
    private readonly GradientStop _domeStopBottom;
    private readonly Path _arcOuterGlowPath;
    private readonly Path _arcInnerGlowPath;
    private readonly Path _arcProgressPath;
    private readonly Path _arcCometPath;
    private readonly GradientStop _arcStopLeft;
    private readonly GradientStop _arcStopMid;
    private readonly GradientStop _arcStopRight;
    private readonly GradientStop _arcGlowStopLeft;
    private readonly GradientStop _arcGlowStopMid;
    private readonly GradientStop _arcGlowStopRight;
    private readonly double _arcTotalDashUnits;
    private readonly double _arcOuterDashUnits;
    private readonly double _arcInnerDashUnits;
    private double _targetArcProgress = 0.35;
    private double _targetBarProgress = 0.35;
    private bool _ambientLoopsRunning;
    private bool _starsAnimating;
    private int _arcSweepVersion;

    private readonly List<Ellipse> _stars = new(16);
    private readonly List<ArcNodeRefs> _arcNodes = new(5);
    private readonly Ellipse _nextNodePulseRing;
    private readonly ScaleTransform _nextNodePulseScale;
    private readonly Ellipse _orbHalo;
    private readonly ScaleTransform _orbHaloScale;
    private readonly Grid _orbGroup;
    private readonly ScaleTransform _orbPopScale;
    private readonly Ellipse _sunOrb;
    private readonly Path _moonOrb;
    private readonly RadialGradientBrush _orbHaloBrush;
    private readonly GradientStop _orbHaloStopInner;
    private readonly GradientStop _orbHaloStopOuter;

    private readonly Grid _cardsGrid;
    private readonly List<CardRefs> _cards = new(6);

    private readonly Border _cityPickerOverlay;
    private readonly Border _enterBtn;
    private readonly TextBox _locationSearchBox;
    private readonly TextBlock _searchStatusText;
    private readonly WrapPanel _cityPickerWrap;
    private readonly List<Border> _cityButtons = new(32);

    private PrayerScheduleSnapshot? _lastSnapshot;
    private string _selectedCity = "Bekasi";
    private double? _customLat;
    private double? _customLon;
    private double? _customTz;
    private CancellationTokenSource? _searchCts;

    public event Action<PrayerCityPreset>? LocationSelected;
    public event Action<bool>? LocationInputModeRequested;

    private sealed record CardRefs(
        Border Container,
        TranslateTransform Slide,
        ScaleTransform Scale,
        Ellipse NextDot,
        TextBlock NameText,
        TextBlock TimeText);

    private sealed record ArcNodeRefs(
        string PrayerName,
        Ellipse Dot,
        TextBlock LabelText);

    public PrayerNotchView()
    {
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;

        var root = new StackPanel
        {
            Margin = new Thickness(8, 28, 8, 6),
            Orientation = Orientation.Vertical
        };

        // 1. Header Row: "Sholat" + Location Pill + Date (zero Adhan menu button)
        var header = new Grid { Margin = new Thickness(4, 0, 4, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        leftHeader.Children.Add(new TextBlock
        {
            Text = "Sholat",
            Foreground = new SolidColorBrush(Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF)),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _cityPill = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            MaxWidth = 200,
            ToolTip = "Klik untuk ketik desa / kecamatan / kota lalu tekan Enter"
        };
        _cityPill.MouseLeftButtonDown += OnCityPillClicked;
        _cityText = new TextBlock
        {
            Text = "Bekasi ▾",
            Foreground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _cityPill.Child = _cityText;
        leftHeader.Children.Add(_cityPill);

        Grid.SetColumn(leftHeader, 0);
        header.Children.Add(leftHeader);

        _dateText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_dateText, 1);
        header.Children.Add(_dateText);

        root.Children.Add(header);

        // 2. Full-Width Background Celestial Dome + Left Hero Summary
        var heroSection = new Grid { Height = 74, Margin = new Thickness(4, 0, 4, 8) };

        // Left: Next prayer hero info + soft borderless readability scrim + Nusa gradient bar
        var leftScrim = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 224,
            Margin = new Thickness(-8, -4, 0, -4),
            IsHitTestVisible = false,
            Background = new RadialGradientBrush(
                Color.FromArgb(0xB8, 0x05, 0x07, 0x0E),
                Color.FromArgb(0x00, 0x05, 0x07, 0x0E))
            {
                Center = new Point(0.42, 0.52),
                GradientOrigin = new Point(0.42, 0.52),
                RadiusX = 0.58,
                RadiusY = 0.62
            }
        };
        heroSection.Children.Add(leftScrim);

        var leftHero = new StackPanel
        {
            Width = 186,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftHero.Children.Add(new TextBlock
        {
            Text = "Berikutnya",
            Foreground = new SolidColorBrush(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.0,
            FontWeight = FontWeights.Medium
        });

        _heroPrayerNameText = new TextBlock
        {
            Text = "Maghrib",
            Foreground = Brushes.White,
            FontSize = 21.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, -1, 0, 2)
        };
        leftHero.Children.Add(_heroPrayerNameText);

        var timeCountdownRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 7)
        };
        _heroPrayerTimeText = new TextBlock
        {
            Text = "17:48",
            Foreground = new SolidColorBrush(Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _countdownText = new TextBlock
        {
            Text = "dalam 01:08:12",
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Lighten(_accentLeft, 0.42)),
            FontSize = 11.0,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        timeCountdownRow.Children.Add(_heroPrayerTimeText);
        timeCountdownRow.Children.Add(_countdownText);
        leftHero.Children.Add(timeCountdownRow);

        var progressTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true
        };
        _periodProgressScale = new ScaleTransform(0.35, 1);
        _progressStopLeft = new GradientStop(_accentLeft, 0);
        _progressStopMid = new GradientStop(_accentMid, 0.55);
        _progressStopRight = new GradientStop(_accentRight, 1);
        var progressBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        progressBrush.GradientStops.Add(_progressStopLeft);
        progressBrush.GradientStops.Add(_progressStopMid);
        progressBrush.GradientStops.Add(_progressStopRight);

        _periodProgressFill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = progressBrush,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _periodProgressScale
        };
        progressTrack.Child = _periodProgressFill;
        leftHero.Children.Add(progressTrack);

        heroSection.Children.Add(leftHero);
        root.Children.Add(heroSection);

        // Giant Enlarged Celestial Parabolic Arc Canvas spanning the background behind header & hero
        _skyCanvas = new Canvas
        {
            Width = ArcWidth,
            Height = ArcHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0),
            ClipToBounds = true,
            IsHitTestVisible = false
        };

        var (arcGeo, arcLengthPx, domeFillGeo) = BuildParabolicArcGeometry();
        _arcTotalDashUnits = arcLengthPx / ArcStrokeThickness;
        _arcOuterDashUnits = arcLengthPx / ArcOuterGlowThickness;
        _arcInnerDashUnits = arcLengthPx / ArcInnerGlowThickness;

        // Ambient breathing aurora fill beneath the celestial dome
        _domeStopTop = new GradientStop(WithAlpha(_accentMid, 0x38), 0.0);
        _domeStopBottom = new GradientStop(Color.FromArgb(0x00, _accentRight.R, _accentRight.G, _accentRight.B), 1.0);
        var domeBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        domeBrush.GradientStops.Add(_domeStopTop);
        domeBrush.GradientStops.Add(_domeStopBottom);
        _domeFillPath = new Path
        {
            Data = domeFillGeo,
            Fill = domeBrush,
            Opacity = 0.85
        };
        _skyCanvas.Children.Add(_domeFillPath);

        // Soft tapered horizon line across full background width
        var horizonBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        horizonBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
        horizonBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0.2));
        horizonBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF), 0.55));
        horizonBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0.85));
        horizonBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        _skyCanvas.Children.Add(new Line
        {
            X1 = 8,
            Y1 = ArcBaseY,
            X2 = ArcWidth - 8,
            Y2 = ArcBaseY,
            Stroke = horizonBrush,
            StrokeThickness = 1
        });

        // Wide starry sky backdrop (15 smoothly twinkling stars)
        (double X, double Y, double Size)[] starSpecs =
        [
            (28, 22, 1.5), (68, 12, 2.0), (112, 28, 1.4), (146, 10, 2.2), (188, 18, 1.6),
            (226, 8, 2.4), (262, 22, 1.5), (298, 12, 2.1), (336, 26, 1.6), (372, 14, 2.2),
            (410, 24, 1.7), (434, 40, 1.4), (206, 46, 1.3), (318, 44, 1.5), (386, 48, 1.4)
        ];
        for (int s = 0; s < starSpecs.Length; s++)
        {
            var star = new Ellipse
            {
                Width = starSpecs[s].Size,
                Height = starSpecs[s].Size,
                Fill = new SolidColorBrush(Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)),
                Opacity = 0.0
            };
            Canvas.SetLeft(star, starSpecs[s].X);
            Canvas.SetTop(star, starSpecs[s].Y);
            _skyCanvas.Children.Add(star);
            _stars.Add(star);
        }

        // Faint full parabolic guide track
        _skyCanvas.Children.Add(new Path
        {
            Data = arcGeo,
            Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        // Dual soft neon aura strokes behind active arc
        _arcGlowStopLeft = new GradientStop(WithAlpha(_accentLeft, 0x38), 0);
        _arcGlowStopMid = new GradientStop(WithAlpha(_accentMid, 0x38), 0.55);
        _arcGlowStopRight = new GradientStop(WithAlpha(_accentRight, 0x38), 1);
        var arcGlowBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        arcGlowBrush.GradientStops.Add(_arcGlowStopLeft);
        arcGlowBrush.GradientStops.Add(_arcGlowStopMid);
        arcGlowBrush.GradientStops.Add(_arcGlowStopRight);

        _arcOuterGlowPath = new Path
        {
            Data = arcGeo,
            Stroke = arcGlowBrush,
            StrokeThickness = ArcOuterGlowThickness,
            Opacity = 0.52,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = [_arcOuterDashUnits, _arcOuterDashUnits * 2.0],
            StrokeDashOffset = _arcOuterDashUnits * (1.0 - _targetArcProgress)
        };
        _skyCanvas.Children.Add(_arcOuterGlowPath);

        _arcInnerGlowPath = new Path
        {
            Data = arcGeo,
            Stroke = arcGlowBrush,
            StrokeThickness = ArcInnerGlowThickness,
            Opacity = 0.88,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = [_arcInnerDashUnits, _arcInnerDashUnits * 2.0],
            StrokeDashOffset = _arcInnerDashUnits * (1.0 - _targetArcProgress)
        };
        _skyCanvas.Children.Add(_arcInnerGlowPath);

        // Main vivid gradient arc stroke
        _arcStopLeft = new GradientStop(_accentLeft, 0);
        _arcStopMid = new GradientStop(_accentMid, 0.55);
        _arcStopRight = new GradientStop(_accentRight, 1);
        var arcBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        arcBrush.GradientStops.Add(_arcStopLeft);
        arcBrush.GradientStops.Add(_arcStopMid);
        arcBrush.GradientStops.Add(_arcStopRight);

        _arcProgressPath = new Path
        {
            Data = arcGeo,
            Stroke = arcBrush,
            StrokeThickness = ArcStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = [_arcTotalDashUnits, _arcTotalDashUnits * 2.0],
            StrokeDashOffset = _arcTotalDashUnits * (1.0 - _targetArcProgress)
        };
        _skyCanvas.Children.Add(_arcProgressPath);

        // Traveling luminous comet pulse racing along the grand curve
        _arcCometPath = new Path
        {
            Data = arcGeo,
            Stroke = new SolidColorBrush(Color.FromArgb(0xB8, 0xEC, 0xF7, 0xFF)),
            StrokeThickness = 3.6,
            Opacity = 0.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = [11.0, _arcTotalDashUnits + 80.0],
            StrokeDashOffset = 12.0
        };
        _skyCanvas.Children.Add(_arcCometPath);

        // Next-prayer expanding beacon ring on the sky arc
        _nextNodePulseScale = new ScaleTransform(1, 1);
        _nextNodePulseRing = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = new SolidColorBrush(WithAlpha(_accentLeft, 0xCC)),
            StrokeThickness = 1.4,
            Opacity = 0.0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _nextNodePulseScale
        };
        _skyCanvas.Children.Add(_nextNodePulseRing);

        // 5 main prayer nodes spaced along the grand background dome
        (string Key, string Short, double T, bool ShowLabel)[] nodeSpecs =
        [
            ("Subuh", "Sbh", 0.08, false),
            ("Dzuhur", "Dzh", 0.36, true),
            ("Ashar", "Ash", 0.57, true),
            ("Maghrib", "Mgh", 0.78, true),
            ("Isya", "Isy", 0.93, true),
        ];
        for (int n = 0; n < nodeSpecs.Length; n++)
        {
            var (nx, ny) = EvaluateArc(nodeSpecs[n].T);
            var dot = new Ellipse
            {
                Width = 5.5,
                Height = 5.5,
                Fill = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF))
            };
            Canvas.SetLeft(dot, nx - 2.75);
            Canvas.SetTop(dot, ny - 2.75);
            _skyCanvas.Children.Add(dot);

            var lbl = new TextBlock
            {
                Text = nodeSpecs[n].Short,
                FontSize = 8.8,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)),
                Visibility = nodeSpecs[n].ShowLabel ? Visibility.Visible : Visibility.Collapsed
            };
            Canvas.SetLeft(lbl, nx - 9);
            Canvas.SetTop(lbl, ArcBaseY + 4);
            _skyCanvas.Children.Add(lbl);

            _arcNodes.Add(new ArcNodeRefs(nodeSpecs[n].Key, dot, lbl));
        }

        // Enlarged luminous Solar / Lunar celestial orb + pulsing halo
        _orbPopScale = new ScaleTransform(1, 1);
        _orbGroup = new Grid
        {
            Width = 30,
            Height = 30,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _orbPopScale
        };
        _orbHaloScale = new ScaleTransform(1, 1);
        _orbHaloStopInner = new GradientStop(Color.FromArgb(0xE2, 0xFF, 0xE9, 0xA8), 0.22);
        _orbHaloStopOuter = new GradientStop(Color.FromArgb(0x00, 0xFF, 0xB8, 0x4D), 1.0);
        _orbHaloBrush = new RadialGradientBrush();
        _orbHaloBrush.GradientStops.Add(_orbHaloStopInner);
        _orbHaloBrush.GradientStops.Add(_orbHaloStopOuter);
        _orbHalo = new Ellipse
        {
            Width = 30,
            Height = 30,
            Fill = _orbHaloBrush,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _orbHaloScale
        };
        _orbGroup.Children.Add(_orbHalo);

        _sunOrb = new Ellipse
        {
            Width = 9.0,
            Height = 9.0,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _orbGroup.Children.Add(_sunOrb);

        _moonOrb = new Path
        {
            Data = Geometry.Parse("M 6.0,0.5 A 5.0,5.0 0 1 0 10.4,7.8 A 4.0,4.0 0 1 1 6.0,0.5 Z"),
            Fill = Brushes.White,
            Width = 11.5,
            Height = 11.5,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _orbGroup.Children.Add(_moonOrb);

        _skyCanvas.Children.Add(_orbGroup);
        IsVisibleChanged += OnViewIsVisibleChanged;

        // 3. Compact 6-card strip below the Nusa split hero
        _cardsGrid = new Grid { Height = 46, Margin = new Thickness(2, 0, 2, 0) };
        for (int i = 0; i < 6; i++)
        {
            _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var slide = new TranslateTransform(0, 0);
            var scale = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(slide);

            var card = new Border
            {
                Margin = new Thickness(i == 0 ? 0 : 2.5, 0, i == 5 ? 0 : 2.5, 0),
                Padding = new Thickness(3, 5, 3, 5),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = group
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var nextDot = new Ellipse
            {
                Width = 4.2,
                Height = 4.2,
                Margin = new Thickness(0, 0, 3.5, 0),
                Fill = new SolidColorBrush(Lighten(_accentLeft, 0.35)),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center
            };
            var nameTb = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 9.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF))
            };
            titleRow.Children.Add(nextDot);
            titleRow.Children.Add(nameTb);

            var timeTb = new TextBlock
            {
                Margin = new Thickness(0, 1.5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12.2,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF))
            };
            stack.Children.Add(titleRow);
            stack.Children.Add(timeTb);
            card.Child = stack;

            Grid.SetColumn(card, i);
            _cardsGrid.Children.Add(card);
            _cards.Add(new CardRefs(card, slide, scale, nextDot, nameTb, timeTb));
        }

        root.Children.Add(_cardsGrid);

        // 4. Inline Searchable Location Picker (type Desa / Kecamatan / Kota + press Enter)
        _cityPickerOverlay = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x10, 0x12, 0x18)),
            BorderBrush = new SolidColorBrush(WithAlpha(_accentMid, 0x85)),
            BorderThickness = new Thickness(1)
        };

        var pickerStack = new Grid();
        pickerStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pickerStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pickerStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputWrap = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3)
        };
        var inputGrid = new Grid();
        var placeholder = new TextBlock
        {
            Text = "Ketik desa / kecamatan / kota lalu tekan Enter...",
            Foreground = new SolidColorBrush(Color.FromArgb(0x68, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _locationSearchBox = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _locationSearchBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = string.IsNullOrEmpty(_locationSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            FilterLocalCitiesPreview(_locationSearchBox.Text);
        };
        _locationSearchBox.PreviewKeyDown += OnSearchBoxKeyDown;
        inputGrid.Children.Add(placeholder);
        inputGrid.Children.Add(_locationSearchBox);
        inputWrap.Child = inputGrid;

        Grid.SetColumn(inputWrap, 0);
        searchRow.Children.Add(inputWrap);

        _enterBtn = new Border
        {
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(WithAlpha(_accentMid, 0x52)),
            BorderBrush = new SolidColorBrush(WithAlpha(_accentLeft, 0x99)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        _enterBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _ = ExecuteLocationSearchAsync(_locationSearchBox.Text, selectFirstMatch: true);
        };
        _enterBtn.Child = new TextBlock
        {
            Text = "Cari / Enter",
            Foreground = Brushes.White,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_enterBtn, 1);
        searchRow.Children.Add(_enterBtn);

        var closeBtn = new Border
        {
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand
        };
        closeBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseCityPicker();
        };
        closeBtn.Child = new TextBlock
        {
            Text = "✕",
            Foreground = Brushes.White,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(closeBtn, 2);
        searchRow.Children.Add(closeBtn);

        Grid.SetRow(searchRow, 0);
        pickerStack.Children.Add(searchRow);

        _searchStatusText = new TextBlock
        {
            Text = "Pilih cepat atau ketik nama desa/kecamatan/kota lalu Enter:",
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            FontSize = 9.8,
            Margin = new Thickness(2, 0, 2, 4)
        };
        Grid.SetRow(_searchStatusText, 1);
        pickerStack.Children.Add(_searchStatusText);

        var scroll = new ScrollViewer
        {
            MaxHeight = 76,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.PreviewMouseWheel += (s, e) =>
        {
            if (s is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.45);
                e.Handled = true;
            }
        };

        _cityPickerWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        PopulateCityChips(PrayerTimesService.Cities);
        scroll.Content = _cityPickerWrap;
        Grid.SetRow(scroll, 2);
        pickerStack.Children.Add(scroll);
        _cityPickerOverlay.Child = pickerStack;

        var hostGrid = new Grid();
        hostGrid.Children.Add(_skyCanvas);
        hostGrid.Children.Add(root);
        hostGrid.Children.Add(_cityPickerOverlay);
        Content = hostGrid;
    }

    public bool IsCityPickerOpen => _cityPickerOverlay.Visibility == Visibility.Visible;

    public void SetAccentPalette(Color left, Color mid, Color right)
    {
        if (_accentLeft == left && _accentMid == mid && _accentRight == right) return;
        _accentLeft = left;
        _accentMid = mid;
        _accentRight = right;
        ApplyAccentPaletteVisuals();
    }

    public void ResetAccentPalette()
    {
        SetAccentPalette(DefaultAccentLeft, DefaultAccentMid, DefaultAccentRight);
    }

    public void UpdateLocationAndRefresh(string? cityName, double? customLat, double? customLon, double? customTz)
    {
        var resolved = PrayerTimesService.ResolveCity(cityName, customLat, customLon, customTz);
        _selectedCity = resolved.Name;
        _customLat = resolved.Latitude;
        _customLon = resolved.Longitude;
        _customTz = resolved.TzOffsetHours;
        RefreshNow();
    }

    public void CloseCityPicker()
    {
        _searchCts?.Cancel();
        if (_cityPickerOverlay.Visibility != Visibility.Collapsed)
        {
            _cityPickerOverlay.Visibility = Visibility.Collapsed;
            LocationInputModeRequested?.Invoke(false);
        }
    }

    public void PlayEntranceAnimation()
    {
        if (AnimationConfig.ReduceMotion)
        {
            StopAmbientSkyAnimations();
            _periodProgressScale.ScaleX = _targetBarProgress;
            ApplyArcProgressDashOffsets(_targetArcProgress);
            _orbGroup.Opacity = 1.0;
            _orbPopScale.ScaleX = _orbPopScale.ScaleY = 1.0;
            return;
        }

        // 1. Progress bar glide (.pr-bar)
        _periodProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _periodProgressScale.ScaleX = 0.04;
        var barAnim = new DoubleAnimation(0.04, _targetBarProgress, TimeSpan.FromMilliseconds(1100))
        {
            BeginTime = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(barAnim, AnimationConfig.TargetFps);
        _periodProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, barAnim);

        // 2. Giant parabolic gradient arc + outer/inner neon bloom sweep (.pr-arc)
        int sweepVersion = ++_arcSweepVersion;
        AnimateArcStrokeSweep(_arcOuterGlowPath, _arcOuterDashUnits, sweepVersion);
        AnimateArcStrokeSweep(_arcInnerGlowPath, _arcInnerDashUnits, sweepVersion);
        AnimateArcStrokeSweep(_arcProgressPath, _arcTotalDashUnits, sweepVersion);

        // 3. Solar / lunar orb spring pop (.pr-sun)
        _orbGroup.BeginAnimation(OpacityProperty, null);
        _orbPopScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _orbPopScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _orbGroup.Opacity = 0.0;
        _orbPopScale.ScaleX = _orbPopScale.ScaleY = 0.3;

        var orbDelay = TimeSpan.FromMilliseconds(750);
        var orbFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = orbDelay };
        var orbPop = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(680))
        {
            BeginTime = orbDelay,
            EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(orbFade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(orbPop, AnimationConfig.TargetFps);
        _orbGroup.BeginAnimation(OpacityProperty, orbFade);
        _orbPopScale.BeginAnimation(ScaleTransform.ScaleXProperty, orbPop);
        _orbPopScale.BeginAnimation(ScaleTransform.ScaleYProperty, orbPop);

        // 4. Compact prayer card strip staggered spring entrance
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            TimeSpan delay = TimeSpan.FromMilliseconds(i * 36);

            card.Container.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
            card.Container.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(340))
            {
                BeginTime = delay,
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
            card.Slide.BeginAnimation(TranslateTransform.YProperty, slide);

            var scaleAnim = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
            card.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            card.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        if (IsVisible)
        {
            StartAmbientSkyAnimations();
        }
    }

    private void AnimateArcStrokeSweep(Path path, double dashUnits, int sweepVersion)
    {
        double targetOffset = dashUnits * (1.0 - _targetArcProgress);
        path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        path.StrokeDashOffset = dashUnits;
        var anim = new DoubleAnimation(dashUnits, targetOffset, TimeSpan.FromMilliseconds(1300))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            if (sweepVersion != _arcSweepVersion) return;
            // Release the entrance HoldEnd clock so 1-second RefreshNow ticks advance the arc cleanly.
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            path.StrokeDashOffset = dashUnits * (1.0 - _targetArcProgress);
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        path.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
    }

    private void ApplyArcProgressDashOffsets(double progress)
    {
        double remaining = 1.0 - progress;
        _arcProgressPath.StrokeDashOffset = _arcTotalDashUnits * remaining;
        _arcOuterGlowPath.StrokeDashOffset = _arcOuterDashUnits * remaining;
        _arcInnerGlowPath.StrokeDashOffset = _arcInnerDashUnits * remaining;
    }

    public void RefreshNow()
    {
        var now = DateTime.Now;
        var snapshot = PrayerTimesService.ComputeSchedule(_selectedCity, now, _customLat, _customLon, _customTz);
        _lastSnapshot = snapshot;

        _cityText.Text = $"{snapshot.CityName} ▾";
        _dateText.Text = $"{now:HH:mm:ss} · {snapshot.DateLabel}";

        PrayerEntry? nextEntry = null;
        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            if (snapshot.Entries[i].IsNext)
            {
                nextEntry = snapshot.Entries[i];
                break;
            }
        }
        nextEntry ??= snapshot.Entries[0];

        _heroPrayerNameText.Text = snapshot.NextPrayerName;
        _heroPrayerTimeText.Text = $"{nextEntry.Time.Hours:D2}:{nextEntry.Time.Minutes:D2}";
        _countdownText.Text = $"dalam {snapshot.CountdownLabel}";

        _targetBarProgress = Math.Clamp(snapshot.PeriodProgress, 0.04, 1.0);
        _periodProgressScale.ScaleX = _targetBarProgress;

        UpdateCelestialArcAndNodes(now, snapshot);
        UpdateCardStyles(snapshot);
    }

    private void ApplyAccentPaletteVisuals()
    {
        _progressStopLeft.Color = _accentLeft;
        _progressStopMid.Color = _accentMid;
        _progressStopRight.Color = _accentRight;

        _arcStopLeft.Color = _accentLeft;
        _arcStopMid.Color = _accentMid;
        _arcStopRight.Color = _accentRight;

        _arcGlowStopLeft.Color = WithAlpha(_accentLeft, 0x38);
        _arcGlowStopMid.Color = WithAlpha(_accentMid, 0x38);
        _arcGlowStopRight.Color = WithAlpha(_accentRight, 0x38);
        _domeStopTop.Color = WithAlpha(_accentMid, 0x38);
        _domeStopBottom.Color = Color.FromArgb(0x00, _accentRight.R, _accentRight.G, _accentRight.B);
        _nextNodePulseRing.Stroke = new SolidColorBrush(WithAlpha(_accentLeft, 0xCC));

        Color skyTint = Lighten(_accentLeft, 0.35);
        _cityPill.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        _cityPill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        _cityText.Foreground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF));
        _countdownText.Foreground = new SolidColorBrush(skyTint);

        _cityPickerOverlay.BorderBrush = new SolidColorBrush(WithAlpha(_accentMid, 0x85));
        _enterBtn.Background = new SolidColorBrush(WithAlpha(_accentMid, 0x52));
        _enterBtn.BorderBrush = new SolidColorBrush(WithAlpha(_accentLeft, 0x99));

        if (_lastSnapshot != null)
        {
            UpdateCelestialArcAndNodes(DateTime.Now, _lastSnapshot);
            UpdateCardStyles(_lastSnapshot);
        }
    }

    private void UpdateCardStyles(PrayerScheduleSnapshot snapshot)
    {
        int count = Math.Min(_cards.Count, snapshot.Entries.Count);
        for (int i = 0; i < count; i++)
        {
            var entry = snapshot.Entries[i];
            var card = _cards[i];
            card.NameText.Text = entry.Name;
            card.TimeText.Text = $"{entry.Time.Hours:D2}:{entry.Time.Minutes:D2}";
            card.NextDot.Fill = new SolidColorBrush(Lighten(_accentLeft, 0.35));
            card.NextDot.Visibility = entry.IsNext ? Visibility.Visible : Visibility.Collapsed;

            if (entry.IsNext)
            {
                var bg = new LinearGradientBrush(
                    WithAlpha(_accentLeft, 0x36),
                    WithAlpha(_accentRight, 0x2E),
                    new Point(0, 0),
                    new Point(1, 1));
                card.Container.Background = bg;
                card.Container.BorderBrush = new SolidColorBrush(WithAlpha(_accentLeft, 0xC8));
                card.NameText.Foreground = new SolidColorBrush(Lighten(_accentLeft, 0.48));
                card.TimeText.Foreground = Brushes.White;
            }
            else if (entry.IsPassed)
            {
                card.Container.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
                card.Container.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                card.NameText.Foreground = new SolidColorBrush(Color.FromArgb(0x68, 0xFF, 0xFF, 0xFF));
                card.TimeText.Foreground = new SolidColorBrush(Color.FromArgb(0x82, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                card.Container.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
                card.Container.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                card.NameText.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                card.TimeText.Foreground = new SolidColorBrush(Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    private void UpdateCelestialArcAndNodes(DateTime now, PrayerScheduleSnapshot snapshot)
    {
        TimeSpan subuh = TimeSpan.FromHours(4.5);
        TimeSpan maghrib = TimeSpan.FromHours(18.0);
        TimeSpan isya = TimeSpan.FromHours(19.0);
        foreach (var e in snapshot.Entries)
        {
            if (e.Name == "Subuh") subuh = e.Time;
            else if (e.Name == "Maghrib") maghrib = e.Time;
            else if (e.Name == "Isya") isya = e.Time;
        }

        double tod = now.TimeOfDay.TotalHours;
        double arcStartH = subuh.TotalHours;
        double arcEndH = Math.Max(arcStartH + 10.0, isya.TotalHours + 1.0);
        double sunriseH = arcStartH + 1.2;
        bool isDay = tod >= sunriseH && tod <= maghrib.TotalHours;

        // Arc progress maps Subuh -> post-Isya along the grand parabolic dome
        double t = tod >= arcStartH
            ? Math.Clamp((tod - arcStartH) / (arcEndH - arcStartH), 0.04, 0.98)
            : 0.96;
        _targetArcProgress = t;
        ApplyArcProgressDashOffsets(_targetArcProgress);

        // Starfield active at night (animated twinkles when visible; static fallback otherwise)
        if (isDay)
        {
            StopStarTwinkleAnimations(targetOpacity: 0.0);
        }
        else if (!_ambientLoopsRunning || AnimationConfig.ReduceMotion)
        {
            StopStarTwinkleAnimations(targetOpacity: 0.65);
        }
        else
        {
            StartStarTwinkleAnimations();
        }

        // Update 5 main prayer dots along the parabola + align next-prayer beacon ring
        bool positionedBeacon = false;
        foreach (var node in _arcNodes)
        {
            PrayerEntry? match = null;
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                if (string.Equals(snapshot.Entries[i].Name, node.PrayerName, StringComparison.OrdinalIgnoreCase))
                {
                    match = snapshot.Entries[i];
                    break;
                }
            }
            bool isNext = match?.IsNext == true;
            bool isPassed = match?.IsPassed == true;
            node.Dot.Fill = isNext
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(isPassed ? (byte)0xC0 : (byte)0x5C, _accentLeft.R, _accentLeft.G, _accentLeft.B));
            double dotSize = isNext ? 7.2 : 5.0;
            double oldSize = node.Dot.Width;
            if (Math.Abs(oldSize - dotSize) > 0.01)
            {
                double cx = Canvas.GetLeft(node.Dot) + oldSize * 0.5;
                double cy = Canvas.GetTop(node.Dot) + oldSize * 0.5;
                node.Dot.Width = node.Dot.Height = dotSize;
                Canvas.SetLeft(node.Dot, cx - dotSize * 0.5);
                Canvas.SetTop(node.Dot, cy - dotSize * 0.5);
            }
            if (isNext && !positionedBeacon)
            {
                positionedBeacon = true;
                double cx = Canvas.GetLeft(node.Dot) + dotSize * 0.5;
                double cy = Canvas.GetTop(node.Dot) + dotSize * 0.5;
                Canvas.SetLeft(_nextNodePulseRing, cx - 7.0);
                Canvas.SetTop(_nextNodePulseRing, cy - 7.0);
            }
            node.LabelText.Foreground = isNext
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(isPassed ? (byte)0xA4 : (byte)0x6C, 0xFF, 0xFF, 0xFF));
            node.LabelText.FontWeight = isNext ? FontWeights.Bold : FontWeights.SemiBold;
        }
        _nextNodePulseRing.Visibility = positionedBeacon ? Visibility.Visible : Visibility.Collapsed;

        var (orbX, orbY) = EvaluateArc(t);
        _sunOrb.Visibility = isDay ? Visibility.Visible : Visibility.Collapsed;
        _moonOrb.Visibility = isDay ? Visibility.Collapsed : Visibility.Visible;

        if (isDay)
        {
            _orbHaloStopInner.Color = Color.FromArgb(0xE0, 0xFF, 0xE9, 0xA8);
            _orbHaloStopOuter.Color = Color.FromArgb(0x00, 0xFF, 0xB8, 0x4D);
        }
        else
        {
            _orbHaloStopInner.Color = Color.FromArgb(0xCC, _accentLeft.R, _accentLeft.G, _accentLeft.B);
            _orbHaloStopOuter.Color = Color.FromArgb(0x00, _accentRight.R, _accentRight.G, _accentRight.B);
        }

        Canvas.SetLeft(_orbGroup, orbX - 15.0);
        Canvas.SetTop(_orbGroup, orbY - 15.0);
    }

    private static (double X, double Y) EvaluateArc(double t)
    {
        double u = 1.0 - t;
        double x = u * u * ArcStartX + 2.0 * u * t * ArcCtrlX + t * t * ArcEndX;
        double y = u * u * ArcBaseY + 2.0 * u * t * ArcCtrlY + t * t * ArcBaseY;
        return (x, y);
    }

    private static (Geometry ArcGeo, double Length, Geometry DomeFillGeo) BuildParabolicArcGeometry()
    {
        const int steps = 64;
        var fig = new PathFigure { StartPoint = new Point(ArcStartX, ArcBaseY), IsClosed = false, IsFilled = false };
        var fillFig = new PathFigure { StartPoint = new Point(ArcStartX, ArcBaseY), IsClosed = true, IsFilled = true };
        double length = 0.0;
        var prev = new Point(ArcStartX, ArcBaseY);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            var (x, y) = EvaluateArc(t);
            var pt = new Point(x, y);
            fig.Segments.Add(new LineSegment(pt, true));
            fillFig.Segments.Add(new LineSegment(pt, true));
            double dx = pt.X - prev.X;
            double dy = pt.Y - prev.Y;
            length += Math.Sqrt(dx * dx + dy * dy);
            prev = pt;
        }
        fillFig.Segments.Add(new LineSegment(new Point(ArcStartX, ArcBaseY), true));

        var arcGeo = new PathGeometry();
        arcGeo.Figures.Add(fig);
        arcGeo.Freeze();

        var domeGeo = new PathGeometry();
        domeGeo.Figures.Add(fillFig);
        domeGeo.Freeze();
        return (arcGeo, length, domeGeo);
    }

    private void OnViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && !AnimationConfig.ReduceMotion)
        {
            StartAmbientSkyAnimations();
        }
        else
        {
            StopAmbientSkyAnimations();
        }
    }

    private void StartAmbientSkyAnimations()
    {
        if (AnimationConfig.ReduceMotion || _ambientLoopsRunning) return;
        _ambientLoopsRunning = true;

        // 1. Breathing sun/moon halo
        var orbPulse = new DoubleAnimation(0.84, 1.38, TimeSpan.FromMilliseconds(1600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(orbPulse, 30);
        _orbHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, orbPulse);
        _orbHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, orbPulse);

        // 2. Aurora sky dome gentle ambient breathe
        var domeBreathe = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(3400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(domeBreathe, 30);
        _domeFillPath.BeginAnimation(OpacityProperty, domeBreathe);

        // 3. Traveling luminous comet pulse racing smoothly along the curve
        var cometSlide = new DoubleAnimation(14.0, -(_arcTotalDashUnits + 14.0), TimeSpan.FromMilliseconds(3600))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(cometSlide, 30);
        _arcCometPath.BeginAnimation(Shape.StrokeDashOffsetProperty, cometSlide);

        var cometOpacity = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        cometOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        cometOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550))));
        cometOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2300))));
        cometOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3100))));
        cometOpacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3600))));
        Timeline.SetDesiredFrameRate(cometOpacity, 30);
        _arcCometPath.BeginAnimation(OpacityProperty, cometOpacity);

        // 4. Next-prayer sonar halo ring pulse
        var ringScale = new DoubleAnimation(0.75, 2.35, TimeSpan.FromMilliseconds(2000))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var ringFade = new DoubleAnimation(0.82, 0.0, TimeSpan.FromMilliseconds(2000))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(ringScale, 30);
        Timeline.SetDesiredFrameRate(ringFade, 30);
        _nextNodePulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringScale);
        _nextNodePulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringScale);
        _nextNodePulseRing.BeginAnimation(OpacityProperty, ringFade);

        // 5. Independent staggered star twinkle at night
        if (_sunOrb.Visibility != Visibility.Visible)
        {
            StartStarTwinkleAnimations();
        }
    }

    private void StartStarTwinkleAnimations()
    {
        if (_starsAnimating || AnimationConfig.ReduceMotion || !_ambientLoopsRunning) return;
        _starsAnimating = true;
        for (int i = 0; i < _stars.Count; i++)
        {
            var twinkle = new DoubleAnimation(0.16, 0.92, TimeSpan.FromMilliseconds(1500 + (i % 5) * 340))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * 190),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(twinkle, 24);
            _stars[i].BeginAnimation(OpacityProperty, twinkle);
        }
    }

    private void StopStarTwinkleAnimations(double targetOpacity)
    {
        _starsAnimating = false;
        foreach (var star in _stars)
        {
            star.BeginAnimation(OpacityProperty, null);
            star.Opacity = targetOpacity;
        }
    }

    private void StopAmbientSkyAnimations()
    {
        if (!_ambientLoopsRunning) return;
        _ambientLoopsRunning = false;

        _orbHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _orbHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _domeFillPath.BeginAnimation(OpacityProperty, null);
        _arcCometPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _arcCometPath.BeginAnimation(OpacityProperty, null);
        _arcCometPath.Opacity = 0.0;
        _nextNodePulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _nextNodePulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _nextNodePulseRing.BeginAnimation(OpacityProperty, null);
        _nextNodePulseRing.Opacity = 0.0;
        StopStarTwinkleAnimations(targetOpacity: _sunOrb.Visibility == Visibility.Visible ? 0.0 : 0.65);
    }

    private void PopulateCityChips(IReadOnlyList<PrayerCityPreset> presets)
    {
        _cityPickerWrap.Children.Clear();
        _cityButtons.Clear();

        for (int i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            bool active = string.Equals(preset.Name, _selectedCity, StringComparison.OrdinalIgnoreCase);

            var btn = new Border
            {
                Tag = preset,
                Margin = new Thickness(2),
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(active
                    ? WithAlpha(_accentMid, 0x52)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(active
                    ? WithAlpha(_accentLeft, 0xD0)
                    : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            btn.Child = new TextBlock
            {
                Text = preset.Name,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)),
                FontSize = 10.2,
                FontWeight = FontWeights.Medium
            };
            btn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ApplySelectedLocation(preset);
            };
            _cityButtons.Add(btn);
            _cityPickerWrap.Children.Add(btn);
        }
    }

    private void FilterLocalCitiesPreview(string query)
    {
        string q = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q))
        {
            _searchStatusText.Text = "Pilih cepat atau ketik nama desa/kecamatan/kota lalu Enter:";
            PopulateCityChips(PrayerTimesService.Cities);
            return;
        }

        var localMatches = new List<PrayerCityPreset>();
        foreach (var city in PrayerTimesService.Cities)
        {
            if (city.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                localMatches.Add(city);
            }
        }

        _searchStatusText.Text = $"Tekan Enter untuk mencari \"{q}\" (desa/kecamatan/kota)...";
        if (localMatches.Count > 0)
        {
            PopulateCityChips(localMatches);
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ExecuteLocationSearchAsync(_locationSearchBox.Text, selectFirstMatch: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseCityPicker();
        }
    }

    private async Task ExecuteLocationSearchAsync(string query, bool selectFirstMatch)
    {
        string q = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q)) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        _searchStatusText.Text = $"Mencari koordinat \"{q}\"...";
        IReadOnlyList<PrayerCityPreset> matches;
        try
        {
            matches = await PrayerTimesService.SearchLocationsAsync(q, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.Token.IsCancellationRequested) return;

        if (matches.Count == 0)
        {
            _searchStatusText.Text = $"Lokasi \"{q}\" tidak ditemukan. Coba tambah nama kabupaten/kota.";
            return;
        }

        if (selectFirstMatch)
        {
            ApplySelectedLocation(matches[0]);
            return;
        }

        _searchStatusText.Text = $"Hasil untuk \"{q}\": klik lokasi di bawah";
        PopulateCityChips(matches);
    }

    private void ApplySelectedLocation(PrayerCityPreset preset)
    {
        _selectedCity = preset.Name;
        _customLat = preset.Latitude;
        _customLon = preset.Longitude;
        _customTz = preset.TzOffsetHours;
        CloseCityPicker();
        RefreshNow();
        LocationSelected?.Invoke(preset);
    }

    private void OnCityPillClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_cityPickerOverlay.Visibility != Visibility.Visible)
        {
            PopulateCityChips(PrayerTimesService.Cities);
            _locationSearchBox.Text = string.Empty;
            _cityPickerOverlay.Visibility = Visibility.Visible;
            LocationInputModeRequested?.Invoke(true);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                _locationSearchBox.Focus();
                Keyboard.Focus(_locationSearchBox);
            });
        }
        else
        {
            CloseCityPicker();
        }
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Lighten(Color c, double factor)
    {
        byte r = (byte)Math.Clamp(c.R + (255 - c.R) * factor, 0, 255);
        byte g = (byte)Math.Clamp(c.G + (255 - c.G) * factor, 0, 255);
        byte b = (byte)Math.Clamp(c.B + (255 - c.B) * factor, 0, 255);
        return Color.FromRgb(r, g, b);
    }
}
