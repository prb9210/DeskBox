using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Dispatching;

namespace DeskBox;

/// <summary>
/// Companion-engine glue: starts the bundled organizer and, on the very first
/// launch after installation, maps the shared library's four categories as file
/// widgets so the user never has to add them by hand.
/// </summary>
public partial class App
{
    private static readonly string[] CompanionLibraryFolders =
    {
        "01 数学教学",
        "02 教务考务",
        "03 控辍保学",
        "04 上级文件",
        "05 班级管理",
        "06 家校沟通",
        "07 教研与进修",
        "08 行政与事务",
    };

    private void StartCompanionHostAndMapLibrary()
    {
        CompanionProcessHost.StartIfPresent();
        TryQueueCompanionLibraryWidgets();
    }

    private CompanionPanelWindow? _companionPanelWindow;

    /// <summary>
    /// Opens the in-app organizer panel (status + 立即整理/同步/备份 + 同步周期),
    /// so the engine's web dashboard is optional rather than the only way in.
    /// </summary>
    public void ShowCompanionPanelWindow()
    {
        CancelBackgroundMemoryCleanup();
        if (_companionPanelWindow is null)
        {
            var window = new CompanionPanelWindow();
            _companionPanelWindow = window;
            ThemeService.TrackWindow(window);
            window.Closed += CompanionPanelWindow_Closed;
        }

        _companionPanelWindow.ShowWindow();
    }

    private void CompanionPanelWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not CompanionPanelWindow window)
        {
            return;
        }

        window.Closed -= CompanionPanelWindow_Closed;
        if (ReferenceEquals(_companionPanelWindow, window))
        {
            _companionPanelWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: false);
    }

    /// <summary>
    /// Lays the four library widgets out in a tidy grid. Runs before the widget
    /// windows are restored, so the stored rects win and nothing has to be
    /// dragged by hand.
    /// </summary>
    private void ArrangeCompanionWidgets()
    {
        try
        {
            string companionDirectory = Path.Combine(AppContext.BaseDirectory, "companion");
            string configPath = Path.Combine(companionDirectory, "config.json");
            string markerPath = Path.Combine(companionDirectory, "widgets.arranged");
            if (!File.Exists(configPath) || File.Exists(markerPath))
            {
                return;
            }

            string library = ReadCompanionLibraryPath(configPath);
            if (string.IsNullOrWhiteSpace(library))
            {
                return;
            }

            string root = library.TrimEnd('\\') + "\\";
            var targets = new List<WidgetConfig>();
            foreach (WidgetConfig widget in SettingsService.Settings.WidgetLayout.Widgets)
            {
                string? mapped = widget.MappedFolderPath;
                if (!string.IsNullOrWhiteSpace(mapped) &&
                    mapped!.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(widget);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            const double width = 356;
            const double height = 428;
            const double gap = 18;
            const double margin = 40;

            double screenWidth = 1920;
            double screenHeight = 1040;
            var screens = WidgetScreenCatalog.Capture();
            if (screens.Count > 0)
            {
                screenWidth = screens[0].WorkArea.Width;
                screenHeight = screens[0].WorkArea.Height;
            }

            int perRow = Math.Max(1, (int)((screenWidth - (margin * 2) + gap) / (width + gap)));
            if (perRow > targets.Count)
            {
                perRow = targets.Count;
            }

            int rows = (int)Math.Ceiling(targets.Count / (double)perRow);
            double totalWidth = (perRow * width) + ((perRow - 1) * gap);
            double totalHeight = (rows * height) + ((rows - 1) * gap);
            double startX = Math.Max(margin, (screenWidth - totalWidth) / 2);
            double startY = Math.Max(margin, (screenHeight - totalHeight) / 2);

            for (int i = 0; i < targets.Count; i++)
            {
                int row = i / perRow;
                int column = i % perRow;
                targets[i].X = Math.Round(startX + (column * (width + gap)));
                targets[i].Y = Math.Round(startY + (row * (height + gap)));
                targets[i].Width = width;
                targets[i].Height = height;
            }

            SettingsService.SaveDebounced(notifySubscribers: false);
            File.WriteAllText(markerPath, DateTime.Now.ToString("s") + " arranged=" + targets.Count);
            Log($"[Companion] arranged {targets.Count} library widget(s)");
        }
        catch (Exception ex)
        {
            Log($"[Companion] widget arrangement failed: {ex.Message}");
        }
    }

    private void TryQueueCompanionLibraryWidgets()
    {
        try
        {
            string companionDirectory = Path.Combine(AppContext.BaseDirectory, "companion");
            string configPath = Path.Combine(companionDirectory, "config.json");
            string markerPath = Path.Combine(companionDirectory, "widgets.created");
            if (!File.Exists(configPath) || File.Exists(markerPath))
            {
                return;
            }

            UiDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    await CreateCompanionLibraryWidgetsAsync(configPath, markerPath);
                }
                catch (Exception ex)
                {
                    Log($"[Companion] library widget mapping failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"[Companion] library widget mapping skipped: {ex.Message}");
        }
    }

    private async Task CreateCompanionLibraryWidgetsAsync(string configPath, string markerPath)
    {
        WidgetManager? manager = WidgetManager;
        if (manager is null)
        {
            // Widget layer not ready yet: leave the marker absent so the mapping
            // is retried on the next launch.
            return;
        }

        string library = ReadCompanionLibraryPath(configPath);
        if (string.IsNullOrWhiteSpace(library))
        {
            Log("[Companion] config has no library path");
            return;
        }

        // The engine creates the tree on start; give it a few seconds to win
        // the race instead of giving up permanently.
        for (int attempt = 0; attempt < 5 && !Directory.Exists(library); attempt++)
        {
            await Task.Delay(3000);
        }

        if (!Directory.Exists(library))
        {
            Log($"[Companion] library folder not found: '{library}'");
            return;
        }

        int created = 0;
        foreach (string folderName in CompanionLibraryFolders)
        {
            string folderPath = Path.Combine(library, folderName);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            if (HasWidgetForPath(folderPath))
            {
                continue;
            }

            await manager.CreateFolderWidgetAsync(folderPath);
            created++;
        }

        int removed = await RemoveStaleDesktopWidgetsAsync(manager);

        // Only mark done once at least one mapping exists, otherwise a missing
        // library would permanently disable the bootstrap.
        if (created > 0 || HasAnyCompanionWidget(library))
        {
            File.WriteAllText(markerPath, DateTime.Now.ToString("s") + " created=" + created);
            Log($"[Companion] mapped {created} library folder(s) as widgets, removed {removed} stale desktop widget(s)");
        }
    }

    /// <summary>
    /// Drops file widgets that pointed at desktop folders the organizer already
    /// moved into the library. Widget-only removal: the folder is never touched.
    /// </summary>
    private async Task<int> RemoveStaleDesktopWidgetsAsync(WidgetManager manager)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return 0;
        }

        var stale = new List<string>();
        foreach (WidgetConfig widget in SettingsService.Settings.WidgetLayout.Widgets)
        {
            string? mapped = widget.MappedFolderPath;
            if (string.IsNullOrWhiteSpace(mapped))
            {
                continue;
            }

            string path = mapped!.TrimEnd('\\');
            if (!path.StartsWith(desktop + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Directory.Exists(path))
            {
                stale.Add(widget.Id);
            }
        }

        int removed = 0;
        foreach (string widgetId in stale)
        {
            try
            {
                await manager.RemoveWidgetAsync(widgetId);
                removed++;
            }
            catch (Exception ex)
            {
                Log($"[Companion] could not remove stale widget '{widgetId}': {ex.Message}");
            }
        }

        return removed;
    }

    private bool HasWidgetForPath(string folderPath)
    {
        string target = folderPath.TrimEnd('\\');
        foreach (WidgetConfig widget in SettingsService.Settings.WidgetLayout.Widgets)
        {
            string? mapped = widget.MappedFolderPath;
            if (!string.IsNullOrWhiteSpace(mapped) &&
                string.Equals(mapped!.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAnyCompanionWidget(string library)
    {
        string root = library.TrimEnd('\\') + "\\";
        foreach (WidgetConfig widget in SettingsService.Settings.WidgetLayout.Widgets)
        {
            string? mapped = widget.MappedFolderPath;
            if (!string.IsNullOrWhiteSpace(mapped) &&
                mapped!.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadCompanionLibraryPath(string configPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!document.RootElement.TryGetProperty("library", out JsonElement element))
        {
            return string.Empty;
        }

        string raw = element.GetString() ?? string.Empty;
        return Environment.ExpandEnvironmentVariables(raw);
    }
}
