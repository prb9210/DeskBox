using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
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

namespace DeskBox.Views;

/// <summary>
/// The drawer handle: a 56px glass ball in the corner of the desktop. One click
/// parks every widget, the next brings them back; the ball itself carries the
/// colour the user picked and stays out of the taskbar and Alt-Tab.
///
/// Shape is a real circle: the HWND is clipped with an elliptic region, because
/// a borderless WinUI window is otherwise a rounded-corner square.
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const int WindowSize = 68;
    private const int BallSize = 56;
    private const int ScreenMargin = 20;
    private const int EdgeSnapDistance = 110;
    private const double IdleOpacity = 0.45;
    private const double HoverOpacity = 1.0;

    private readonly WidgetDrawerSettings _settings;

    private bool _pointerDown;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private DispatcherTimer? _tapDelayTimer;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        ApplyColor();
        ApplyChrome();

        Loaded += (_, _) => UpdateStateDot();
    }

    /// <summary>Hands the HWND to whoever needs to register a hotkey on it.</summary>
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

            appWindow.Resize(new SizeInt32(WindowSize, WindowSize));
            AppBranding.ApplyWindowIcon(appWindow);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] window chrome setup failed: {ex}");
        }

        if (WindowsCompatibilityService.ApplySafeBackdrop(this) != "Solid")
        {
            RootGrid.Background = null;
        }

        ClipToCircle();
    }

    private AppWindow GetAppWindow()
    {
        IntPtr hWnd = WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
    }

    /// <summary>Elliptic window region → a genuine circle on any backdrop.</summary>
    private void ClipToCircle()
    {
        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int diameter = Math.Max(16, (int)Math.Round(WindowSize * scale));
            IntPtr region = CreateEllipticRgn(0, 0, diameter + 1, diameter + 1);
            if (region == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(hWnd, region, true) == 0)
            {
                DeleteObject(region);
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
            preset = ResolveThemePreset();
        }

        Ball.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.15, 0.0),
            EndPoint = new Windows.Foundation.Point(0.85, 1.0),
            GradientStops =
            {
                new GradientStop { Color = ParseColor(preset.Start), Offset = 0 },
                new GradientStop { Color = ParseColor(preset.End), Offset = 1 }
            }
        };

        // The rim is the preset's light tone at reduced alpha: every colour then
        // reads as the same glass bead instead of eight different buttons.
        Color rim = ParseColor(preset.Rim);
        Ball.BorderBrush = new SolidColorBrush(Color.FromArgb(170, rim.R, rim.G, rim.B));
    }

    private static DrawerColorPreset ResolveThemePreset()
    {
        // Light theme needs a deeper ball so white glyphs keep contrast.
        bool lightTheme = Application.Current.RequestedTheme == ApplicationTheme.Light;
        return WidgetDrawerSettings.Find(lightTheme ? "blue" : "cyan") ?? WidgetDrawerSettings.Presets[0];
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

    /// <summary>Re-reads colours and state, e.g. after the tray changed them.</summary>
    public void Refresh()
    {
        ApplyColor();
        UpdateStateDot();
    }

    private void AnimateTo(double opacity, double scale)
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = opacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, Ball);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var scaleX = new DoubleAnimation
            {
                To = scale,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleX, BallScale);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");

            var scaleY = new DoubleAnimation
            {
                To = scale,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleY, BallScale);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            storyboard.Children.Add(fade);
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] animation failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- position

    public void ShowAtSavedSpot()
    {
        AppWindow appWindow = GetAppWindow();
        RectInt32 work = DisplayArea
            .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        int x = work.X + work.Width - WindowSize - ScreenMargin;
        int y = work.Y + work.Height - WindowSize - ScreenMargin;

        if (_settings.X is double savedX && _settings.Y is double savedY)
        {
            x = Clamp((int)savedX, work.X, work.X + work.Width - WindowSize);
            y = Clamp((int)savedY, work.Y, work.Y + work.Height - WindowSize);
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
            int x = current.X;
            int y = current.Y;

            if (current.X - work.X < EdgeSnapDistance)
            {
                x = work.X + ScreenMargin;
            }
            else if (work.X + work.Width - (current.X + WindowSize) < EdgeSnapDistance)
            {
                x = work.X + work.Width - WindowSize - ScreenMargin;
            }

            if (current.Y - work.Y < EdgeSnapDistance)
            {
                y = work.Y + ScreenMargin;
            }
            else if (work.Y + work.Height - (current.Y + WindowSize) < EdgeSnapDistance)
            {
                y = work.Y + work.Height - WindowSize - ScreenMargin;
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

    // ----------------------------------------------------------------- pointer

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateTo(HoverOpacity, 1.06);
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            AnimateTo(IdleOpacity, 1.0);
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
        if (!_dragging && (Math.Abs(dx) < 6 && Math.Abs(dy) < 6))
        {
            return;
        }

        _dragging = true;
        CancelPendingTap();
        try
        {
            GetAppWindow().Move(new PointInt32(
                _pressWindowOrigin.X + (int)Math.Round(dx),
                _pressWindowOrigin.Y + (int)Math.Round(dy)));
        }
        catch (Exception)
        {
            // Moving is cosmetic; a failed move must never break the click path.
        }
    }

    private async void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.ReleasePointerCapture(e.Pointer);
        _pointerDown = false;
        if (_dragging)
        {
            _dragging = false;
            SnapAndPersistPosition();
            AnimateTo(HoverOpacity, 1.06);
            return;
        }

        // Delay the toggle just long enough for a second click to arrive, so a
        // double-click opens the panel instead of parking twice.
        CancelPendingTap();
        _tapDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _tapDelayTimer.Tick += async (_, _) =>
        {
            CancelPendingTap();
            if (Application.Current is App app)
            {
                await app.ToggleWidgetsFromDrawerAsync();
                UpdateStateDot();
            }
        };
        _tapDelayTimer.Start();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void CancelPendingTap()
    {
        _tapDelayTimer?.Stop();
        _tapDelayTimer = null;
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        CancelPendingTap();
    }

    // ------------------------------------------------------------------- menu

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            return;
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

        IReadOnlyList<(string Id, string Name)> widgets = app.DrawerWidgetChoices();
        if (widgets.Count > 0)
        {
            var parkMenu = new MenuFlyoutSubItem { Text = "收起某个格子" };
            var raiseMenu = new MenuFlyoutSubItem { Text = "呼出某个格子" };
            foreach ((string id, string name) in widgets)
            {
                string capturedId = id;
                string capturedName = name;

                var park = new MenuFlyoutItem { Text = capturedName };
                park.Click += async (_, _) =>
                {
                    await app.CollapseWidgetAsync(capturedId);
                    UpdateStateDot();
                };
                parkMenu.Items.Add(park);

                var raise = new MenuFlyoutItem { Text = capturedName };
                raise.Click += async (_, _) =>
                {
                    await app.ExpandWidgetAsync(capturedId);
                    UpdateStateDot();
                };
                raiseMenu.Items.Add(raise);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(parkMenu);
            flyout.Items.Add(raiseMenu);
        }

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
