using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Companion (桌面管家) "按分类生成格子" rebuild. One command that:
///   1. retires every folder grid that is not part of the current taxonomy
///      (its folder contents move to the desktop first, so nothing is lost
///      and the organizer can re-sort them into the new categories),
///   2. creates the missing category grids in taxonomy order,
///   3. folds every remaining grid into a collapsed capsule rail along the
///      right edge of its display — weather first, search last.
///
/// Before this the button only ever *added* widgets (old grids kept piling up
/// in the fan) and every new widget went through the cascade placement, which
/// offsets by 24 DIP per existing widget — eight of them ended up stacked on
/// top of each other.
/// </summary>
public sealed partial class WidgetManager
{
    /// <summary>Gap between the screen edge and the capsule rail, in logical px.</summary>
    private const double CompanionRailEdgeMargin = 12;

    /// <summary>Rank of a grid that is neither weather, a category nor search.</summary>
    private const int CompanionRailOtherRank = 500;

    public async Task<CompanionWidgetResetResult> ResetCompanionCategoryWidgetsAsync(
        string libraryFolder,
        IReadOnlyList<string> categoryNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFolder);
        ArgumentNullException.ThrowIfNull(categoryNames);

        string library = NormalizeCompanionPath(libraryFolder);
        Directory.CreateDirectory(library);

        string managedRoot = string.Empty;
        string configuredManagedRoot = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        if (!string.IsNullOrWhiteSpace(configuredManagedRoot))
        {
            managedRoot = NormalizeCompanionPath(configuredManagedRoot);
        }

        var targets = new List<(string Name, string Path)>();
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedNames = new List<string>();
        var errors = new List<string>();
        int movedFolders = 0;
        int leftInPlace = 0;

        foreach (string rawName in categoryNames)
        {
            string name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string path = NormalizeCompanionPath(Path.Combine(library, name));
            if (!targetPaths.Add(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                errors.Add("目录「" + name + "」建不出来：" + ex.Message);
                targetPaths.Remove(path);
                continue;
            }

            targets.Add((name, path));
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("分类清单是空的，没有格子可生成。");
        }

        // ── 1. Retire the grids of the previous taxonomy ────────────────────
        // Only folder-backed file grids participate: a manual widget (no
        // MappedFolderPath) owns no category folder and is left alone.
        var keepByPath = new Dictionary<string, WidgetConfig>(StringComparer.OrdinalIgnoreCase);
        var redundant = new List<(WidgetConfig Widget, string? Folder)>();
        foreach (WidgetConfig widget in _settingsService.Settings.Widgets
                     .Where(candidate => candidate.WidgetKind == WidgetKind.File && !IsDeleted(candidate.Id))
                     .ToList())
        {
            string? mapped = widget.MappedFolderPath;
            if (string.IsNullOrWhiteSpace(mapped))
            {
                continue;
            }

            string path;
            try
            {
                path = NormalizeCompanionPath(mapped!);
            }
            catch (Exception ex)
            {
                App.Log($"[Companion] unreadable widget folder '{mapped}': {ex.Message}");
                redundant.Add((widget, null));
                continue;
            }

            // A second grid on the same category folder is a duplicate: the
            // first one stays, the rest retire with it (never draining the
            // folder — it is a live category).
            if (targetPaths.Contains(path) && keepByPath.TryAdd(path, widget))
            {
                continue;
            }

            redundant.Add((widget, path));
        }

        string desktopPath = string.Empty;
        try
        {
            desktopPath = NormalizeCompanionPath(_desktopPathProvider());
        }
        catch (Exception ex)
        {
            App.Log($"[Companion] desktop path unavailable: {ex.Message}");
        }

        foreach ((WidgetConfig widget, string? mappedFolder) in redundant)
        {
            string displayName = string.IsNullOrWhiteSpace(widget.Name)
                ? (mappedFolder is null ? "旧格子" : Path.GetFileName(mappedFolder))
                : widget.Name;

            string? folder = mappedFolder;
            if (folder is null)
            {
                // The path never normalized: retire the grid, leave the disk alone.
                try
                {
                    await RemoveWidgetAsync(widget.Id);
                    removedNames.Add(displayName);
                }
                catch (Exception ex)
                {
                    errors.Add("旧格子「" + displayName + "」没删掉：" + ex.Message);
                }

                continue;
            }

            // Only drain folders DeskBox itself owns: a retired category
            // folder inside the shared library, or a managed storage folder.
            // Never the library root (or one of its parents), never the
            // desktop, never an arbitrary user folder — for those the grid is
            // removed and the files stay exactly where they are.
            bool canDrain =
                desktopPath.Length > 0 &&
                Directory.Exists(folder) &&
                !targetPaths.Contains(folder) &&
                !IsSameOrAncestorDirectory(folder, library) &&
                !IsSameOrAncestorDirectory(folder, desktopPath) &&
                (IsUnderDirectory(folder, library) ||
                 (managedRoot.Length > 0 && IsUnderDirectory(folder, managedRoot)));

            if (canDrain)
            {
                try
                {
                    await MoveManagedFolderContentsToDesktopAsync(folder);
                    movedFolders++;
                }
                catch (Exception ex)
                {
                    // Keep the grid so the user can still reach the files that
                    // did not make it to the desktop; report instead of hiding.
                    errors.Add("旧格子「" + displayName + "」的文件没搬完，先保留：" + ex.Message);
                    continue;
                }
            }
            else if (Directory.Exists(folder))
            {
                leftInPlace++;
            }

            try
            {
                await RemoveWidgetAsync(widget.Id);
                removedNames.Add(displayName);
            }
            catch (Exception ex)
            {
                errors.Add("旧格子「" + displayName + "」没删掉：" + ex.Message);
            }
        }

        // ── 2. Create the missing categories, in taxonomy order ─────────────
        int created = 0;
        int reused = 0;
        var categoryPaths = new List<string>(targets.Count);
        foreach ((string name, string path) in targets)
        {
            categoryPaths.Add(path);
            WidgetConfig? existing = FindFolderWidget(path);
            if (existing is not null)
            {
                reused++;
                continue;
            }

            try
            {
                await CreateFolderWidgetAsync(path);
                created++;
            }
            catch (Exception ex)
            {
                errors.Add("新建「" + name + "」失败：" + ex.Message);
            }
        }

        // ── 3. Fold everything into the right-edge capsule rail ─────────────
        int railCount = ArrangeCompanionCapsuleRail(categoryPaths);

        await _settingsService.SaveAsync();

        return new CompanionWidgetResetResult(
            created,
            reused,
            removedNames.Count,
            movedFolders,
            leftInPlace,
            railCount,
            removedNames,
            errors);
    }

    private WidgetConfig? FindFolderWidget(string folderPath)
    {
        foreach (WidgetConfig widget in _settingsService.Settings.Widgets)
        {
            if (widget.WidgetKind != WidgetKind.File ||
                IsDeleted(widget.Id) ||
                string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                continue;
            }

            string mapped;
            try
            {
                mapped = NormalizeCompanionPath(widget.MappedFolderPath!);
            }
            catch
            {
                continue;
            }

            if (string.Equals(mapped, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return widget;
            }
        }

        return null;
    }

    /// <summary>
    /// Places every visible grid on the right-edge capsule rail of its own
    /// display: weather on top, categories in taxonomy order, other grids in
    /// the middle, search at the bottom. Also switches the persisted capsule
    /// arrangement to a vertical right bar so later refreshes keep the rail.
    /// </summary>
    private int ArrangeCompanionCapsuleRail(IReadOnlyList<string> categoryPaths)
    {
        AppSettings settings = _settingsService.Settings;

        // A capsule can only exist when the collapse behavior is not "always
        // expanded"; the user asked for folded grids, so downgrade the global
        // mode once instead of leaving every grid expanded.
        if (WidgetCollapseBehaviorNames.Normalize(settings.WidgetCollapseBehavior) ==
            WidgetCollapseBehavior.Expanded)
        {
            settings.WidgetCollapseBehavior = WidgetCollapseBehaviorNames.Click;
        }

        var categoryRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < categoryPaths.Count; index++)
        {
            categoryRank[categoryPaths[index]] = index;
        }

        var candidates = new List<WidgetConfig>();
        foreach (WidgetConfig config in settings.Widgets)
        {
            if (IsDeleted(config.Id) ||
                config.IsDisabled ||
                !config.IsVisible ||
                !WidgetGroupSettings.IsActiveMember(settings, config.Id) ||
                !_widgetRegistry.IsAvailableForSession(config, settings))
            {
                continue;
            }

            candidates.Add(config);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        int Rank(WidgetConfig config)
        {
            if (config.WidgetKind == WidgetKind.Weather)
            {
                return 0;
            }

            if (config.WidgetKind == WidgetKind.Search)
            {
                return int.MaxValue - 1;
            }

            if (!string.IsNullOrWhiteSpace(config.MappedFolderPath))
            {
                try
                {
                    if (categoryRank.TryGetValue(
                            NormalizeCompanionPath(config.MappedFolderPath!),
                            out int categoryIndex))
                    {
                        return 1 + categoryIndex;
                    }
                }
                catch
                {
                    // Unreadable path: fall through to the "other" rank.
                }
            }

            return CompanionRailOtherRank;
        }

        List<WidgetConfig> ordered = candidates
            .OrderBy(Rank)
            .ThenBy(config => config.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        double spacingLogical = SettingsService.NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
        var nextYByWorkArea = new Dictionary<string, int>(StringComparer.Ordinal);
        var columnByWorkArea = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (WidgetConfig config in ordered)
        {
            config.IsCollapsed = true;
            if (WidgetCollapseBehaviorNames.Resolve(config, settings.WidgetCollapseBehavior) ==
                WidgetCollapseBehavior.Expanded)
            {
                // Per-grid override wins over the global mode; clear it so the
                // capsule actually folds.
                WidgetCollapseBehaviorNames.SetOverride(config, WidgetCollapseBehavior.Click);
            }

            RectInt32 current = ResolveCompactBounds(config);
            RectInt32 workArea = DisplayArea.GetFromRect(
                current,
                DisplayAreaFallback.Nearest).WorkArea;
            string workAreaKey = $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";
            double scale = WidgetPositioningService.GetDpiScale(workArea);
            int margin = Math.Max(0, (int)Math.Round(CompanionRailEdgeMargin * scale));
            int spacing = Math.Max(0, (int)Math.Round(spacingLogical * scale));
            int maxWidth = Math.Max(1, workArea.Width - (margin * 2));
            int width = Math.Min(Math.Max(1, current.Width), maxWidth);
            int height = Math.Max(1, current.Height);

            int bottom = workArea.Y + workArea.Height - margin;
            int column = columnByWorkArea.TryGetValue(workAreaKey, out int currentColumn)
                ? currentColumn
                : 0;
            int y = nextYByWorkArea.TryGetValue(workAreaKey, out int cursorY)
                ? cursorY
                : workArea.Y + margin;

            // More capsules than the screen height: wrap into a second column
            // to the left instead of running off the bottom edge.
            if (y + height > bottom && y > workArea.Y + margin)
            {
                column++;
                columnByWorkArea[workAreaKey] = column;
                y = workArea.Y + margin;
            }

            int x = workArea.X + workArea.Width - margin - width - (column * (width + spacing));
            x = Math.Max(workArea.X + margin, x);
            var bounds = new RectInt32(x, y, width, height);
            nextYByWorkArea[workAreaKey] = y + height + spacing;

            config.CompactPlacement = CreatePlacement(bounds);
            SynchronizeGroupLayoutFromMember(config);

            if (FindLoadedWindow(config.Id) is { } window)
            {
                window.RequestCompactState(true, animate: false);
                window.ApplyCompactArrangement(bounds, constrainSize: true);
            }
        }

        settings.WidgetCapsuleArrangementMode = SettingsService.WidgetCapsuleArrangementBar;
        settings.WidgetCapsuleBarPlacement = SettingsService.WidgetCapsuleBarPlacementRight;
        settings.WidgetCapsuleBarDirection = SettingsService.WidgetCapsuleBarDirectionVertical;
        settings.WidgetCapsuleBarOrder.Clear();
        settings.WidgetCapsuleBarOrder.AddRange(ordered.Select(config => config.Id));

        _settingsService.SaveDebounced(notifySubscribers: false);
        // Let the shared arrangement pass re-derive the bar slots from the
        // order + first member we just wrote, so a later consistency check
        // cannot snap the rail back to a stale layout.
        RefreshCapsuleBarLayout();
        NoteWidgetWindowsMoved();
        return ordered.Count;
    }

    private static string NormalizeCompanionPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="candidate"/> is <paramref name="path"/> itself or one of its ancestors.</summary>
    private static bool IsSameOrAncestorDirectory(string candidate, string path) =>
        string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) ||
        IsUnderDirectory(path, candidate);

    private static bool IsUnderDirectory(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
