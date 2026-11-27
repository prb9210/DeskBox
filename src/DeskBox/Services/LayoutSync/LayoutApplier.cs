using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 把对端合并后的文档落到本机。只应用「比自己新」的条目：
/// 每条自带 updatedAt，比本机记录旧的一律跳过——否则两台机会互相把
/// 对方的旧值又推回去，界面上看起来就是「格子自己在跳」。
/// </summary>
internal static class LayoutApplier
{
    public sealed class Result
    {
        public int Applied { get; set; }
        public int Created { get; set; }
        public int Removed { get; set; }
        public bool StyleApplied { get; set; }
        public bool DrawerApplied { get; set; }
        public bool CapsuleApplied { get; set; }
        public List<string> Touched { get; } = new();
        public long Rev { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;

        public bool Anything => Applied > 0 || Created > 0 || Removed > 0 ||
                                StyleApplied || DrawerApplied || CapsuleApplied;
    }

    public static async Task<Result> ApplyAsync(
        App app,
        JsonElement doc,
        LayoutSyncState state,
        string library,
        bool force = false)
    {
        var result = new Result();
        if (app.WidgetManager is not WidgetManager manager)
        {
            return result;
        }

        AppSettings appSettings = app.SettingsService.Settings;
        var widgets = appSettings.WidgetLayout.Widgets;
        string root = LayoutDocBuilder.Normalize(library);
        IReadOnlyList<WidgetScreenInfo> screens = LayoutDocBuilder.SafeScreens();
        WidgetScreenInfo? screen = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        double scale = screen?.EffectiveDpiScale ?? 1.0;
        RectInt32 area = screen?.WorkArea ?? new RectInt32(0, 0, 1920, 1040);

        // 先吸收墓碑：对端明确删掉的格子，本机如果还在就删。
        if (doc.TryGetProperty("deleted", out JsonElement deleted) &&
            deleted.ValueKind == JsonValueKind.Array)
        {
            var keys = LayoutDocBuilder.AssignKeys(widgets, root);
            foreach (JsonElement tomb in deleted.EnumerateArray())
            {
                string key = Text(tomb, "key");
                DateTimeOffset at = Time(tomb, "deletedAt");
                if (key.Length == 0 || at == default)
                {
                    continue;
                }

                string? id = keys.FirstOrDefault(pair => pair.Value == key).Key;
                if (id is null)
                {
                    continue;
                }

                if (!force &&
                    state.Widgets.TryGetValue(id, out LayoutSyncState.WidgetState? known) &&
                    known.UpdatedAt > at)
                {
                    continue; // 本机后来又编辑过这个格子：以本机为准
                }

                try
                {
                    await manager.RemoveWidgetAsync(id);
                    state.Widgets.Remove(id);
                    state.DeadKeys[key] = at;
                    result.Removed++;
                }
                catch (Exception ex)
                {
                    App.Log("[LayoutSync] remove failed: " + ex.Message);
                }
            }
        }

        if (doc.TryGetProperty("widgets", out JsonElement array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                string key = Text(item, "key");
                if (key.Length == 0)
                {
                    continue;
                }

                DateTimeOffset at = Time(item, "updatedAt");
                WidgetConfig? config = FindLocal(app, key, root);
                if (config is null)
                {
                    if (!key.StartsWith("folder:", StringComparison.Ordinal))
                    {
                        continue; // 功能格子要在本机自己建，v1 不代建
                    }

                    string folder = ResolveFolder(key, library);
                    if (folder.Length == 0 || !System.IO.Directory.Exists(folder))
                    {
                        continue;
                    }

                    try
                    {
                        await manager.CreateFolderWidgetAsync(folder);
                        config = FindLocal(app, key, root);
                        if (config is not null)
                        {
                            result.Created++;
                            result.Touched.Add(config.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log("[LayoutSync] create failed: " + ex.Message);
                    }

                    if (config is null)
                    {
                        continue;
                    }
                }

                if (!force &&
                    state.Widgets.TryGetValue(config.Id, out LayoutSyncState.WidgetState? stored) &&
                    stored.UpdatedAt >= at && at != default)
                {
                    continue; // 本机这条更新，下一轮由本机推上去
                }

                ApplyWidget(config, item, area, scale);
                state.Widgets[config.Id] = new LayoutSyncState.WidgetState
                {
                    Key = key,
                    Signature = LayoutDocBuilder.ComputeSignature(app, config, key, screens),
                    UpdatedAt = at
                };
                state.DeadKeys.Remove(key);
                result.Applied++;
                result.Touched.Add(config.Id);
            }
        }

        // 全局样式：只有对端更新过才覆盖，避免把本机正在调的滑杆弹回去。
        DateTimeOffset shellAt = Time(doc, "shellUpdatedAt");
        if (doc.TryGetProperty("shellStyle", out JsonElement shell) &&
            shell.ValueKind == JsonValueKind.Object &&
            (force || shellAt > state.ShellAt))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(shell.GetRawText());
                if (node is JsonObject shellObject &&
                    ShellStyleApplier.Apply(appSettings, shellObject) > 0)
                {
                    state.ShellSignature = shellObject.ToJsonString();
                    state.ShellAt = shellAt;
                    result.StyleApplied = true;
                }
            }
            catch (Exception ex)
            {
                App.Log("[LayoutSync] shell style apply failed: " + ex.Message);
            }
        }

        DateTimeOffset drawerAt = Time(doc, "drawerUpdatedAt");
        if (doc.TryGetProperty("drawer", out JsonElement drawer) &&
            drawer.ValueKind == JsonValueKind.Object &&
            (force || drawerAt > state.DrawerAt))
        {
            try
            {
                if (ApplyDrawer(app, drawer))
                {
                    state.DrawerSignature = JsonNode.Parse(drawer.GetRawText())?.ToJsonString() ?? string.Empty;
                    state.DrawerAt = drawerAt;
                    result.DrawerApplied = true;
                }
            }
            catch (Exception ex)
            {
                App.Log("[LayoutSync] drawer apply failed: " + ex.Message);
            }
        }

        DateTimeOffset capsuleAt = Time(doc, "capsuleUpdatedAt");
        if (doc.TryGetProperty("capsuleOrder", out JsonElement capsule) &&
            capsule.ValueKind == JsonValueKind.Array &&
            (force || capsuleAt > state.CapsuleAt))
        {
            List<string> orderKeys = capsule.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(k => k.Length > 0)
                .ToList();
            var keys = LayoutDocBuilder.AssignKeys(widgets, root);
            var order = new List<string>();
            foreach (string key in orderKeys)
            {
                string? id = keys.FirstOrDefault(pair => pair.Value == key).Key;
                if (id is not null && !order.Contains(id, StringComparer.Ordinal))
                {
                    order.Add(id);
                }
            }

            foreach (WidgetConfig widget in widgets)
            {
                if (!order.Contains(widget.Id, StringComparer.Ordinal))
                {
                    order.Add(widget.Id);
                }
            }

            if (order.Count > 0)
            {
                appSettings.WidgetShell.WidgetCapsuleBarOrder.Clear();
                appSettings.WidgetShell.WidgetCapsuleBarOrder.AddRange(order);
                state.CapsuleSignature = new JsonArray(orderKeys.Select(k => (JsonNode?)k).ToArray()).ToJsonString();
                state.CapsuleAt = capsuleAt;
                result.CapsuleApplied = true;
            }
        }

        result.Rev = Number(doc, "rev") is double rev ? (long)rev : 0;
        result.UpdatedBy = Text(doc, "updatedBy");

        if (result.Applied > 0 || result.Created > 0 || result.StyleApplied || result.CapsuleApplied)
        {
            await app.SettingsService.SaveAsync();
        }

        // 窗口要重新落位/重绘，否则配置改了界面还停在原地。只刷新这次真被
        // 改动的几个：全部重显会打乱 Z 序、偶尔闪一下（每轮同步都会跑）。
        foreach (string touchedId in result.Touched)
        {
            WidgetConfig? widget = widgets.FirstOrDefault(w => w.Id == touchedId);
            if (widget is null || !widget.IsVisible)
            {
                continue;
            }

            try
            {
                await manager.ShowWidgetAsync(widget.Id, reveal: true);
            }
            catch (Exception ex)
            {
                App.Log("[LayoutSync] refresh failed: " + ex.Message);
            }
        }

        return result;
    }

    private static void ApplyWidget(WidgetConfig config, JsonElement item, RectInt32 area, double scale)
    {
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

        double widthPx = config.Width * scale;
        double heightPx = config.Height * scale;
        config.PositionAnchor = anchor;
        config.PositionMarginX = Math.Max(0, marginX);
        config.PositionMarginY = Math.Max(0, marginY);
        config.X = anchor.Contains("Right", StringComparison.OrdinalIgnoreCase)
            ? area.X + area.Width - (marginX * scale) - widthPx
            : area.X + (marginX * scale);
        config.Y = anchor.Contains("Bottom", StringComparison.OrdinalIgnoreCase)
            ? area.Y + area.Height - (marginY * scale) - heightPx
            : area.Y + (marginY * scale);
        config.NeedsInitialPlacement = false;

        if (item.TryGetProperty("visible", out JsonElement visible) &&
            visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            config.IsVisible = visible.GetBoolean();
        }

        if (item.TryGetProperty("title", out JsonElement title) &&
            title.ValueKind == JsonValueKind.String)
        {
            string name = title.GetString() ?? string.Empty;
            if (name.Length > 0)
            {
                config.Name = name;
            }
        }

        if (item.TryGetProperty("style", out JsonElement style) &&
            style.ValueKind == JsonValueKind.Object)
        {
            ApplyWidgetStyle(config, style);
        }
    }

    /// <summary>单个格子的样式白名单，与 WidgetStyleBackupProjection.WidgetKeys 一致。</summary>
    private static void ApplyWidgetStyle(WidgetConfig config, JsonElement style)
    {
        if (style.TryGetProperty("isDefaultTitle", out JsonElement isDefault) &&
            isDefault.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            config.IsDefaultTitle = isDefault.GetBoolean();
        }

        if (style.TryGetProperty("viewMode", out JsonElement view) &&
            view.ValueKind == JsonValueKind.String &&
            Enum.TryParse(view.GetString(), ignoreCase: true, out ViewMode mode))
        {
            config.ViewMode = mode;
        }

        if (style.TryGetProperty("sortMode", out JsonElement sort) &&
            sort.ValueKind == JsonValueKind.String &&
            Enum.TryParse(sort.GetString(), ignoreCase: true, out WidgetSortMode sortMode))
        {
            config.SortMode = sortMode;
        }

        if (style.TryGetProperty("sortDescending", out JsonElement desc) &&
            desc.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            config.SortDescending = desc.GetBoolean();
        }
    }

    private static bool ApplyDrawer(App app, JsonElement drawer)
    {
        WidgetDrawerSettings current = app.DrawerSettings;
        WidgetDrawerSettings disk = WidgetDrawerSettings.Load(current.Path);
        bool changed = false;
        if (drawer.TryGetProperty("glass", out JsonElement glass) &&
            glass.ValueKind == JsonValueKind.String)
        {
            string value = glass.GetString() ?? string.Empty;
            if (value.Length > 0 && !string.Equals(disk.Glass, value, StringComparison.Ordinal))
            {
                disk.Glass = value;
                changed = true;
            }
        }

        if (drawer.TryGetProperty("shape", out JsonElement shape) &&
            shape.ValueKind == JsonValueKind.String)
        {
            string value = shape.GetString() ?? string.Empty;
            if (value.Length > 0 && !string.Equals(disk.Shape, value, StringComparison.Ordinal))
            {
                disk.Shape = value;
                changed = true;
            }
        }

        if (drawer.TryGetProperty("size", out JsonElement size) &&
            size.ValueKind == JsonValueKind.Number)
        {
            double value = size.GetDouble();
            if (value >= 24 && Math.Abs(disk.Size - value) > 0.5)
            {
                disk.Size = value;
                changed = true;
            }
        }

        changed |= SetBool(drawer, "visible", disk.Visible, v => disk.Visible = v);
        changed |= SetBool(drawer, "idle_fade", disk.IdleFade, v => disk.IdleFade = v);
        changed |= SetBool(drawer, "auto_hide_fullscreen", disk.AutoHideFullScreen, v => disk.AutoHideFullScreen = v);

        if (drawer.TryGetProperty("custom", out JsonElement custom) &&
            custom.ValueKind == JsonValueKind.Object)
        {
            if (custom.TryGetProperty("fill", out JsonElement fill) &&
                fill.ValueKind == JsonValueKind.String)
            {
                disk.Custom.Fill = fill.GetString() ?? disk.Custom.Fill;
                changed = true;
            }

            if (custom.TryGetProperty("opacity", out JsonElement opacity) &&
                opacity.ValueKind == JsonValueKind.Number)
            {
                disk.Custom.Opacity = Math.Clamp(opacity.GetDouble(), 0, 1);
                changed = true;
            }

            if (custom.TryGetProperty("highlight", out JsonElement highlight) &&
                highlight.ValueKind == JsonValueKind.Number)
            {
                disk.Custom.Highlight = Math.Clamp(highlight.GetDouble(), 0, 1);
                changed = true;
            }

            if (custom.TryGetProperty("rim", out JsonElement rim) &&
                rim.ValueKind == JsonValueKind.Number)
            {
                disk.Custom.Rim = Math.Clamp(rim.GetDouble(), 0, 1);
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        disk.Save();
        app.ReloadDrawerSettingsFromDisk();
        return true;
    }

    private static bool SetBool(JsonElement obj, string name, bool current, Action<bool> setter)
    {
        if (!obj.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        bool value = element.GetBoolean();
        if (value == current)
        {
            return false;
        }

        setter(value);
        return true;
    }

    private static WidgetConfig? FindLocal(App app, string key, string root)
    {
        var widgets = app.SettingsService.Settings.WidgetLayout.Widgets;
        var keys = LayoutDocBuilder.AssignKeys(widgets, root);
        foreach ((string id, string candidate) in keys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return widgets.FirstOrDefault(w => w.Id == id);
            }
        }

        return null;
    }

    private static string ResolveFolder(string key, string library)
    {
        string relative = key.Substring("folder:".Length);
        if (relative.Length == 0 || relative.StartsWith('@') || System.IO.Path.IsPathRooted(relative))
        {
            return string.Empty;
        }

        return System.IO.Path.Combine(library, relative.Replace('/', '\\'));
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : 0;

    private static DateTimeOffset Time(JsonElement root, string name)
    {
        string raw = Text(root, name);
        return DateTimeOffset.TryParse(raw, out DateTimeOffset parsed) ? parsed : default;
    }
}
