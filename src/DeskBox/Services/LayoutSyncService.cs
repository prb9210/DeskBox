using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Keeps the desktop design identical on two machines by exchanging a logical
/// layout document through the organizer engine (<c>/api/layout</c>), which
/// stores it in the library's <c>_deskbox\layout.json</c>.
///
/// Only machine-independent things travel: which widgets exist, their size, and
/// their position expressed as a screen-edge anchor plus a margin. Absolute
/// pixels, local paths, hotkeys and credentials stay in the local settings.json.
/// Step 1 is deliberately manual: the user pushes, the other machine pulls.
/// </summary>
public static class LayoutSyncService
{
    public sealed class Result
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
        public int Rev { get; init; }
    }

    private sealed record Entry(
        string Key, string Kind, string Title, string Anchor,
        double MarginX, double MarginY, double Width, double Height, bool Visible);

    // ---------------------------------------------------------------- push

    /// <summary>Serialises this machine's widgets and pushes them to the engine.</summary>
    public static async Task<Result> PushAsync(App app, string library)
    {
        if (app.WidgetManager is null)
        {
            return new Result { Ok = false, Message = "格子管理器还没就绪。" };
        }

        string json = BuildLayoutJson(app, library);
        (bool ok, string message) = await CompanionClient.PutJsonAsync("/api/layout", json);
        if (!ok)
        {
            return new Result { Ok = false, Message = "推送失败：" + message };
        }

        return new Result { Ok = true, Message = "已把这台机器的布局推送上去。请在另一台点「以另一台为准」。" };
    }

    private static string BuildLayoutJson(App app, string library)
    {
        IReadOnlyList<Entry> entries = CollectEntries(app, library);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteNumber("rev", 0);
            writer.WriteString("updatedBy", DeviceLabel(app));
            writer.WriteStartArray("widgets");
            foreach (Entry e in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("key", e.Key);
                writer.WriteString("kind", e.Kind);
                writer.WriteString("title", e.Title);
                writer.WriteString("anchor", e.Anchor);
                writer.WriteNumber("marginX", Math.Round(e.MarginX));
                writer.WriteNumber("marginY", Math.Round(e.MarginY));
                writer.WriteNumber("width", Math.Round(e.Width));
                writer.WriteNumber("height", Math.Round(e.Height));
                writer.WriteBoolean("visible", e.Visible);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<Entry> CollectEntries(App app, string library)
    {
        var list = new List<Entry>();
        string root = Normalize(library);
        foreach (WidgetConfig widget in app.SettingsService.Settings.WidgetLayout.Widgets)
        {
            if (widget.WidgetKind == WidgetKind.File)
            {
                string? mapped = widget.MappedFolderPath;
                if (string.IsNullOrWhiteSpace(mapped))
                {
                    continue;
                }

                string path = Normalize(mapped);
                string key = path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? "folder:" + path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                    : "folder:" + path;
                list.Add(ToEntry(widget, key));
                continue;
            }

            list.Add(ToEntry(widget, "kind:" + widget.WidgetKind));
        }

        return list;
    }

    private static Entry ToEntry(WidgetConfig widget, string key)
    {
        string anchor = string.IsNullOrWhiteSpace(widget.PositionAnchor) ? "LeftTop" : widget.PositionAnchor!;
        (double marginX, double marginY) = ResolveMargins(widget, anchor);
        return new Entry(key, widget.WidgetKind.ToString(), widget.Name, anchor,
            marginX, marginY, widget.Width, widget.Height, widget.IsVisible);
    }

    /// <summary>Margins are measured from the anchor edges; derive them from the
    /// stored rect when the config predates the anchor fields.</summary>
    private static (double X, double Y) ResolveMargins(WidgetConfig widget, string anchor)
    {
        (int workWidth, int workHeight) = WorkArea();
        double marginX = widget.PositionMarginX;
        double marginY = widget.PositionMarginY;
        if (marginX <= 0 && marginY <= 0)
        {
            marginX = anchor.Contains("Right", StringComparison.OrdinalIgnoreCase)
                ? workWidth - widget.X - widget.Width
                : widget.X;
            marginY = anchor.Contains("Bottom", StringComparison.OrdinalIgnoreCase)
                ? workHeight - widget.Y - widget.Height
                : widget.Y;
        }

        return (Math.Max(0, marginX), Math.Max(0, marginY));
    }

    // ---------------------------------------------------------------- pull

    /// <summary>Reads the shared layout and applies it to this machine.</summary>
    public static async Task<Result> PullAsync(App app, string library)
    {
        if (app.WidgetManager is not WidgetManager manager)
        {
            return new Result { Ok = false, Message = "格子管理器还没就绪。" };
        }

        JsonDocument? document = await CompanionClient.GetJsonAsync("/api/layout");
        if (document is null)
        {
            return new Result { Ok = false, Message = "没读到共享布局：管家引擎没在运行，或另一台还没推送过。" };
        }

        int applied = 0;
        int created = 0;
        int missing = 0;
        int rev = 0;
        string updatedBy = string.Empty;
        try
        {
            using (document)
            {
                JsonElement root = document.RootElement;
                rev = (int)Number(root, "rev");
                updatedBy = Text(root, "updatedBy");
                if (!root.TryGetProperty("widgets", out JsonElement widgets) ||
                    widgets.ValueKind != JsonValueKind.Array)
                {
                    return new Result { Ok = false, Message = "共享布局里没有格子清单。" };
                }

                (int workWidth, int workHeight) = WorkArea();
                foreach (JsonElement item in widgets.EnumerateArray())
                {
                    string key = Text(item, "key");
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    WidgetConfig? config = FindLocal(app, key, library);
                    if (config is null)
                    {
                        if (!key.StartsWith("folder:", StringComparison.Ordinal))
                        {
                            missing++; // 功能格子要在本机手动加，v1 不代建
                            continue;
                        }

                        string relative = key.Substring("folder:".Length);
                        string folder = relative.StartsWith('@') || Path.IsPathRooted(relative)
                            ? relative
                            : Path.Combine(library, relative.Replace('/', '\\'));
                        try
                        {
                            await manager.CreateFolderWidgetAsync(folder);
                            created++;
                            config = FindLocal(app, key, library);
                        }
                        catch (Exception)
                        {
                            missing++;
                            config = null;
                        }
                    }

                    if (config is null)
                    {
                        continue;
                    }

                    string anchor = Text(item, "anchor");
                    if (anchor.Length == 0)
                    {
                        anchor = "LeftTop";
                    }

                    double width = Number(item, "width");
                    double height = Number(item, "height");
                    double marginX = Number(item, "marginX");
                    double marginY = Number(item, "marginY");
                    if (width > 80)
                    {
                        config.Width = width;
                    }

                    if (height > 80)
                    {
                        config.Height = height;
                    }

                    config.PositionAnchor = anchor;
                    config.PositionMarginX = marginX;
                    config.PositionMarginY = marginY;
                    config.X = anchor.Contains("Right", StringComparison.OrdinalIgnoreCase)
                        ? workWidth - marginX - config.Width
                        : marginX;
                    config.Y = anchor.Contains("Bottom", StringComparison.OrdinalIgnoreCase)
                        ? workHeight - marginY - config.Height
                        : marginY;
                    config.NeedsInitialPlacement = false;
                    applied++;
                }
            }
        }
        finally
        {
            // Always persist what was applied so a half-finished pull still moves
            // the widgets the user can see instead of silently doing nothing.
            await app.SettingsService.SaveAsync();
        }

        foreach (WidgetConfig widget in app.SettingsService.Settings.WidgetLayout.Widgets)
        {
            if (!widget.IsVisible)
            {
                continue;
            }

            try
            {
                await manager.ShowWidgetAsync(widget.Id, reveal: true);
            }
            catch (Exception ex)
            {
                App.Log("[Layout] refresh failed: " + ex.Message);
            }
        }

        string who = updatedBy.Length == 0 ? "另一台" : updatedBy;
        string message = "已按 " + who + " 的布局（rev " + rev + "）调整 " + applied + " 个格子";
        if (created > 0)
        {
            message += "，新建 " + created + " 个";
        }

        if (missing > 0)
        {
            message += "，" + missing + " 个功能格子需要在本机手动添加";
        }

        return new Result { Ok = true, Message = message + "。", Rev = rev };
    }

    private static WidgetConfig? FindLocal(App app, string key, string library)
    {
        foreach (WidgetConfig widget in app.SettingsService.Settings.WidgetLayout.Widgets)
        {
            if (key.StartsWith("kind:", StringComparison.Ordinal))
            {
                if (widget.WidgetKind != WidgetKind.File &&
                    string.Equals(widget.WidgetKind.ToString(), key.Substring(5), StringComparison.OrdinalIgnoreCase))
                {
                    return widget;
                }

                continue;
            }

            string? mapped = widget.MappedFolderPath;
            if (string.IsNullOrWhiteSpace(mapped))
            {
                continue;
            }

            string path = Normalize(mapped);
            string root = Normalize(library);
            string candidate = "folder:" + (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : path);
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return widget;
            }
        }

        return null;
    }

    // -------------------------------------------------------------- helpers

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Trim()).Replace('/', '\\');

    private static string DeviceLabel(App app)
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "pc";
        }
    }

    /// <summary>Primary work area in physical pixels; the layout only needs the
    /// size because anchors are relative to the primary screen's edges.</summary>
    private static (int Width, int Height) WorkArea()
    {
        try
        {
            IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();
            if (screens.Count > 0)
            {
                return (screens[0].WorkArea.Width, screens[0].WorkArea.Height);
            }
        }
        catch (Exception)
        {
            // fall through to the default below
        }

        return (1920, 1040);
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : 0;
}
