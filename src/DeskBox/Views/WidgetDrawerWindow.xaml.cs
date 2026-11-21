using System;
using System.Collections.Generic;
using System.Linq;
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
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// The desktop drawer: a 56px glass bead in a corner of the screen. Clicking it
/// fans out one sector per widget; a sector toggles that single widget, so the
/// user can keep the weather and the diary out and park the rest.
///
/// Two rendering rules make or break the look:
///   * the window IS the ball — content bleeds to the edges, because the window
///     region is an ellipse and any margin lets the black HWND background show;
///   * every size is multiplied by the monitor scale, because AppWindow.Resize
///     takes physical pixels while the XAML tree is laid out in DIPs.
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const int BallDip = 56;
    private const int ScreenMargin = 18;
    private const int EdgeSnapDistance = 110;
    private const double IdleOpacity = 0.5;
    private const double HoverOpacity = 0.95;

    private const double FanCanvasSize = 340;
    private const double FanInner = 44;
    private const double FanOuter = 148;

    private readonly WidgetDrawerSettings _settings;

    private bool _pointerDown;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private Flyout? _fan;
    private double _scale = 1.0;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        ApplyColor();
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
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] chrome setup failed: {ex.Message}");
        }

        // No backdrop: the region clip supplies the silhouette, and the ball
        // border paints every pixel inside it.
        try
        {
            SystemBackdrop = null;
        }
        catch (Exception)
        {
            // Older builds may not support clearing the backdrop.
        }

        ApplyScale();
    }

    /// <summary>Sizes the HWND to the ball in physical pixels and re-clips it.</summary>
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

        int pixels = (int)Math.Round(BallDip * _scale);
        try
        {
            GetAppWindow().Resize(new SizeInt32(pixels, pixels));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] resize failed: {ex.Message}");
        }

        ClipToCircle(pixels);
    }

    /// <summary>Elliptic window region → a genuine circle on any backdrop.</summary>
    private void ClipToCircle(int pixels)
    {
        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            int diameter = Math.Max(16, pixels);
            IntPtr region = Win32Helper.CreateEllipticRgn(0, 0, diameter + 1, diameter + 1);
            if (region == IntPtr.Zero)
            {
                return;
            }

            if (Win32Helper.SetWindowRgn(hWnd, region, true) == 0)
            {
                Win32Helper.DeleteObject(region);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] circular clip failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- appearance

    private void ApplyColor()
    {
        DrawerColorPreset preset = _settings.ResolveColor();
        if (preset.Id == WidgetDrawerSettings.FollowThemeId)
        {
            bool light = Application.Current.RequestedTheme == ApplicationTheme.Light;
            preset = WidgetDrawerSettings.Find(light ? "blue" : "cyan") ?? WidgetDrawerSettings.Presets[0];
        }

        Ball.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.12, 0.0),
            EndPoint = new Windows.Foundation.Point(0.88, 1.0),
            GradientStops =
            {
                new GradientStop { Color = ParseColor(preset.Start), Offset = 0 },
                new GradientStop { Color = ParseColor(preset.End), Offset = 1 }
            }
        };

        Color rim = ParseColor(preset.Rim);
        Ball.BorderBrush = new SolidColorBrush(Color.FromArgb(200, rim.R, rim.G, rim.B));
    }

    private static Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 6 &&
            byte.TryParse(value.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
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
            App.Log($"[Drawer] fade failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- position

    public void ShowAtSavedSpot()
    {
        ApplyScale();

        AppWindow appWindow = GetAppWindow();
        RectInt32 work = DisplayArea
            .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        int size = (int)Math.Round(BallDip * _scale);
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
            appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] positioning failed: {ex.Message}");
        }

        Activate();
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private void SnapAndPersistPosition()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            PointInt32 current = appWindow.Position;
            int size = (int)Math.Round(BallDip * _scale);
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

    // ------------------------------------------------------------------ fan

    /// <summary>Fans one sector per widget, opening away from the nearest edge.</summary>
    private async void ToggleFanAsync()
    {
        if (_fan is not null)
        {
            _fan.Hide();
            _fan = null;
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        bool openUpward = IsInLowerHalf();

        var canvas = new Canvas
        {
            Width = FanCanvasSize,
            Height = FanCanvasSize,
            Background = new SolidColorBrush(Colors.Transparent)
        };

        IReadOnlyList<(string Id, string Name, bool Visible)> states = app.DrawerWidgetStates();
        int count = states.Count;
        if (count == 0)
        {
            return;
        }

        double center = FanCanvasSize / 2;
        double baseAngle = openUpward ? -90 : 90;
        double step = 180.0 / count;
        double span = Math.Max(8, step - 3);

        for (int i = 0; i < count; i++)
        {
            (string id, string name, bool visible) = states[i];
            double mid = baseAngle - 90 + step * (i + 0.5);
            double a0 = mid - span / 2;
            double a1 = mid + span / 2;

            var sector = new XamlPath
            {
                Data = BuildSector(center, FanInner, FanOuter, a0, a1),
                Fill = BuildSectorBrush(visible),
                Stroke = new SolidColorBrush(Color.FromArgb(visible ? (byte)90 : (byte)45, 255, 255, 255)),
                StrokeThickness = 1
            };

            string widgetId = id;
            sector.Tapped += async (_, args) =>
            {
                args.Handled = true;
                await app.ToggleWidgetAsync(widgetId);
                if (_fan is not null)
                {
                    _fan.Hide();
                    _fan = null;
                    ToggleFanAsync();
                }
            };

            canvas.Children.Add(sector);

            double labelRadius = (FanInner + FanOuter) / 2;
            double rad = mid * Math.PI / 180;
            var label = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Width = 62,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(label, center + labelRadius * Math.Cos(rad) - 31);
            Canvas.SetTop(label, center + labelRadius * Math.Sin(rad) - 9);
            canvas.Children.Add(label);

            if (visible)
            {
                var dot = new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Colors.White),
                    IsHitTestVisible = false,
                    Opacity = 0.85
                };
                Canvas.SetLeft(dot, center + labelRadius * Math.Cos(rad) - 3);
                Canvas.SetTop(dot, center + labelRadius * Math.Sin(rad) + 8);
                canvas.Children.Add(dot);
            }
        }

        var flyout = new Flyout
        {
            Placement = openUpward ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false,
            Content = canvas
        };

        try
        {
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            flyout.FlyoutPresenterStyle = style;
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] fan style failed: {ex.Message}");
        }

        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_fan, flyout))
            {
                _fan = null;
            }
        };

        _fan = flyout;
        flyout.ShowAt(RootGrid);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private bool IsInLowerHalf()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            return appWindow.Position.Y + (BallDip * _scale) / 2 > work.Y + work.Height / 2;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static Geometry BuildSector(double center, double inner, double outer, double a0, double a1)
    {
        Windows.Foundation.Point InnerStart = PointAt(center, inner, a0);
        Windows.Foundation.Point OuterStart = PointAt(center, outer, a0);
        Windows.Foundation.Point OuterEnd = PointAt(center, outer, a1);
        Windows.Foundation.Point InnerEnd = PointAt(center, inner, a1);

        var figure = new PathFigure
        {
            StartPoint = InnerStart,
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment { Point = OuterStart });
        figure.Segments.Add(new ArcSegment
        {
            Point = OuterEnd,
            Size = new Windows.Foundation.Size(outer, outer),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = InnerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point = InnerStart,
            Size = new Windows.Foundation.Size(inner, inner),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Counterclockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Windows.Foundation.Point PointAt(double center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Windows.Foundation.Point(center + radius * Math.Cos(rad), center + radius * Math.Sin(rad));
    }

    private Brush BuildSectorBrush(bool visible)
    {
        DrawerColorPreset preset = _settings.ResolveColor();
        if (preset.Id == WidgetDrawerSettings.FollowThemeId)
        {
            bool light = Application.Current.RequestedTheme == ApplicationTheme.Light;
            preset = WidgetDrawerSettings.Find(light ? "blue" : "cyan") ?? WidgetDrawerSettings.Presets[0];
        }

        Color start = ParseColor(preset.Start);
        Color end = ParseColor(preset.End);
        byte alpha = visible ? (byte)235 : (byte)90;

        return new SolidColorBrush(visible
            ? Color.FromArgb(alpha, start.R, start.G, start.B)
            : Color.FromArgb(alpha, end.R, end.G, end.B));
    }

    // ----------------------------------------------------------------- pointer

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateBallOpacity(HoverOpacity);
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            AnimateBallOpacity(IdleOpacity);
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
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
            AnimateBallOpacity(HoverOpacity);
            return;
        }

        ToggleFanAsync();
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OpenCompanionPanelFromDrawer();
        }
    }

    // ------------------------------------------------------------------- menu

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        if (_fan is not null)
        {
            _fan.Hide();
            _fan = null;
        }

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
