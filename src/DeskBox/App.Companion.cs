using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
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
        "02 教导处",
        "03 上级文件",
        "04 其他",
    };

    private void StartCompanionHostAndMapLibrary()
    {
        CompanionProcessHost.StartIfPresent();
        TryQueueCompanionLibraryWidgets();
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
