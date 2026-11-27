using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DeskBox;

/// <summary>
/// Fan-sector actions that need the main process's services (library path,
/// widget manager). Kept out of App.Drawer.cs so the drag/drop routing in
/// WidgetDrawerWindow only has to call one method per sector.
/// </summary>
public partial class App
{
    /// <summary>Fan sector 「待定」: park the files in the engine's pending
    /// folder so they stop cluttering the desktop until someone decides.</summary>
    internal async Task<(int Moved, string Message)> FileDrawerToPendingAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return (0, "没有可归类的文件");
        }

        await ResolveDrawerLibraryAsync();
        string library = _drawerLibrary ?? string.Empty;
        if (string.IsNullOrEmpty(library))
        {
            FlashDrawerGlyph("?", "找不到资料库", "先在设置里指定共享库");
            return (0, "找不到资料库");
        }

        string folder = Path.Combine(library, "_待归类");
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            FlashDrawerGlyph("!", "归类失败", "建目录失败：" + ex.Message);
            return (0, "建目录失败：" + ex.Message);
        }

        (int moved, List<string> failures) = Platform.DrawerStaging.MoveFiles(folder, paths);
        FlashDrawerGlyph(
            moved > 0 ? "类" : "!",
            moved > 0 ? "已收进待归类 " + moved + " 个" : "归类失败",
            failures.Count == 0 ? null : string.Join("；", failures));
        return (moved, failures.Count == 0 ? "已收进待归类" : string.Join("；", failures));
    }

    internal async Task OpenDrawerPendingAsync()
    {
        await ResolveDrawerLibraryAsync();
        string library = _drawerLibrary ?? string.Empty;
        if (string.IsNullOrEmpty(library))
        {
            FlashDrawerGlyph("?", "找不到资料库", "先在设置里指定共享库");
            return;
        }

        OpenDrawerFolder(Path.Combine(library, "_待归类"));
    }

    /// <summary>
    /// drawer.json 的 CollapsedWidgetIds 是「哪些格子收起」的唯一真相。
    /// 设置页勾掉一个格子后经 drawer-reload 走到这里，逐格设成
    /// visible = !collapsed —— 双向都收敛，不会出现「勾上也不出来」。
    /// </summary>
    internal async Task ApplyDrawerParkedStateAsync()
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        var collapsed = new HashSet<string>(DrawerSettings.CollapsedWidgetIds, StringComparer.Ordinal);
        foreach (var state in DrawerWidgetStates())
        {
            try
            {
                await manager.SetWidgetVisibleAsync(state.Id, !collapsed.Contains(state.Id));
            }
            catch (Exception ex)
            {
                Log($"[Drawer] apply parked state for {state.Id} failed: {ex.Message}");
            }
        }

        RefreshDrawerAppearance();
    }

    /// <summary>「一键收起全部」也要写进 CollapsedWidgetIds，否则设置页的
    /// 勾选状态会和桌面实际不一致（设置页读的就是这份列表）。</summary>
    private void RecordAllWidgetsCollapsed()
    {
        DrawerSettings.CollapsedWidgetIds.Clear();
        foreach (var state in DrawerWidgetStates())
        {
            DrawerSettings.CollapsedWidgetIds.Add(state.Id);
        }

        DrawerSettings.Save();
    }
}
