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
/// The desktop drawer: a glass bead that opens a full-circle radial menu.
///
/// Closed, the window is 64 DIP and only the ball shows. Open, the sectors form
/// a complete ring around the ball (the "one circle" layout), so no direction
/// has to be guessed and nothing is hidden behind a screen edge: the outer
/// radius shrinks to whatever fits between the ball and the nearest edge.
///
/// The scene canvas is a fixed 460 DIP with the ball pinned to its centre; the
/// window size and the translate offset are the only things that change.
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const double SceneSize = 460;
    private const double SceneCenter = SceneSize / 2;
    private const double BallDiameter = 56;
    private const double ClosedSize = 64;

    /// <summary>Radii when the ball has room; shrunk dynamically near edges.</summary>
    private const double MaxFanOuter = 208;
    private const double MinFanOuter = 118;
    private const double FanGapDegrees = 2.2;

    private const int ScreenMargin = 18;
    private const int EdgeSnapDistance = 110;

    private readonly WidgetDrawerSettings _settings;

    private bool _expanded;
    private bool _pointerDown;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private double _scale = 1.0;
    private double _fanInner = 44;
    private double _fanOuter = MaxFanOuter;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        ApplyColor();
        LayoutScene();
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

            // Windows 11 outlines and rounds every top-level window. This window
            // is a shape, so both have to go or the desktop shows a grey box.
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

    private void LayoutScene()
    {
        Canvas.SetLeft(Ball, SceneCenter - (BallDiameter / 2));
        Canvas.SetTop(Ball, SceneCenter - (BallDiameter / 2));
        Canvas.SetLeft(BallRing, SceneCenter - 31);
        Canvas.SetTop(BallRing, SceneCenter - 31);

        // The ball is always at the canvas centre; the scene is never shifted.
        SceneShift.X = 0;
        SceneShift.Y = 0;
    }

    private SizeInt32 WindowSizePixels()
    {
        double size = _expanded ? (_fanOuter + 12) * 2 : ClosedSize;
        int pixels = (int)Math.Round(size * _scale);
        return new SizeInt32(pixels, pixels);
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

    /// <summary>Grows or shrinks the window around the ball centre.</summary>
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

        if (expanded)
        {
            ComputeFanRadius(ballCenterX, ballCenterY);
        }

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

    /// <summary>Keeps the whole ring on screen: the outer radius is capped by the
    /// distance from the ball to the nearest work-area edge.</summary>
    private void ComputeFanRadius(int ballCenterX, int ballCenterY)
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            double nearest = Math.Min(
                Math.Min(ballCenterX - work.X, work.X + work.Width - ballCenterX),
                Math.Min(ballCenterY - work.Y, work.Y + work.Height - ballCenterY));
            double available = (nearest / _scale) - 10;

            _fanOuter = Math.Clamp(available, MinFanOuter, MaxFanOuter);
            _fanInner = Math.Max(38, Math.Round(_fanOuter * 0.22));
        }
        catch (Exception)
        {
            _fanOuter = MaxFanOuter;
            _fanInner = 44;
        }
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

    /// <summary>Full 360° ring: sectors are equal slices starting at the top.</summary>
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
        double span = Math.Max(10, step - FanGapDegrees);

        for (int i = 0; i < count; i++)
        {
            (string id, string name, bool visible) = states[i];
            double mid = -90 + (step * (i + 0.5));
            double start = mid - (span / 2);
            double end = mid + (span / 2);

            var sector = new XamlPath
            {
                Data = BuildSector(start, end),
                Fill = BuildSectorBrush(mid, visible),
                Stroke = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                StrokeThickness = 1,
                Tag = id
            };

            Paint hoverFill = BuildSectorBrush(mid, true, hovered: true);
            Paint restingFill = sector.Fill;
            sector.PointerEntered += (_, _) =>
            {
                sector.Fill = hoverFill;
                sector.Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                sector.StrokeThickness = 1.4;
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
        double radius = (_fanInner + _fanOuter) / 2;
        double rad = degrees * Math.PI / 180;
        double x = SceneCenter + (radius * Math.Cos(rad));
        double y = SceneCenter + (radius * Math.Sin(rad));

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)190, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Width = 76,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(label, x - 38);
        Canvas.SetTop(label, y - 8);
        FanCanvas.Children.Add(label);

        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(visible ? (byte)255 : (byte)90, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, x - 3);
        Canvas.SetTop(dot, y + 10);
        FanCanvas.Children.Add(dot);
    }

    private Brush BuildSectorBrush(double directionDegrees, bool visible, bool hovered = false)
    {
        DrawerColorPreset preset = ResolvePreset();
        Color inner = Shade(ParseColor(preset.Start), hovered ? 1.06 : 0.70);
        Color outer = Shade(ParseColor(preset.End), hovered ? 0.74 : 0.44);
        byte alpha = hovered ? (byte)255 : visible ? (byte)250 : (byte)150;

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
        // A press that reaches this handler while the ring is open is a press on
        // empty space (sectors mark their own taps), so it closes the ring.
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
