using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

/// <summary>
/// 一款玻璃主题。Fill 为 null 表示「跟随格子」——底色取格子的材料色（深色主题
/// #202020 / 浅色主题 #F3F3F3），不透明度取格子的 0.80；这也是默认项。
/// Highlight 是顶部高光强度，RimOpacity 是描边不透明度，LightText 表示浅色字。
/// </summary>
public sealed record DrawerGlassPreset(
    string Id,
    string Name,
    string? Fill,
    double Opacity,
    double Highlight,
    double RimOpacity,
    bool LightText);

/// <summary>悬浮球上的一条备忘。存在 drawer.json 里，双机各记各的。</summary>
public sealed class DrawerNote
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public string Created { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

/// <summary>「我的常用」里的一个入口：目录、文件、网址或程序。</summary>
public sealed class DrawerShortcut
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;
}

/// <summary>自定义玻璃的四根滑杆。</summary>
public sealed class DrawerCustomGlass
{
    [JsonPropertyName("fill")]
    public string Fill { get; set; } = "#202020";

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.80;

    [JsonPropertyName("highlight")]
    public double Highlight { get; set; } = 0.12;

    [JsonPropertyName("rim")]
    public double Rim { get; set; } = 0.35;
}

/// <summary>
/// Persisted state for the widget drawer: where the ball sits, its glass theme,
/// shape, whether it is shown at all, parked widgets, notes and shortcuts.
/// Kept in its own file so the settings contract (and its restore/migration
/// paths) stay untouched.
/// </summary>
public sealed class WidgetDrawerSettings
{
    /// <summary>默认玻璃主题：与格子同材质的烟熏玻璃。</summary>
    public const string DefaultGlassId = "smoke";

    /// <summary>默认收起尺寸（完整球 56 DIP）。</summary>
    public const double DefaultBallDiameter = 56;

    /// <summary>剪贴板历史保留条数。</summary>
    public const int MaxClipboardEntries = 20;

    /// <summary>六款玻璃主题 + 自定义。纯色预设已全部删除——用户明确不要高饱和实心球。</summary>
    public static readonly IReadOnlyList<DrawerGlassPreset> Glasses = new[]
    {
        // Fill=null → 跟随格子材料底（深色 #202020 / 浅色 #F3F3F3），0.80 不透明度。
        new DrawerGlassPreset("smoke", "烟熏（跟随格子）", null, 0.80, 0.12, 0.35, true),
        new DrawerGlassPreset("mist", "雾面（浅色桌面）", "#F4F5F8", 0.62, 0.28, 0.30, false),
        new DrawerGlassPreset("ink", "墨蓝", "#0F172A", 0.78, 0.14, 0.32, true),
        new DrawerGlassPreset("warm", "暖灰", "#1C1917", 0.78, 0.12, 0.30, true),
        new DrawerGlassPreset("outline", "线框", "#000000", 0.18, 0.06, 0.78, true),
        new DrawerGlassPreset("pearl", "玻璃珠", "#232A33", 0.86, 0.46, 0.52, true),
        new DrawerGlassPreset("custom", "自定义", "#202020", 0.80, 0.12, 0.35, true)
    };

    /// <summary>形态：完整球 / 大球 / 小球 / 小圆点 / 贴边条。</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Shapes = new[]
    {
        ("ball", "完整球"),
        ("large", "大球（触摸屏）"),
        ("small", "小球"),
        ("dot", "小圆点"),
        ("strip", "贴边条")
    };

    [JsonPropertyName("glass")]
    public string Glass { get; set; } = DefaultGlassId;

    /// <summary>旧版纯色 id（blue/green/…）；保留读入只为迁移，界面不再使用。</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;

    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "ball";

    /// <summary>球直径（DIP）。形态选择会写这个值，设置页也能单独调。</summary>
    [JsonPropertyName("size")]
    public double Size { get; set; } = DefaultBallDiameter;

    /// <summary>闲置 8 秒后淡到 35%，鼠标靠近恢复。</summary>
    [JsonPropertyName("idle_fade")]
    public bool IdleFade { get; set; } = true;

    /// <summary>全屏应用（投影、视频）在前台时自动隐藏球。</summary>
    [JsonPropertyName("auto_hide_fullscreen")]
    public bool AutoHideFullScreen { get; set; }

    /// <summary>记录最近复制过的文字（最多 20 条），球上一键再贴。</summary>
    [JsonPropertyName("clipboard_history")]
    public bool ClipboardHistory { get; set; } = true;

    [JsonPropertyName("clipboard")]
    public List<string> Clipboard { get; set; } = new();

    [JsonPropertyName("custom")]
    public DrawerCustomGlass Custom { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<DrawerNote> Notes { get; set; } = new();

    [JsonPropertyName("shortcuts")]
    public List<DrawerShortcut> Shortcuts { get; set; } = new();

    /// <summary>常用短语，一行一条；右键子菜单里点一下复制。</summary>
    [JsonPropertyName("phrases")]
    public List<string> Phrases { get; set; } = new();

    /// <summary>暂存目录；空表示用 &lt;资料库&gt;\_暂存。</summary>
    [JsonPropertyName("staging_dir")]
    public string? StagingDir { get; set; }

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
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] settings load failed: {ex.Message}");
        }

        var created = new WidgetDrawerSettings { Path = target };
        created.Normalize();
        return created;
    }

    /// <summary>
    /// 旧配置迁移：color=blue/green/... 一律回落到烟熏；未知形态回落到完整球；
    /// 尺寸钳在合理范围。每次 Load 后跑一遍，坏配置不会让球消失。
    /// </summary>
    public void Normalize()
    {
        CollapsedWidgetIds ??= new List<string>();
        Notes ??= new List<DrawerNote>();
        Shortcuts ??= new List<DrawerShortcut>();
        Phrases ??= new List<string>();
        Clipboard ??= new List<string>();
        Custom ??= new DrawerCustomGlass();

        if (Clipboard.Count > MaxClipboardEntries)
        {
            Clipboard.RemoveRange(MaxClipboardEntries, Clipboard.Count - MaxClipboardEntries);
        }

        if (FindGlass(Glass) is null)
        {
            Glass = DefaultGlassId;
        }

        if (!string.Equals(Shape, "ball", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Shape, "large", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Shape, "small", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Shape, "dot", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Shape, "strip", StringComparison.OrdinalIgnoreCase))
        {
            Shape = "ball";
        }

        if (Size < 28 || Size > 96)
        {
            Size = DefaultBallDiameter;
        }

        if (Shortcuts.Count == 0)
        {
            Shortcuts.AddRange(DefaultShortcuts());
        }
    }

    /// <summary>首次运行时的「我的常用」：占位目标由宿主解析成真实路径。</summary>
    public static IEnumerable<DrawerShortcut> DefaultShortcuts()
    {
        yield return new DrawerShortcut { Name = "资料库", Target = "@library" };
        yield return new DrawerShortcut { Name = "待归类", Target = "@library\\_待归类" };
        yield return new DrawerShortcut { Name = "暂存", Target = "@staging" };
        yield return new DrawerShortcut { Name = "下载", Target = "@downloads" };
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

    /// <summary>把磁盘上那份的设置抄进当前实例（位置坐标不抄，避免拖动中被覆盖）。</summary>
    public void CopyFrom(WidgetDrawerSettings other)
    {
        Glass = other.Glass;
        Color = other.Color;
        Visible = other.Visible;
        Shape = other.Shape;
        Size = other.Size;
        IdleFade = other.IdleFade;
        AutoHideFullScreen = other.AutoHideFullScreen;
        ClipboardHistory = other.ClipboardHistory;
        Clipboard = other.Clipboard;
        Custom = other.Custom;
        Notes = other.Notes;
        Shortcuts = other.Shortcuts;
        Phrases = other.Phrases;
        StagingDir = other.StagingDir;
        // 设置页的「格子显示」勾选写的就是这份列表，必须跟着 disk 走一遍，
        // 否则主进程还是旧列表，勾了也不生效。
        CollapsedWidgetIds = other.CollapsedWidgetIds;
    }

    public DrawerGlassPreset ResolveGlass()
    {
        DrawerGlassPreset? preset = FindGlass(Glass);
        if (preset is null)
        {
            return Glasses[0];
        }

        if (preset.Id == "custom")
        {
            return preset with
            {
                Fill = Custom.Fill,
                Opacity = Custom.Opacity,
                Highlight = Custom.Highlight,
                RimOpacity = Custom.Rim
            };
        }

        return preset;
    }

    public static DrawerGlassPreset? FindGlass(string id)
    {
        // 旧版纯色 id（cyan/blue/green/…）没有对应玻璃，统一回落到默认烟熏。
        foreach (DrawerGlassPreset preset in Glasses)
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

    public int OpenNoteCount()
    {
        int count = 0;
        foreach (DrawerNote note in Notes)
        {
            if (!note.Done)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>Source-generated serialization for the drawer state file.</summary>
[JsonSerializable(typeof(WidgetDrawerSettings))]
[JsonSerializable(typeof(List<DrawerNote>))]
[JsonSerializable(typeof(List<DrawerShortcut>))]
internal sealed partial class WidgetDrawerJsonContext : JsonSerializerContext
{
}
