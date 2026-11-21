using System;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// The "drawer" handle: a small always-on-top tile in the bottom-right corner
/// of the work area. One click collapses every widget off the desktop, the next
/// click brings them back — the desktop stops being a wall of permanent windows
/// while the widgets stay one gesture away.
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const int WindowSize = 88;
    private const int ScreenMargin = 24;

    public WidgetDrawerWindow()
    {
        InitializeComponent();
        Title = "格子抽屉";

        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
            // Presenter + switcher state must route through WindowShellState:
            // early-logon sessions reject the raw calls with E_NOTIMPL.
            WindowShellState.TryApplyBorderlessOverlappedPresenter(appWindow);
            // A helper handle, not a document: keep it out of Alt-Tab and the
            // taskbar so it can never be lost behind other windows' entries.
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
    }

    /// <summary>Puts the tile in the bottom-right corner of the primary work area.</summary>
    public void ShowAtDefaultSpot()
    {
        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
            DisplayArea area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            appWindow.Move(new PointInt32(
                work.X + work.Width - WindowSize - ScreenMargin,
                work.Y + work.Height - WindowSize - ScreenMargin));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] positioning failed: {ex}");
        }

        Activate();
    }

    private async void DrawerButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            await app.ToggleWidgetsFromDrawerAsync();
        }
    }

    private void DrawerButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var showAll = new MenuFlyoutItem { Text = "呼出全部格子" };
        showAll.Click += async (_, _) =>
        {
            if (Application.Current is App app)
            {
                await app.SetAllWidgetsVisibleAsync(true);
            }
        };

        var hideAll = new MenuFlyoutItem { Text = "收起全部格子" };
        hideAll.Click += async (_, _) =>
        {
            if (Application.Current is App app)
            {
                await app.SetAllWidgetsVisibleAsync(false);
            }
        };

        var panel = new MenuFlyoutItem { Text = "打开桌面管家面板" };
        panel.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.OpenCompanionPanelFromDrawer();
            }
        };

        var close = new MenuFlyoutItem { Text = "关闭抽屉" };
        close.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.CloseWidgetDrawer();
            }
        };

        flyout.Items.Add(showAll);
        flyout.Items.Add(hideAll);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(panel);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(close);
        flyout.ShowAt(DrawerButton, e.GetPosition(DrawerButton));
        e.Handled = true;
    }
}
