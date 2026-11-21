using System;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Hosts <see cref="CompanionSettingsSection"/> inside the same window chrome
/// DeskBox uses everywhere else (48px title bar, branded icon, Mica/Acrylic
/// backdrop), so the organizer stops looking like a bolted-on web page.
/// </summary>
public sealed partial class CompanionPanelWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public CompanionPanelWindow()
    {
        InitializeComponent();
        Title = "桌面管家";

        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
            appWindow.Resize(new SizeInt32(960, 880));
            AppBranding.ApplyWindowIcon(appWindow);
        }
        catch (Exception)
        {
            // Sizing/branding are cosmetic; never block the panel on them.
        }

        // Same chrome contract as the desktop-organization window: extend into
        // the title bar, keep the solid brush only when no backdrop is available.
        if (WindowsCompatibilityService.ApplySafeBackdrop(this) != "Solid")
        {
            RootGrid.Background = null;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _timer.Tick += async (_, _) => await Section.RefreshStatusAsync();
        Closed += (_, _) => _timer.Stop();
    }

    public void ShowWindow()
    {
        Activate();
        _timer.Start();
        _ = Section.RefreshStatusAsync();
    }
}
