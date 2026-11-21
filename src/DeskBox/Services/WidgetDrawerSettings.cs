using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

/// <summary>One selectable drawer-ball colour. Every preset shares the same
/// glass treatment and 1px rim, so no choice can look out of place.</summary>
public sealed record DrawerColorPreset(string Id, string Name, string Start, string End, string Rim);

/// <summary>
/// Persisted state for the widget drawer: where the ball sits, which colour it
/// wears, and which widgets the user parked. Kept in its own file so the
/// settings contract (and its restore/migration paths) stay untouched.
/// </summary>
public sealed class WidgetDrawerSettings
{
    public const string FollowThemeId = "theme";

    /// <summary>Presets ordered as they appear in the menu. Colours are the
    /// Fluent accent family so they harmonise with the app and Windows alike.</summary>
    public static readonly IReadOnlyList<DrawerColorPreset> Presets = new[]
    {
        new DrawerColorPreset(FollowThemeId, "跟随主题", "#3B82F6", "#1D4ED8", "#93C5FD"),
        new DrawerColorPreset("blue", "蓝", "#3B82F6", "#1D4ED8", "#93C5FD"),
        new DrawerColorPreset("cyan", "青", "#06B6D4", "#0E7490", "#A5F3FC"),
        new DrawerColorPreset("green", "绿", "#10B981", "#047857", "#A7F3D0"),
        new DrawerColorPreset("purple", "紫", "#8B5CF6", "#6D28D9", "#DDD6FE"),
        new DrawerColorPreset("orange", "橙", "#F97316", "#C2410C", "#FED7AA"),
        new DrawerColorPreset("pink", "粉", "#EC4899", "#BE185D", "#FBCFE8"),
        new DrawerColorPreset("slate", "灰", "#64748B", "#334155", "#CBD5E1")
    };

    [JsonPropertyName("color")]
    public string Color { get; set; } = FollowThemeId;

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("collapsed_widgets")]
    public List<string> CollapsedWidgetIds { get; set; } = new();

    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath();

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "drawer.json");

    public static WidgetDrawerSettings Load(string? path = null)
    {
        string target = path ?? DefaultPath();
        try
        {
            if (File.Exists(target))
            {
                WidgetDrawerSettings? loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(target), WidgetDrawerJsonContext.Default.WidgetDrawerSettings);
                if (loaded is not null)
                {
                    loaded.Path = target;
                    loaded.CollapsedWidgetIds ??= new List<string>();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] settings load failed: {ex.Message}");
        }

        return new WidgetDrawerSettings { Path = target };
    }

    public void Save()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Source-generated context: the reflection overloads raise
            // IL2026/IL3050, which the AOT retail publish treats as failures.
            File.WriteAllText(Path, JsonSerializer.Serialize(
                this, WidgetDrawerJsonContext.Default.WidgetDrawerSettings));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] settings save failed: {ex.Message}");
        }
    }

    public DrawerColorPreset ResolveColor() => Find(Color) ?? Presets[0];

    public static DrawerColorPreset? Find(string id)
    {
        foreach (DrawerColorPreset preset in Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }

    public bool IsCollapsed(string widgetId) => CollapsedWidgetIds.Contains(widgetId);

    public void SetCollapsed(string widgetId, bool collapsed)
    {
        bool present = CollapsedWidgetIds.Contains(widgetId);
        if (collapsed && !present)
        {
            CollapsedWidgetIds.Add(widgetId);
        }
        else if (!collapsed && present)
        {
            CollapsedWidgetIds.Remove(widgetId);
        }
    }
}

/// <summary>Source-generated serialization for the drawer state file.</summary>
[JsonSerializable(typeof(WidgetDrawerSettings))]
internal sealed partial class WidgetDrawerJsonContext : JsonSerializerContext
{
}
