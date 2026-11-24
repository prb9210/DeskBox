using System;
using System.Collections.Generic;
using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Views;

/// <summary>
/// The desktop drawer: one glass bead that expands into a full ring of sectors,
/// one sector per格子, all of them together forming a circle.
///
/// Geometry rule: nothing is hard-coded in DIP. The ball sits at the centre of
/// the window's real client area, and the ring radius is derived from that same
/// area, so DPI scaling can never push the ball out of the frame (which is what
/// made earlier builds show an empty rectangle).
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const double BallDiameter = 56;
    private const double ClosedSize = 64;
    private const double WindowPadding = 14;

    // 扇形几何与窗口尺寸解耦（照 ClassFlow 的 RadialMenu：半径是常量、
    // 圆心固定在画布正中）。窗口装不下时整块等比缩放，绝不再按窗口重算半径
    // —— 重算 + 边缘钳制会把环拉成非圆/偏心，扇片就变成一条条斜片。
    private const double FanInner = 44;
    private const double FanOuter = 132;
    private const double ExpandedSize = (FanOuter + WindowPadding) * 2; // 292 DIP
    private const double MinExpandedSize = 190;
    private const double FanGapDegrees = 2.2;

    private const int ScreenMargin = 18;
    private const int EdgeSnapDistance = 110;

    private readonly WidgetDrawerSettings _settings;

    private bool _expanded;
    private bool _pointerDown;
    private bool _collapseHandledPress;
    /// <summary>Press started on the bead while the ring is open. Release
    /// without movement collapses the ring; movement drags the whole ring.</summary>
    private bool _centerPressWhileExpanded;
    private double _layoutDiameterDip = ClosedSize;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private double _scale = 1.0;
    private bool _clipApplied;

    // Recomputed每一次 from the actual client size.
    private double _centerX = ClosedSize / 2;
    private double _centerY = ClosedSize / 2;
    private double _fanInner = 44;
    private double _fanOuter = FanOuter;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] extend-into-titlebar failed: {ex.Message}");
        }

        ApplyColor();
        ApplyChrome();

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (_expanded)
            {
                SetExpanded(false);
                args.Handled = true;
            }
        };
        RootGrid.KeyboardAccelerators.Add(escape);

        RootGrid.Loaded += (_, _) =>
        {
            UpdateStateDot();
            Relayout();
        };

        RootGrid.SizeChanged += (_, _) =>
        {
            Relayout();
            if (_expanded)
            {
                BuildFan();
            }

            // 关键：窗口真正改变尺寸后必须重新裁圆。
            // SetExpanded/ShowAtSavedSpot 里调 Resize 时 Size 还没生效，
            // 那次裁出来的圆偏小 → 球被裁成"剩一部分"。这里用生效后的真实尺寸重裁。
            ApplyWindowClip();
        };
    }

    public IntPtr WindowHandle
    {
        get
        {
            try
            {
                return WindowNative.GetWindowHandle(this);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }
    }

    private AppWindow GetAppWindow()
    {
        IntPtr hWnd = WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
    }

    // ------------------------------------------------------------------ chrome

    private void ApplyChrome()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            WindowShellState.TryApplyBorderlessOverlappedPresenter(appWindow);
            WindowShellState.TryHideFromSwitchers(appWindow);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
            }

            AppBranding.ApplyWindowIcon(appWindow);

            // A borderless overlapped window still gets a 1px frame from DWM, and
            // that frame is what用户 saw as「白色矩形框」. WS_POPUP has no frame at
            // all, so the window becomes pure pixels.
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            int style = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_STYLE);
            style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_THICKFRAME | Win32Helper.WS_BORDER);
            style |= Win32Helper.WS_POPUP | 0x10000000; // WS_POPUP | WS_VISIBLE
            Win32Helper.SetWindowLongPtr(hWnd, Win32Helper.GWL_STYLE, (IntPtr)style);

            int noBorder = -2; // DWMWA_COLOR_NONE
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref noBorder);
            int doNotRound = 1; // DWMWCP_DONOTROUND
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] chrome setup failed: {ex.Message}");
        }

        try
        {
            SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Colors.Transparent);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] transparent backdrop unavailable: {ex.Message}");
            SystemBackdrop = null;
        }

        ApplyScale();
        ApplyWindowClip();
    }

    /// <summary>
    /// 圆不圆只跟「收起状态」有关：收起时把窗口裁成内切圆，球一定是完整的圆。
    ///
    /// 展开时**一律不裁**。扇形的外径是 (窗口/2 - WindowPadding)，比内切圆还大；
    /// 只要裁切半径不是从「窗口真实像素」算出来（早前几版用 DIP×_scale 或
    /// 按屏幕边缘重算直径），圆就会比扇形小或偏心，把扇片切成一条条斜片
    /// —— 用户看到的「展开后是很奇怪的形状」就是它。
    /// </summary>
    private void ApplyWindowClip()
    {
        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            if (_expanded)
            {
                // 展开：撤销区域裁切，窗口整块可见。圆不圆由扇片自己的几何决定，
                // 背景透明由 TransparentTintBackdrop 负责。
                if (_clipApplied)
                {
                    Win32Helper.SetWindowRgn(hWnd, IntPtr.Zero, true);
                    _clipApplied = false;
                    App.Log("[Drawer] clip cleared (expanded)");
                }

                return;
            }

            // 收起：内切圆 = 圆心在窗口正中央、半径 min(宽,高)/2。
            // 球只有 56 DIP、窗口 64 DIP，内切圆必然比球大，所以无论缩放值、
            // 尺寸生效时序如何，都切不到球 —— 这条约束是构造性的，不靠调参。
            SizeInt32 size = GetAppWindow().Size;
            int w = size.Width;
            int h = size.Height;
            int radius = Math.Min(w, h) / 2;
            if (radius < 8)
            {
                App.Log($"[Drawer] clip skipped: win={w}x{h}");
                return;
            }

            int cx = w / 2;
            int cy = h / 2;
            IntPtr region = Win32Helper.CreateEllipticRgn(cx - radius, cy - radius, cx + radius, cy + radius);
            bool ok = Win32Helper.SetWindowRgn(hWnd, region, true) != 0;
            _clipApplied = ok;
            App.Log($"[Drawer] clip d={radius * 2} win={w}x{h} scale={_scale:0.##} ok={ok}");
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] window clip failed: {ex.Message}");
        }
    }

    private void ApplyScale()
    {
        try
        {
            _scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        }
        catch (Exception)
        {
            _scale = 1.0;
        }

        if (_scale <= 0)
        {
            _scale = 1.0;
        }
    }

    /// <summary>球放在窗口真实中心；扇形画在固定坐标里再等比缩放。</summary>
    private void Relayout()
    {
        // 不能用 ActualWidth：SetExpanded 里 Resize 之后马上布局，框架还没测量，
        // 读到的是旧尺寸（曾把环缩成球内的小圈，表现成“点了没反应”）。
        // 目标尺寸只认 _layoutDiameterDip。
        double windowSide = _layoutDiameterDip > 0 ? _layoutDiameterDip : ClosedSize;
        double ballCx = windowSide / 2;
        double ballCy = windowSide / 2;

        // 扇形画在固定的 ExpandedSize 坐标系里，再由 ScaleTransform 等比缩进窗口，
        // 所以圆心永远是 ExpandedSize/2，与窗口实际尺寸无关。
        _centerX = ExpandedSize / 2;
        _centerY = ExpandedSize / 2;
        _fanOuter = FanOuter;
        _fanInner = FanInner;

        double fanScale = Math.Clamp(windowSide / ExpandedSize, 0.4, 1.0);
        FanCanvas.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        FanCanvas.RenderTransform = new ScaleTransform { ScaleX = fanScale, ScaleY = fanScale };

        Canvas.SetLeft(Ball, ballCx - (BallDiameter / 2));
        Canvas.SetTop(Ball, ballCy - (BallDiameter / 2));
        Canvas.SetLeft(BallRing, ballCx - (BallRing.Width / 2));
        Canvas.SetTop(BallRing, ballCy - (BallRing.Height / 2));

        BallGlyph.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(BallGlyph, ballCx - (BallGlyph.DesiredSize.Width / 2));
        Canvas.SetTop(BallGlyph, ballCy - (BallGlyph.DesiredSize.Height / 2));

        Canvas.SetLeft(StateDot, ballCx + 12);
        Canvas.SetTop(StateDot, ballCy + 12);
    }

    private SizeInt32 WindowSizePixels(double diameterDip)
    {
        int pixels = (int)Math.Round(diameterDip * _scale);
        return new SizeInt32(pixels, pixels);
    }

    /// <summary>展开窗口的直径（DIP）：默认就是固定的 ExpandedSize，只有球
    /// 贴着屏幕边缘、装不下时才整体缩小（扇形同步等比缩放，形状不变）。</summary>
    private double AvailableExpandedDip()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            PointInt32 position = appWindow.Position;
            SizeInt32 size = appWindow.Size;

            // 用窗口真实中心当球心：收起/展开都成立，不依赖 ClosedSize 与 _scale。
            int ballCenterX = position.X + (size.Width / 2);
            int ballCenterY = position.Y + (size.Height / 2);

            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            double nearest = Math.Min(
                Math.Min(ballCenterX - work.X, work.X + work.Width - ballCenterX),
                Math.Min(ballCenterY - work.Y, work.Y + work.Height - ballCenterY));

            double available = ((nearest / _scale) * 2) - 8;
            return Math.Clamp(available, MinExpandedSize, ExpandedSize);
        }
        catch (Exception)
        {
            return ExpandedSize;
        }
    }

    public void ShowAtSavedSpot()
    {
        ApplyScale();

        AppWindow appWindow = GetAppWindow();
        RectInt32 work = DisplayArea
            .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        int size = (int)Math.Round(ClosedSize * _scale);
        int margin = (int)Math.Round(ScreenMargin * _scale);

        int x = work.X + work.Width - size - margin;
        int y = work.Y + work.Height - size - margin;

        if (_settings.X is double savedX && _settings.Y is double savedY)
        {
            x = Clamp((int)savedX, work.X, work.X + work.Width - size);
            y = Clamp((int)savedY, work.Y, work.Y + work.Height - size);
        }

        try
        {
            appWindow.Resize(WindowSizePixels(ClosedSize));
            appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] positioning failed: {ex.Message}");
        }

        Activate();
    }

    /// <summary>Ball centre in screen pixels, derived from what the window
    /// actually is right now. Using ClosedSize here while the window was
    /// expanded is what made the bead creep up-left on every click.</summary>
    private PointInt32 BallCenterPixels(AppWindow appWindow)
    {
        PointInt32 position = appWindow.Position;
        SizeInt32 size = appWindow.Size;
        return new PointInt32(position.X + (size.Width / 2), position.Y + (size.Height / 2));
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        AppWindow appWindow = GetAppWindow();
        PointInt32 center = BallCenterPixels(appWindow);
        int ballCenterX = center.X;
        int ballCenterY = center.Y;

        _expanded = expanded;
        double diameter = expanded ? AvailableExpandedDip() : ClosedSize;
        _layoutDiameterDip = diameter;

        // The window grows around the ball centre, so the bead never moves on
        // screen; the ring is then built from the size the window really got.
        int half = (int)Math.Round((diameter / 2) * _scale);
        try
        {
            appWindow.Resize(WindowSizePixels(diameter));
            appWindow.Move(new PointInt32(ballCenterX - half, ballCenterY - half));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] resize failed: {ex.Message}");
        }

        Relayout();

        if (expanded)
        {
            BuildFan();
        }
        else
        {
            FanCanvas.Children.Clear();
        }

        Animate(FanCanvas, expanded ? 1 : 0);
        ApplyWindowClip();
    }

    // ------------------------------------------------------------- appearance

    private void ApplyColor()
    {
        Ball.Fill = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));

        DrawerColorPreset preset = ResolvePreset();
        Ball.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.25, 0.05),
            EndPoint = new Windows.Foundation.Point(0.75, 0.95),
            GradientStops =
            {
                new GradientStop { Color = ParseColor(preset.Start), Offset = 0 },
                new GradientStop { Color = ParseColor(preset.End), Offset = 1 }
            }
        };

        Color rim = ParseColor(preset.Rim);
        BallRing.Stroke = new SolidColorBrush(Color.FromArgb(70, rim.R, rim.G, rim.B));
        Ball.Stroke = new SolidColorBrush(Color.FromArgb(190, rim.R, rim.G, rim.B));
        Ball.StrokeThickness = 1.4;
    }

    private DrawerColorPreset ResolvePreset()
    {
        DrawerColorPreset preset = _settings.ResolveColor();
        if (preset.Id != WidgetDrawerSettings.FollowThemeId)
        {
            return preset;
        }

        bool light = Application.Current.RequestedTheme == ApplicationTheme.Light;
        return WidgetDrawerSettings.Find(light ? "blue" : "cyan") ?? WidgetDrawerSettings.Presets[0];
    }

    private static Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 6 &&
            byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }

        return Color.FromArgb(255, 59, 130, 246);
    }

    private void UpdateStateDot()
    {
        bool parked = (Application.Current as App)?.WidgetsParked == true;
        StateDot.Visibility = parked ? Visibility.Visible : Visibility.Collapsed;
        BallGlyph.Text = parked ? "呼" : "格";
    }

    public void Refresh()
    {
        ApplyColor();
        UpdateStateDot();
        if (_expanded)
        {
            BuildFan();
        }
    }

    private void Animate(UIElement target, double to)
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] fade failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------- ring

    private void BuildFan()
    {
        FanCanvas.Children.Clear();
        if (Application.Current is not App app)
        {
            return;
        }

        IReadOnlyList<(string Id, string Name, bool Visible)> states = app.DrawerWidgetStates();
        if (states.Count == 0)
        {
            return;
        }

        int count = states.Count;
        double step = 360.0 / count;
        double span = Math.Max(8, step - FanGapDegrees);

        for (int i = 0; i < count; i++)
        {
            (string id, string name, bool visible) = states[i];
            double mid = -90 + (step * (i + 0.5));

            var sector = new XamlPath
            {
                Data = BuildSector(mid - (span / 2), mid + (span / 2)),
                Fill = BuildSectorBrush(mid, visible),
                Stroke = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                StrokeThickness = 1,
                Tag = id
            };

            Brush hoverFill = BuildSectorBrush(mid, true, hovered: true);
            Brush restingFill = sector.Fill;
            sector.PointerEntered += (_, _) =>
            {
                sector.Fill = hoverFill;
                sector.Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
                sector.StrokeThickness = 1.5;
            };
            sector.PointerExited += (_, _) =>
            {
                sector.Fill = restingFill;
                sector.Stroke = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
                sector.StrokeThickness = 1;
            };

            string widgetId = id;
            sector.Tapped += async (_, args) =>
            {
                args.Handled = true;
                await app.ToggleWidgetAsync(widgetId);
                BuildFan();
            };

            FanCanvas.Children.Add(sector);
            AddSectorLabel(mid, name, visible);
        }
    }

    private void AddSectorLabel(double degrees, string text, bool visible)
    {
        // Labels run along their own wedge (radially), so a long folder name can
        // never spill over the neighbouring sector. The half on the left is
        // rotated 180 more so the characters stay upright.
        double radius = (_fanInner + _fanOuter) / 2;
        double rad = degrees * Math.PI / 180;
        double x = _centerX + (radius * Math.Cos(rad));
        double y = _centerY + (radius * Math.Sin(rad));

        double norm = ((degrees % 360) + 360) % 360;
        bool flip = norm > 90 && norm < 270;
        double span = Math.Max(56, (_fanOuter - _fanInner) - 16);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)185, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = span,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = flip ? degrees + 180 : degrees }
        };

        Canvas.SetLeft(label, x - (span / 2));
        Canvas.SetTop(label, y - 9);
        FanCanvas.Children.Add(label);

        double dotRadius = _fanOuter - 16;
        double dotX = _centerX + (dotRadius * Math.Cos(rad));
        double dotY = _centerY + (dotRadius * Math.Sin(rad));

        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)80, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, dotX - 3);
        Canvas.SetTop(dot, dotY - 3);
        FanCanvas.Children.Add(dot);
    }

    private Brush BuildSectorBrush(double directionDegrees, bool visible, bool hovered = false)
    {
        DrawerColorPreset preset = ResolvePreset();
        Color inner = Shade(ParseColor(preset.Start), hovered ? 1.06 : 0.74);
        Color outer = Shade(ParseColor(preset.End), hovered ? 0.78 : 0.46);
        byte alpha = hovered ? (byte)255 : visible ? (byte)250 : (byte)158;

        double rad = directionDegrees * Math.PI / 180;
        double cx = Math.Cos(rad) / 2;
        double cy = Math.Sin(rad) / 2;

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5 - cx, 0.5 - cy),
            EndPoint = new Windows.Foundation.Point(0.5 + cx, 0.5 + cy),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(alpha, inner.R, inner.G, inner.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(alpha, outer.R, outer.G, outer.B), Offset = 1 }
            }
        };
    }

    private static Color Shade(Color color, double factor)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * factor, 0, 255),
            (byte)Math.Clamp(color.G * factor, 0, 255),
            (byte)Math.Clamp(color.B * factor, 0, 255));
    }

    private Geometry BuildSector(double a0, double a1)
    {
        Windows.Foundation.Point innerStart = PointAt(_fanInner, a0);
        Windows.Foundation.Point outerStart = PointAt(_fanOuter, a0);
        Windows.Foundation.Point outerEnd = PointAt(_fanOuter, a1);
        Windows.Foundation.Point innerEnd = PointAt(_fanInner, a1);

        var figure = new PathFigure { StartPoint = innerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = outerStart });
        figure.Segments.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Windows.Foundation.Size(_fanOuter, _fanOuter),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = innerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point = innerStart,
            Size = new Windows.Foundation.Size(_fanInner, _fanInner),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private Windows.Foundation.Point PointAt(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Windows.Foundation.Point(
            _centerX + (radius * Math.Cos(rad)),
            _centerY + (radius * Math.Sin(rad)));
    }

    // ---------------------------------------------------------------- pointer

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_expanded)
        {
            Animate(Ball, 1.0);
        }
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging && !_expanded)
        {
            Animate(Ball, 0.62);
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_expanded)
        {
            bool onBead = PressIsOnBead(e);
            if (!onBead && IsSectorSource(e.OriginalSource))
            {
                // A press that started on a sector must reach the sector's own
                // tap handler; only presses on empty space close the ring.
                return;
            }

            if (!onBead)
            {
                SetExpanded(false);
                _collapseHandledPress = true;
                e.Handled = true;
                return;
            }

            // The bead sits inside the ring: a click collapses it, a drag moves
            // the whole ring. Which one it is can only be known on release, so
            // the press is captured here and decided there. Deciding on press
            // (the old behaviour) meant a one-pixel wobble on the bead was read
            // as "clicked the neighbouring sector" and the ring never closed.
            _centerPressWhileExpanded = true;
            BeginPressTracking(e);
            return;
        }

        _centerPressWhileExpanded = false;
        BeginPressTracking(e);
    }

    private void BeginPressTracking(PointerRoutedEventArgs e)
    {
        RootGrid.CapturePointer(e.Pointer);
        _pointerDown = true;
        _dragging = false;
        _pressPoint = e.GetCurrentPoint(RootGrid).Position;
        try
        {
            _pressWindowOrigin = GetAppWindow().Position;
        }
        catch (Exception)
        {
            _pressWindowOrigin = new PointInt32(0, 0);
        }
    }

    /// <summary>True when the press landed on the bead at the window centre.
    /// Hit-testing by distance instead of by element is deliberate: the ring's
    /// inner radius follows the window size, so at small sizes the sectors
    /// creep over the bead and claim the click.</summary>
    private bool PressIsOnBead(PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point p = e.GetCurrentPoint(RootGrid).Position;
        double dx = p.X - (RootGrid.ActualWidth / 2);
        double dy = p.Y - (RootGrid.ActualHeight / 2);
        double limit = (ClosedSize / 2) + 12;
        return ((dx * dx) + (dy * dy)) <= (limit * limit);
    }

    private static bool IsSectorSource(object? source)
    {
        DependencyObject? node = source as DependencyObject;
        int guard = 0;
        while (node is not null && guard++ < 12)
        {
            if (node is XamlPath path && path.Tag is string)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        Windows.Foundation.Point current = e.GetCurrentPoint(RootGrid).Position;
        double dx = current.X - _pressPoint.X;
        double dy = current.Y - _pressPoint.Y;
        // 5 DIP of travel starts a drag: 6 felt sticky on a 56 DIP bead and made
        // the whole gesture read as a click.
        if (!_dragging && ((dx * dx) + (dy * dy)) < 25)
        {
            return;
        }

        _dragging = true;
        try
        {
            GetAppWindow().Move(new PointInt32(
                _pressWindowOrigin.X + (int)Math.Round(dx * _scale),
                _pressWindowOrigin.Y + (int)Math.Round(dy * _scale)));
        }
        catch (Exception)
        {
            // Dragging is cosmetic; never let it break the click path.
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.ReleasePointerCapture(e.Pointer);
        _pointerDown = false;

        if (_collapseHandledPress)
        {
            // This release belongs to the press that collapsed the ring; without
            // this guard the ring reopened immediately, so a second click on the
            // ball could never close it.
            _collapseHandledPress = false;
            Animate(Ball, 1.0);
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _centerPressWhileExpanded = false;
            SnapAndPersistPosition();
            Animate(Ball, 1.0);
            return;
        }

        if (_centerPressWhileExpanded)
        {
            // Click on the bead while the ring was open: collapse. The press
            // path already returned, so no sector ever saw this gesture.
            _centerPressWhileExpanded = false;
            SetExpanded(false);
            Animate(Ball, 1.0);
            return;
        }

        SetExpanded(true);
        Animate(Ball, 1.0);
    }

    private void SnapAndPersistPosition()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            PointInt32 current = appWindow.Position;
            SizeInt32 real = appWindow.Size;
            int sizeW = real.Width > 0 ? real.Width : (int)Math.Round(ClosedSize * _scale);
            int sizeH = real.Height > 0 ? real.Height : (int)Math.Round(ClosedSize * _scale);
            int margin = (int)Math.Round(ScreenMargin * _scale);
            int x = current.X;
            int y = current.Y;

            if (current.X - work.X < EdgeSnapDistance)
            {
                x = work.X + margin;
            }
            else if (work.X + work.Width - (current.X + sizeW) < EdgeSnapDistance)
            {
                x = work.X + work.Width - sizeW - margin;
            }

            if (current.Y - work.Y < EdgeSnapDistance)
            {
                y = work.Y + margin;
            }
            else if (work.Y + work.Height - (current.Y + sizeH) < EdgeSnapDistance)
            {
                y = work.Y + work.Height - sizeH - margin;
            }

            appWindow.Move(new PointInt32(x, y));

            // Persist the position of the *collapsed* window: a drag can happen
            // while the ring is open, and saving that bigger window's origin
            // made the bead reappear offset by half the ring on the next launch.
            int closed = (int)Math.Round(ClosedSize * _scale);
            _settings.X = x + ((sizeW - closed) / 2);
            _settings.Y = y + ((sizeH - closed) / 2);
            _settings.Save();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] snap failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- menu

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        SetExpanded(false);

        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var raiseAll = new MenuFlyoutItem { Text = "呼出全部格子" };
        raiseAll.Click += async (_, _) =>
        {
            await app.RaiseAllWidgetsAsync();
            UpdateStateDot();
        };

        var hideAll = new MenuFlyoutItem { Text = "收起全部格子" };
        hideAll.Click += async (_, _) =>
        {
            await app.SetAllWidgetsVisibleAsync(false);
            UpdateStateDot();
        };

        flyout.Items.Add(raiseAll);
        flyout.Items.Add(hideAll);

        var colors = new MenuFlyoutSubItem { Text = "颜色" };
        foreach (DrawerColorPreset preset in WidgetDrawerSettings.Presets)
        {
            DrawerColorPreset captured = preset;
            var item = new MenuFlyoutItem
            {
                Text = captured.Id == _settings.Color ? "✓ " + captured.Name : "　" + captured.Name
            };
            item.Click += (_, _) =>
            {
                _settings.Color = captured.Id;
                _settings.Save();
                ApplyColor();
                if (_expanded)
                {
                    BuildFan();
                }

                app.RefreshDrawerAppearance();
            };
            colors.Items.Add(item);
        }

        var panel = new MenuFlyoutItem { Text = "打开桌面管家面板" };
        panel.Click += (_, _) => app.OpenCompanionPanelFromDrawer();

        var close = new MenuFlyoutItem { Text = "关闭抽屉" };
        close.Click += (_, _) => app.CloseWidgetDrawer();

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(colors);
        flyout.Items.Add(panel);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(close);

        flyout.ShowAt(Ball, new FlyoutShowOptions { Position = e.GetPosition(Ball) });
        e.Handled = true;
    }
}
