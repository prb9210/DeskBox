using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

/// <summary>
/// The widget drawer: a glass ball that parks the whole widget layer, plus the
/// per-widget park/raise bookkeeping and the Ctrl+Alt+Space chord.
///
/// Parked state lives in drawer.json, never in the settings' IsVisible flag:
/// that flag is what the tray raise/hide pass manipulates, and two owners of one
/// flag is exactly how widgets end up reappearing after a restart.
/// </summary>
public partial class App
{
    private WidgetDrawerWindow? _widgetDrawerWindow;
    private DrawerHotkeyService? _drawerHotkey;

    internal WidgetDrawerSettings DrawerSettings { get; } = WidgetDrawerSettings.Load();

    /// <summary>True when no widget is currently on the desktop.</summary>
    internal bool WidgetsParked => WidgetManager is { } manager && !manager.HasVisibleWidgets;

    internal IReadOnlyList<(string Id, string Name)> DrawerWidgetChoices() =>
        WidgetManager?.DrawerWidgetChoices() ?? Array.Empty<(string Id, string Name)>();

    internal IReadOnlyList<(string Id, string Name, bool Visible)> DrawerWidgetStates() =>
        WidgetManager?.WidgetStates() ?? Array.Empty<(string Id, string Name, bool Visible)>();

    /// <summary>Fan sector click: flip one widget between shown and parked.</summary>
    internal async Task ToggleWidgetAsync(string widgetId)
    {
        bool visible = false;
        foreach ((string id, string name, bool isVisible) in DrawerWidgetStates())
        {
            if (string.Equals(id, widgetId, StringComparison.Ordinal))
            {
                visible = isVisible;
                _ = name;
                break;
            }
        }

        if (visible)
        {
            await CollapseWidgetAsync(widgetId);
        }
        else
        {
            await ExpandWidgetAsync(widgetId);
        }
    }

    /// <summary>Shows the floating drawer (created on first use).</summary>
    internal void ShowWidgetDrawer()
    {
        try
        {
            if (_widgetDrawerWindow is not null)
            {
                _widgetDrawerWindow.Activate();
                return;
            }

            var window = new WidgetDrawerWindow(DrawerSettings);
            window.Closed += (_, _) =>
            {
                _widgetDrawerWindow = null;
                _drawerHotkey?.Detach();
                _drawerHotkey = null;
            };

            _widgetDrawerWindow = window;
            window.ShowAtSavedSpot();
            RegisterDrawerHotkey(window.WindowHandle);
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to open: {ex}");
        }
    }

    private void RegisterDrawerHotkey(IntPtr windowHandle)
    {
        try
        {
            _drawerHotkey ??= new DrawerHotkeyService(() => _ = ToggleWidgetsFromDrawerAsync());
            if (!_drawerHotkey.TryRegister(windowHandle, out string? error) &&
                !string.IsNullOrEmpty(error))
            {
                Log($"[Drawer] hotkey unavailable: {error}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Drawer] hotkey setup failed: {ex}");
        }
    }

    internal void RefreshDrawerAppearance() => _widgetDrawerWindow?.Refresh();

    internal void CloseWidgetDrawer()
    {
        WidgetDrawerWindow? window = _widgetDrawerWindow;
        _widgetDrawerWindow = null;
        _drawerHotkey?.Detach();
        _drawerHotkey = null;
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to close: {ex}");
        }
    }

    /// <summary>Startup hook: the handle is there before the user needs it, and
    /// the widgets parked last session stay off the desktop.</summary>
    internal void ShowWidgetDrawerOnStartup()
    {
        ShowWidgetDrawer();
        _ = ApplyParkedWidgetsAsync();
    }

    private async Task ApplyParkedWidgetsAsync()
    {
        if (DrawerSettings.CollapsedWidgetIds.Count == 0 || WidgetManager is not { } manager)
        {
            return;
        }

        foreach (string widgetId in DrawerSettings.CollapsedWidgetIds.ToArray())
        {
            try
            {
                await manager.SetWidgetVisibleAsync(widgetId, false);
            }
            catch (Exception ex)
            {
                Log($"[Drawer] restoring parked widget {widgetId} failed: {ex}");
            }
        }

        RefreshDrawerAppearance();
    }

    /// <summary>One click on the ball: anything visible → park all; nothing
    /// visible → raise everything (and forget the parked list, because "raise
    /// all" means all).</summary>
    internal async Task ToggleWidgetsFromDrawerAsync()
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        if (manager.HasVisibleWidgets)
        {
            await manager.SetAllWidgetsVisibleAsync(false);
        }
        else
        {
            await RaiseAllWidgetsAsync();
        }

        RefreshDrawerAppearance();
    }

    internal async Task RaiseAllWidgetsAsync()
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        DrawerSettings.CollapsedWidgetIds.Clear();
        DrawerSettings.Save();
        await manager.SetAllWidgetsVisibleAsync(true);
        RefreshDrawerAppearance();
    }

    internal async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        await manager.SetAllWidgetsVisibleAsync(visible);
        RefreshDrawerAppearance();
    }

    internal async Task CollapseWidgetAsync(string widgetId)
    {
        DrawerSettings.SetCollapsed(widgetId, true);
        DrawerSettings.Save();
        if (WidgetManager is { } manager)
        {
            await manager.SetWidgetVisibleAsync(widgetId, false);
        }

        RefreshDrawerAppearance();
    }

    internal async Task ExpandWidgetAsync(string widgetId)
    {
        DrawerSettings.SetCollapsed(widgetId, false);
        DrawerSettings.Save();
        if (WidgetManager is { } manager)
        {
            await manager.SetWidgetVisibleAsync(widgetId, true);
        }

        RefreshDrawerAppearance();
    }

    internal void OpenCompanionPanelFromDrawer()
    {
        CancelBackgroundMemoryCleanup();
        ShowCompanionPanelWindow();
    }
}
