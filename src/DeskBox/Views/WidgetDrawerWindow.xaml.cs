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
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Views;

/// <summary>
/// The desktop drawer: a glass bead that fans the widgets out around itself.
///
/// Geometry mirrors the reference implementation (ClassFlow / MathBoard): inner
/// radius 60, outer 206, sectors on a 180° arc pointing from the ball toward the
/// middle of the screen, 2.5° gaps, labels at mid radius. Closed state is the
/// same scene translated so only the 64 DIP ball is on screen; that keeps one
/// visual tree for both states and stops the ball from jumping when it opens.
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const double SceneSize = 460;
    private const double SceneCenter = SceneSize / 2;
    private const double BallDiameter = 56;
    private const double ClosedSize = 64;

    private const double FanInner = 44;
    private const double FanOuter = 210;
    private const double FanGapDegrees = 2.5;

    private const int ScreenMargin = 18;
    private const int EdgeSnapDistance = 110;

    private readonly WidgetDrawerSettings _settings;

    private bool _expanded;
    private bool _pointerDown;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private double _scale = 1.0;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        ApplyColor();
        LayoutScene();

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
        ApplyChrome();
        RootGrid.Loaded += (_, _) => UpdateStateDot();
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

            // Windows 11 draws a 1px outline (and rounds corners) around any
            // top-level window, which showed up as a grey rectangle floating on
            // the desktop. This window is a shape, not a window: kill both.
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            int noBorder = -2; // DWMWA_COLOR_NONE
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref noBorder);
            int doNotRound = 1; // DWMWCP_DONOTROUND
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] chrome setup failed: {ex.Message}");
        }

        // Per-pixel transparency: without it every unpainted pixel of the scene
        // is black, which is exactly the "black shape with a frame" artifact.
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

    /// <summary>Places the ball at the scene centre and translates the scene for
    /// the current state.</summary>
    private void LayoutScene()
    {
        Canvas.SetLeft(Ball, SceneCenter - (BallDiameter / 2));
        Canvas.SetTop(Ball, SceneCenter - (BallDiameter / 2));
        Canvas.SetLeft(BallRing, SceneCenter - 31);
        Canvas.SetTop(BallRing, SceneCenter - 31);

        // No translation: the scene is always centred on the ball, and the
        // window size alone decides how much of it is on screen. Translating it
        // was what pushed the ball off-centre.
        SceneShift.X = 0;
        SceneShift.Y = 0;
    }

    private SizeInt32 WindowSizePixels() => new(
        (int)Math.Round((_expanded ? SceneSize : ClosedSize) * _scale),
        (int)Math.Round((_expanded ? SceneSize : ClosedSize) * _scale));

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
            appWindow.Resize(WindowSizePixels());
            appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] positioning failed: {ex.Message}");
        }

        Activate();
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Grows/shrinks the window around the ball centre.</summary>
    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        AppWindow appWindow = GetAppWindow();
        PointInt32 current = appWindow.Position;
        int ballCenterX = current.X + (int)Math.Round((ClosedSize / 2) * _scale);
        int ballCenterY = current.Y + (int)Math.Round((ClosedSize / 2) * _scale);

        _expanded = expanded;
        LayoutScene();

        SizeInt32 size = WindowSizePixels();
        int half = size.Width / 2;
        try
        {
            appWindow.Resize(size);
            appWindow.Move(new PointInt32(ballCenterX - half, ballCenterY - half));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] resize failed: {ex.Message}");
        }

        if (expanded)
        {
            BuildFan();
        }
        else
        {
            FanCanvas.Children.Clear();
        }

        AnimateFan(expanded ? 1 : 0);
    }

    // ------------------------------------------------------------- appearance

    private void ApplyColor()
    {
        DrawerColorPreset preset = ResolvePreset();

        Ball.Background = new LinearGradientBrush
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
        Ball.BorderBrush = new SolidColorBrush(Color.FromArgb(190, rim.R, rim.G, rim.B));
        BallRing.BorderBrush = new SolidColorBrush(Color.FromArgb(70, rim.R, rim.G, rim.B));
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

    private void AnimateFan(double to)
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
            Storyboard.SetTarget(fade, FanCanvas);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] fan fade failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------- fan

    /// <summary>Direction the fan opens: from the ball toward the screen centre,
    /// snapped to 45° so it always looks deliberate.</summary>
    private double FanDirection()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            PointInt32 position = appWindow.Position;
            int half = (int)Math.Round((SceneSize / 2) * _scale);

            double dx = (work.X + (work.Width / 2.0)) - (position.X + half);
            double dy = (work.Y + (work.Height / 2.0)) - (position.Y + half);
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
            {
                return -90;
            }

            double degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
            return Math.Round(degrees / 45.0) * 45.0;
        }
        catch (Exception)
        {
            return -90;
        }
    }

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

        double direction = FanDirection();
        int count = states.Count;
        double step = 180.0 / count;
        double span = Math.Max(10, step - FanGapDegrees);

        for (int i = 0; i < count; i++)
        {
            (string id, string name, bool visible) = states[i];
            double mid = direction - 90 + (step * (i + 0.5));
            double start = mid - (span / 2);
            double end = mid + (span / 2);

            var sector = new XamlPath
            {
                Data = BuildSector(start, end),
                Fill = BuildSectorBrush(direction, visible),
                Stroke = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
                StrokeThickness = 1,
                Tag = id
            };

            var restingFill = sector.Fill;
            var hoverFill = BuildSectorBrush(direction, true, hovered: true);
            sector.PointerEntered += (_, _) =>
            {
                sector.Fill = hoverFill;
                sector.Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                sector.StrokeThickness = 1.4;
            };
            sector.PointerExited += (_, _) =>
            {
                sector.Fill = restingFill;
                sector.Stroke = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));
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

        // Outer rim highlight: the reference fan is closed by a single light arc,
        // which is what stops it from looking like loose wedges.
        var rim = new XamlPath
        {
            Data = BuildOuterArc(direction - 90, direction + 90 - FanGapDegrees),
            Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            StrokeThickness = 1.2,
            IsHitTestVisible = false
        };
        FanCanvas.Children.Add(rim);
    }

    private static Geometry BuildOuterArc(double a0, double a1)
    {
        var figure = new PathFigure { StartPoint = PointAt(FanOuter, a0), IsClosed = false, IsFilled = false };
        bool large = Math.Abs(a1 - a0) > 180;
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(FanOuter, a1),
            Size = new Windows.Foundation.Size(FanOuter, FanOuter),
            IsLargeArc = large,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void AddSectorLabel(double degrees, string text, bool visible)
    {
        double radius = (FanInner + FanOuter) / 2;
        double rad = degrees * Math.PI / 180;

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)190, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Width = 78,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(label, SceneCenter + (radius * Math.Cos(rad)) - 39);
        Canvas.SetTop(label, SceneCenter + (radius * Math.Sin(rad)) - 8);
        FanCanvas.Children.Add(label);

        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)90, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, SceneCenter + (radius * Math.Cos(rad)) - 3);
        Canvas.SetTop(dot, SceneCenter + (radius * Math.Sin(rad)) + 10);
        FanCanvas.Children.Add(dot);
    }

    private Brush BuildSectorBrush(double directionDegrees, bool visible, bool hovered = false)
    {
        DrawerColorPreset preset = ResolvePreset();
        Color inner = Shade(ParseColor(preset.Start), hovered ? 1.05 : 0.72);
        Color outer = Shade(ParseColor(preset.End), hovered ? 0.72 : 0.46);
        byte alpha = hovered ? (byte)255 : visible ? (byte)248 : (byte)150;

        // Gradient runs along the fan direction: light at the inner edge, deep at
        // the rim — the same read as the reference fan.
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

    /// <summary>Darkens a colour toward the reference fan: sectors read as deep
    /// glass, not as bright paint.</summary>
    private static Color Shade(Color color, double factor)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * factor, 0, 255),
            (byte)Math.Clamp(color.G * factor, 0, 255),
            (byte)Math.Clamp(color.B * factor, 0, 255));
    }

    private static Geometry BuildSector(double a0, double a1)
    {
        Windows.Foundation.Point innerStart = PointAt(FanInner, a0);
        Windows.Foundation.Point outerStart = PointAt(FanOuter, a0);
        Windows.Foundation.Point outerEnd = PointAt(FanOuter, a1);
        Windows.Foundation.Point innerEnd = PointAt(FanInner, a1);

        var figure = new PathFigure
        {
            StartPoint = innerStart,
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment { Point = outerStart });
        figure.Segments.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Windows.Foundation.Size(FanOuter, FanOuter),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = innerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point = innerStart,
            Size = new Windows.Foundation.Size(FanInner, FanInner),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Windows.Foundation.Point PointAt(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Windows.Foundation.Point(
            SceneCenter + (radius * Math.Cos(rad)),
            SceneCenter + (radius * Math.Sin(rad)));
    }

    // ---------------------------------------------------------------- pointer

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateBallOpacity(1.0);
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging && !_expanded)
        {
            AnimateBallOpacity(0.55);
        }
    }

    private void AnimateBallOpacity(double to)
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, Ball);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] ball fade failed: {ex.Message}");
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A press while the fan is open means "close it": the fan canvas covers
        // the whole scene, so anything reaching this handler is empty space.
        if (_expanded)
        {
            SetExpanded(false);
            e.Handled = true;
            return;
        }

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

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        Windows.Foundation.Point current = e.GetCurrentPoint(RootGrid).Position;
        double dx = current.X - _pressPoint.X;
        double dy = current.Y - _pressPoint.Y;
        if (!_dragging && Math.Abs(dx) < 6 && Math.Abs(dy) < 6)
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

        if (_dragging)
        {
            _dragging = false;
            SnapAndPersistPosition();
            AnimateBallOpacity(1.0);
            return;
        }

        SetExpanded(true);
        AnimateBallOpacity(1.0);
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
            int size = (int)Math.Round(ClosedSize * _scale);
            int margin = (int)Math.Round(ScreenMargin * _scale);
            int x = current.X;
            int y = current.Y;

            if (current.X - work.X < EdgeSnapDistance)
            {
                x = work.X + margin;
            }
            else if (work.X + work.Width - (current.X + size) < EdgeSnapDistance)
            {
                x = work.X + work.Width - size - margin;
            }

            if (current.Y - work.Y < EdgeSnapDistance)
            {
                y = work.Y + margin;
            }
            else if (work.Y + work.Height - (current.Y + size) < EdgeSnapDistance)
            {
                y = work.Y + work.Height - size - margin;
            }

            appWindow.Move(new PointInt32(x, y));
            _settings.X = x;
            _settings.Y = y;
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
            string label = captured.Id == _settings.Color ? "✓ " + captured.Name : "　" + captured.Name;
            var item = new MenuFlyoutItem { Text = label };
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
