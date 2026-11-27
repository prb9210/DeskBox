using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 把本机当前的格子/样式/悬浮球外观投影成交互文档（layout.json 的载荷）。
///
/// 只放「两台应该一致」的东西：位置（边角锚点 + DIP 边距）、尺寸、显隐、
/// 样式白名单、胶囊顺序。绝对像素、本机路径、热键、密钥一律不出去。
/// 每条自带时间戳：改动过才更新时间，没改的沿用上次的值，这样两台机的
/// 合并是「谁改的那条谁赢」，不会整篇覆盖。
/// </summary>
internal static class LayoutDocBuilder
{
    public sealed class BuildResult
    {
        public JsonObject Document { get; init; } = new();
        public bool Changed { get; init; }
    }

    public static BuildResult Build(App app, string library, LayoutSyncState state)
    {
        bool changed = false;
        IReadOnlyList<WidgetScreenInfo> screens = SafeScreens();
        string root = Normalize(library);
        var widgets = app.SettingsService.Settings.WidgetLayout.Widgets;
        var keys = AssignKeys(widgets, root);
        JsonObject styleById = LoadStyleProjection(app);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var array = new JsonArray();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetConfig widget in widgets)
        {
            if (!keys.TryGetValue(widget.Id, out string? key) || key.Length == 0)
            {
                continue;
            }

            seenKeys.Add(key);
            liveIds.Add(widget.Id);
            (string anchor, double marginX, double marginY) = Placement(widget, screens);
            JsonObject? style = styleById[widget.Id] as JsonObject;

            string signature = Signature(widget, key, anchor, marginX, marginY, style);
            if (!state.Widgets.TryGetValue(widget.Id, out LayoutSyncState.WidgetState? stored) ||
                !string.Equals(stored.Key, key, StringComparison.Ordinal) ||
                !string.Equals(stored.Signature, signature, StringComparison.Ordinal))
            {
                stored = new LayoutSyncState.WidgetState
                {
                    Key = key,
                    Signature = signature,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                state.Widgets[widget.Id] = stored;
                changed = true;
            }

            var entry = new JsonObject
            {
                ["key"] = key,
                ["kind"] = widget.WidgetKind.ToString(),
                ["title"] = widget.Name,
                ["anchor"] = anchor,
                ["marginX"] = Math.Round(marginX, 1),
                ["marginY"] = Math.Round(marginY, 1),
                ["width"] = Math.Round(widget.Width, 1),
                ["height"] = Math.Round(widget.Height, 1),
                ["visible"] = widget.IsVisible,
                ["updatedAt"] = stored.UpdatedAt
            };
            if (style is not null)
            {
                entry["style"] = style.DeepClone();
            }

            array.Add(entry);
        }

        // 本机已不存在的格子 → 墓碑。不能靠「对面没有这条」推断删除：
        // 那会把还没拉到别人新格子的机器变成删除机。
        var deleted = new JsonArray();
        foreach (string id in state.Widgets.Keys.ToList())
        {
            if (liveIds.Contains(id))
            {
                continue;
            }

            LayoutSyncState.WidgetState gone = state.Widgets[id];
            state.Widgets.Remove(id);
            if (gone.Key.Length > 0)
            {
                state.DeadKeys[gone.Key] = DateTimeOffset.UtcNow;
            }

            changed = true;
        }

        foreach ((string key, DateTimeOffset at) in state.DeadKeys)
        {
            deleted.Add(new JsonObject { ["key"] = key, ["deletedAt"] = at });
        }

        JsonObject doc = new()
        {
            ["schema"] = 2,
            ["rev"] = 0,
            ["updatedBy"] = Environment.MachineName,
            ["widgets"] = array
        };
        if (deleted.Count > 0)
        {
            doc["deleted"] = deleted;
        }

        JsonObject? shell = ShellStyle(app);
        string shellSig = shell?.ToJsonString() ?? string.Empty;
        if (!string.Equals(shellSig, state.ShellSignature, StringComparison.Ordinal))
        {
            state.ShellSignature = shellSig;
            state.ShellAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        if (shell is not null)
        {
            doc["shellStyle"] = shell.DeepClone();
            doc["shellUpdatedAt"] = state.ShellAt;
        }

        JsonObject drawer = DrawerAppearance();
        string drawerSig = drawer.ToJsonString();
        if (!string.Equals(drawerSig, state.DrawerSignature, StringComparison.Ordinal))
        {
            state.DrawerSignature = drawerSig;
            state.DrawerAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        doc["drawer"] = drawer.DeepClone();
        doc["drawerUpdatedAt"] = state.DrawerAt;

        var capsule = new JsonArray();
        foreach (string id in app.SettingsService.Settings.WidgetShell.WidgetCapsuleBarOrder)
        {
            if (keys.TryGetValue(id, out string? key) && key.Length > 0)
            {
                capsule.Add(key);
            }
        }

        string capsuleSig = capsule.ToJsonString();
        if (!string.Equals(capsuleSig, state.CapsuleSignature, StringComparison.Ordinal))
        {
            state.CapsuleSignature = capsuleSig;
            state.CapsuleAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        if (capsule.Count > 0)
        {
            doc["capsuleOrder"] = capsule.DeepClone();
            doc["capsuleUpdatedAt"] = state.CapsuleAt;
        }

        return new BuildResult { Document = doc, Changed = changed };
    }

    /// <summary>稳定 key：库内格子用相对路径，功能格子用类型名；同类型多实例
    /// 按 Id 排序追加 #n（两台机的 Id 不同，所以只能用序号，不能用 Id）。</summary>
    public static Dictionary<string, string> AssignKeys(IReadOnlyList<WidgetConfig> widgets, string libraryRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var counter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (WidgetConfig widget in widgets.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            string baseKey = BaseKey(widget, libraryRoot);
            if (baseKey.Length == 0)
            {
                continue;
            }

            counter.TryGetValue(baseKey, out int n);
            n++;
            counter[baseKey] = n;
            result[widget.Id] = n == 1 ? baseKey : baseKey + "#" + n;
        }

        return result;
    }

    public static string BaseKey(WidgetConfig widget, string libraryRoot)
    {
        if (widget.WidgetKind == WidgetKind.File)
        {
            string? mapped = widget.MappedFolderPath;
            if (string.IsNullOrWhiteSpace(mapped))
            {
                return string.Empty;
            }

            string path = Normalize(mapped);
            if (libraryRoot.Length > 0 && path.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "folder:" + path.Substring(libraryRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            }

            // 库外格子：另一台不一定有这个路径，但写绝对路径至少不会张冠李戴。
            return "folder:" + path;
        }

        return "kind:" + widget.WidgetKind;
    }

    /// <summary>位置一律换成「屏幕边角 + DIP 边距」。两台分辨率/缩放不同，
    /// 绝对像素搬过去必偏；边距是逻辑像素，由接收方按自己的 DPI 换算。</summary>
    public static (string Anchor, double MarginX, double MarginY) Placement(
        WidgetConfig widget,
        IReadOnlyList<WidgetScreenInfo> screens)
    {
        if (!string.IsNullOrWhiteSpace(widget.PositionAnchor))
        {
            // 锚点由系统定位逻辑写入时边距必然同时写；直接沿用，
            // 避免接收方二次换算把 0 边距的格子算出不同数值。
            return (widget.PositionAnchor!, widget.PositionMarginX, widget.PositionMarginY);
        }

        WidgetScreenInfo? screen = ScreenFor(widget, screens);
        if (screen is null)
        {
            return ("LeftTop", Math.Max(0, widget.X), Math.Max(0, widget.Y));
        }

        double scale = screen.EffectiveDpiScale;
        double widthPx = widget.Width * scale;
        double heightPx = widget.Height * scale;
        RectInt32 area = screen.WorkArea;
        double left = (widget.X - area.X) / scale;
        double right = (area.X + area.Width - (widget.X + widthPx)) / scale;
        double top = (widget.Y - area.Y) / scale;
        double bottom = (area.Y + area.Height - (widget.Y + heightPx)) / scale;

        bool anchorRight = right < left;
        bool anchorBottom = bottom < top;
        return ((anchorRight, anchorBottom) switch
        {
            (true, true) => "RightBottom",
            (true, false) => "RightTop",
            (false, true) => "LeftBottom",
            _ => "LeftTop"
        }, Math.Max(0, anchorRight ? right : left), Math.Max(0, anchorBottom ? bottom : top));
    }

    public static WidgetScreenInfo? ScreenFor(WidgetConfig widget, IReadOnlyList<WidgetScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return null;
        }

        if (screens.Count == 1)
        {
            return screens[0];
        }

        double scale = screens[0].EffectiveDpiScale;
        double centerX = widget.X + (widget.Width * scale / 2);
        double centerY = widget.Y + (widget.Height * scale / 2);
        foreach (WidgetScreenInfo screen in screens)
        {
            RectInt32 area = screen.WorkArea;
            if (centerX >= area.X && centerX <= area.X + area.Width &&
                centerY >= area.Y && centerY <= area.Y + area.Height)
            {
                return screen;
            }
        }

        return screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
    }

    public static IReadOnlyList<WidgetScreenInfo> SafeScreens()
    {
        try
        {
            return WidgetScreenCatalog.Capture();
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] screen capture failed: " + ex.Message);
            return Array.Empty<WidgetScreenInfo>();
        }
    }

    /// <summary>应用对端条目后，按本机当前值重新算一次签名并写回状态：
    /// 否则下一轮构建会以为「本地改过了」，把刚收到的时间戳顶掉，
    /// 两台机就会来回推同一条永远停不下来。</summary>
    public static string ComputeSignature(
        App app,
        WidgetConfig widget,
        string key,
        IReadOnlyList<WidgetScreenInfo> screens)
    {
        (string anchor, double marginX, double marginY) = Placement(widget, screens);
        JsonObject? style = LoadStyleProjection(app)[widget.Id] as JsonObject;
        return Signature(widget, key, anchor, marginX, marginY, style);
    }

    private static string Signature(
        WidgetConfig widget,
        string key,
        string anchor,
        double marginX,
        double marginY,
        JsonObject? style)
    {
        var sb = new StringBuilder();
        sb.Append(key).Append('|')
            .Append(widget.WidgetKind).Append('|')
            .Append(widget.Name).Append('|')
            .Append(anchor).Append('|')
            .Append(Math.Round(marginX, 1)).Append('|')
            .Append(Math.Round(marginY, 1)).Append('|')
            .Append(Math.Round(widget.Width, 1)).Append('|')
            .Append(Math.Round(widget.Height, 1)).Append('|')
            .Append(widget.IsVisible ? '1' : '0').Append('|')
            .Append(style?.ToJsonString() ?? string.Empty);
        return sb.ToString();
    }

    private static JsonObject LoadStyleProjection(App app)
    {
        try
        {
            byte[] bytes = WidgetStyleBackupProjection.Serialize(app.SettingsService.Settings);
            JsonNode? node = JsonNode.Parse(bytes);
            return node?["widgets"] as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] style projection failed: " + ex.Message);
            return new JsonObject();
        }
    }

    private static JsonObject? ShellStyle(App app)
    {
        try
        {
            byte[] bytes = WidgetStyleBackupProjection.Serialize(app.SettingsService.Settings);
            JsonNode? node = JsonNode.Parse(bytes);
            return node?["shell"] as JsonObject;
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] shell style projection failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>悬浮球只同步外观。备注/剪贴板/常用短语/快捷方式/暂存目录
    /// 是内容，带本机路径和隐私，留在各自机器上。</summary>
    private static JsonObject DrawerAppearance()
    {
        var drawer = WidgetDrawerSettings.Load();
        var appearance = new JsonObject
        {
            ["glass"] = drawer.Glass,
            ["shape"] = drawer.Shape,
            ["size"] = drawer.Size,
            ["visible"] = drawer.Visible,
            ["idle_fade"] = drawer.IdleFade,
            ["auto_hide_fullscreen"] = drawer.AutoHideFullScreen
        };
        if (drawer.Custom is not null)
        {
            appearance["custom"] = new JsonObject
            {
                ["fill"] = drawer.Custom.Fill,
                ["opacity"] = drawer.Custom.Opacity,
                ["highlight"] = drawer.Custom.Highlight,
                ["rim"] = drawer.Custom.Rim
            };
        }

        return appearance;
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Trim()).Replace('/', '\\');
}
