using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 把对端推来的「全局格子样式」写回本机设置。键名与
/// <see cref="WidgetStyleBackupProjection.ShellKeys"/> 一一对应——白名单只有
/// 一份，这里只负责赋值，不再维护第二张表。
///
/// 一律通过 <c>Settings.WidgetShell.*</c> 切片写入：facade 属性
/// （<c>Settings.WidgetOpacity</c> 之类）有契约测试按文件计数，新增文件里
/// 出现一次就会红。解析只用 JsonValue.TryGetValue，不走 JsonSerializer——
/// Native AOT 下未知类型的序列化会撞 IL2026/IL3050 告警闸门。
/// </summary>
internal static class ShellStyleApplier
{
    public static int Apply(AppSettings appSettings, JsonObject shell)
    {
        int applied = 0;
        WidgetShellSettingsSlice s = appSettings.WidgetShell;
        foreach ((string key, JsonNode? node) in shell)
        {
            switch (key)
            {
                case "widgetOpacity" when Num(node) is double v:
                    s.WidgetOpacity = Clamp01(v);
                    break;
                case "widgetMaterialType" when Str(node) is string v:
                    s.WidgetMaterialType = v;
                    break;
                case "widgetMaterialIntensity" when Num(node) is double v:
                    s.WidgetMaterialIntensity = Clamp01(v);
                    break;
                case "widgetForegroundMode" when Str(node) is string v:
                    s.WidgetForegroundMode = v;
                    break;
                case "widgetForegroundColor" when Str(node) is string v:
                    s.WidgetForegroundColor = v;
                    break;
                case "widgetBorderColorMode" when Str(node) is string v:
                    s.WidgetBorderColorMode = v;
                    break;
                case "widgetBorderStyle" when Str(node) is string v:
                    s.WidgetBorderStyle = v;
                    break;
                case "widgetCornerPreference" when Str(node) is string v:
                    s.WidgetCornerPreference = v;
                    break;
                case "widgetAnimationEffect" when Str(node) is string v:
                    s.WidgetAnimationEffect = v;
                    break;
                case "widgetAnimationSpeed" when Str(node) is string v:
                    s.WidgetAnimationSpeed = v;
                    break;
                case "widgetAnimationSlideDirection" when Str(node) is string v:
                    s.WidgetAnimationSlideDirection = v;
                    break;
                case "widgetAnimationEasingIntensity" when Str(node) is string v:
                    s.WidgetAnimationEasingIntensity = v;
                    break;
                case "widgetAnimationStaggerEnabled" when Bool(node) is bool v:
                    s.WidgetAnimationStaggerEnabled = v;
                    break;
                case "displayWidgetChromeMode" when Str(node) is string v:
                    s.DisplayWidgetChromeMode = v;
                    break;
                case "interactiveWidgetChromeMode" when Str(node) is string v:
                    s.InteractiveWidgetChromeMode = v;
                    break;
                case "widgetTitleIconMode" when Str(node) is string v:
                    s.WidgetTitleIconMode = v;
                    break;
                case "showHoverButtons" when Bool(node) is bool v:
                    s.ShowHoverButtons = v;
                    break;
                case "widgetHoverButtonActions" when Str(node) is string v:
                    s.WidgetHoverButtonActions = v;
                    break;
                case "widgetCollapsedStyle" when Str(node) is string v:
                    s.WidgetCollapsedStyle = v;
                    break;
                case "widgetCompactContentMode" when Str(node) is string v:
                    s.WidgetCompactContentMode = v;
                    break;
                case "widgetCompactHideSensitiveContent" when Bool(node) is bool v:
                    s.WidgetCompactHideSensitiveContent = v;
                    break;
                case "widgetCompactMediaCornerMode" when Str(node) is string v:
                    s.WidgetCompactMediaCornerMode = v;
                    break;
                case "widgetCompactAnimationEffect" when Str(node) is string v:
                    s.WidgetCompactAnimationEffect = v;
                    break;
                case "widgetCompactAnimationDurationMs" when Int(node) is int v:
                    s.WidgetCompactAnimationDurationMs = Math.Clamp(v, 0, 5000);
                    break;
                default:
                    continue;
            }

            applied++;
        }

        return applied;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static double? Num(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double d)
            ? d
            : null;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue(out string? s)
            ? s
            : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int i)
            ? i
            : null;
}
