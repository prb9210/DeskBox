using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeskBox.Services;

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

    // ---------------------------------------------------------------- 扫描

    /// <summary>最近一次扫描生成的 PDF，供球上「打开扫描资料」定位。</summary>
    private string? _lastScanFile;

    /// <summary>扇片「扫描」：图片 → 引擎找纸边/拉正/去阴影 → 一个 PDF，
    /// 落到 &lt;坚果云&gt;\扫描资料。整条链路在本机完成，不上传任何照片。</summary>
    internal async Task ScanImagesToPdfAsync(IReadOnlyList<string> paths)
    {
        var images = new List<string>();
        var skipped = new List<string>();
        foreach (string path in paths)
        {
            if (DrawerFanActions.IsImage(path))
            {
                images.Add(path);
            }
            else
            {
                skipped.Add(Path.GetFileName(path));
            }
        }

        if (images.Count == 0)
        {
            FlashDrawerGlyph("?", "没有可扫描的图片", "只支持 JPG / PNG / HEIC 等图片");
            return;
        }

        FlashDrawerGlyph("扫", $"正在扫描 {images.Count} 页…", "找纸边 · 去阴影 · 合成 PDF");

        // HEIC/TIFF 之类先转成临时 JPEG；引擎只认 JPEG/PNG/GIF。
        (List<string> ready, List<string> temp, List<string> failures) =
            await Platform.ScanInputs.PrepareAsync(images);

        try
        {
            if (ready.Count == 0)
            {
                FlashDrawerGlyph("!", "扫描失败",
                    failures.Count > 0 ? failures[0] : "没有可用的图片");
                return;
            }

            (bool ok, string body) = await CompanionClient.PostDocScanAsync(BuildDocScanJson(ready));
            if (!ok && string.IsNullOrWhiteSpace(body))
            {
                FlashDrawerGlyph("!", "扫描失败", "管家引擎未运行");
                return;
            }

            if (!TryReadDocScanResult(body, out bool success, out string file,
                    out int pages, out long bytes, out string warning, out string error))
            {
                FlashDrawerGlyph("!", "扫描失败", "引擎返回了无法解析的结果");
                return;
            }

            if (!success || string.IsNullOrEmpty(file))
            {
                FlashDrawerGlyph("!", "扫描失败", string.IsNullOrEmpty(error) ? "未生成 PDF" : error);
                return;
            }

            _lastScanFile = file;
            string size = bytes > 0 ? $" · {bytes / 1024 / 1024.0:0.0} MB" : string.Empty;
            string detail = Path.GetFileName(file) + size;
            if (!string.IsNullOrEmpty(warning))
            {
                detail += " · " + warning;
            }

            if (failures.Count > 0)
            {
                detail += " · 跳过 " + failures.Count + " 个";
            }

            FlashDrawerGlyph("✓", $"已扫描 {pages} 页", detail);

            if (skipped.Count > 0)
            {
                Log($"[Scan] skipped non-image files: {string.Join(", ", skipped)}");
            }
        }
        finally
        {
            Platform.ScanInputs.Cleanup(temp);
        }
    }

    /// <summary>扇片「扫描」点击（没拖文件）：打开扫描资料目录。</summary>
    internal async Task OpenDrawerScanFolderAsync()
    {
        if (!string.IsNullOrEmpty(_lastScanFile))
        {
            string? folder = Path.GetDirectoryName(_lastScanFile);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                OpenDrawerFolder(folder);
                return;
            }
        }

        string library = await ResolveDrawerLibraryAsync();
        if (string.IsNullOrEmpty(library))
        {
            FlashDrawerGlyph("?", "找不到资料库", "先在设置里指定共享库");
            return;
        }

        OpenDrawerFolder(Path.Combine(library, "扫描资料"));
    }

    /// <summary>手写 JSON：AOT 下不走反射序列化，路径里的反斜杠和引号自己转义。</summary>
    private static string BuildDocScanJson(IReadOnlyList<string> paths)
    {
        var builder = new System.Text.StringBuilder("{\"paths\":[");
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(EscapeJson(paths[i])).Append('"');
        }

        builder.Append("],\"mode\":\"auto\",\"dpi\":300}");
        return builder.ToString();
    }

    private static string EscapeJson(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static bool TryReadDocScanResult(
        string body,
        out bool success,
        out string file,
        out int pages,
        out long bytes,
        out string warning,
        out string error)
    {
        success = false;
        file = string.Empty;
        pages = 0;
        bytes = 0;
        warning = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (root.TryGetProperty("ok", out System.Text.Json.JsonElement okElement))
            {
                success = okElement.ValueKind == System.Text.Json.JsonValueKind.True;
            }

            if (root.TryGetProperty("error", out System.Text.Json.JsonElement errorElement) &&
                errorElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                error = errorElement.GetString() ?? string.Empty;
            }

            if (!root.TryGetProperty("result", out System.Text.Json.JsonElement result) ||
                result.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return true;
            }

            if (result.TryGetProperty("file", out System.Text.Json.JsonElement fileElement) &&
                fileElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                file = fileElement.GetString() ?? string.Empty;
            }

            if (result.TryGetProperty("pages", out System.Text.Json.JsonElement pagesElement) &&
                pagesElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                pages = pagesElement.GetInt32();
            }

            if (result.TryGetProperty("bytes", out System.Text.Json.JsonElement bytesElement) &&
                bytesElement.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                bytes = bytesElement.GetInt64();
            }

            if (result.TryGetProperty("warnings", out System.Text.Json.JsonElement warningsElement) &&
                warningsElement.ValueKind == System.Text.Json.JsonValueKind.Array &&
                warningsElement.GetArrayLength() > 0)
            {
                System.Text.Json.JsonElement first = warningsElement[0];
                warning = first.ValueKind == System.Text.Json.JsonValueKind.String
                    ? first.GetString() ?? string.Empty
                    : string.Empty;
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
