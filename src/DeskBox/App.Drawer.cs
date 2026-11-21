using System;
using System.Threading.Tasks;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

/// <summary>
/// The widget drawer: a floating handle that collapses the whole widget layer
/// off the desktop and brings it back on demand. Widgets stay configured and
/// keep their bounds — this only changes whether they sit on the desktop, so
/// nothing has to be re-mapped or re-created.
/// </summary>
public partial class App
{
    private WidgetDrawerWindow? _widgetDrawerWindow;

    /// <summary>Shows the floating drawer handle (created on first use).</summary>
    internal void ShowWidgetDrawer()
    {
        try
        {
            if (_widgetDrawerWindow is not null)
            {
                _widgetDrawerWindow.Activate();
                return;
            }

            var window = new WidgetDrawerWindow();
            window.Closed += (_, _) => _widgetDrawerWindow = null;
            _widgetDrawerWindow = window;
            window.ShowAtDefaultSpot();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to open: {ex}");
        }
    }

    internal void CloseWidgetDrawer()
    {
        WidgetDrawerWindow? window = _widgetDrawerWindow;
        _widgetDrawerWindow = null;
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to close: {ex}");
        }
    }

    /// <summary>Startup hook: the handle is there before the user needs it.</summary>
    internal void ShowWidgetDrawerOnStartup() => ShowWidgetDrawer();

    /// <summary>
    /// One click on the handle: any widget visible → collapse them all; nothing
    /// visible → raise them back. Delegated to the same serialized tray
    /// visibility machine the tray toggle uses, so the two can never race.
    /// </summary>
    internal async Task ToggleWidgetsFromDrawerAsync()
    {
        WidgetManager? manager = WidgetManager;
        if (manager is null)
        {
            return;
        }

        bool hide = manager.HasVisibleWidgets;
        await manager.SetAllWidgetsVisibleAsync(!hide);
    }

    internal async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        WidgetManager? manager = WidgetManager;
        if (manager is null)
        {
            return;
        }

        await manager.SetAllWidgetsVisibleAsync(visible);
    }

    internal void OpenCompanionPanelFromDrawer()
    {
        CancelBackgroundMemoryCleanup();
        ShowCompanionPanelWindow();
    }
}
