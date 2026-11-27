using System;
using System.Collections.Generic;

namespace DeskBox.Services;

/// <summary>
/// What a fan sector does. The ring used to be a list of widget on/off
/// switches, which is a configuration job — it belongs in settings, not on the
/// one surface the user sees every day. Sectors are now verbs instead: files
/// can be dropped on them, and a plain click runs the default action.
/// </summary>
public enum DrawerFanKind
{
    /// <summary>拖任意文件 → 资料库\_暂存\日期；点击 → 打开暂存。</summary>
    Stage,

    /// <summary>拖任意文件 → 资料库\_待归类；点击 → 打开待归类。</summary>
    Pending,

    /// <summary>点击 → 立刻整理桌面；不接受拖入（整理的是桌面，不是某个文件）。</summary>
    Organize,

    /// <summary>点击 → 全屏截图并复制；不接受拖入。</summary>
    Screenshot,
}

public sealed record DrawerFanAction(
    string Id,
    string Title,
    DrawerFanKind Kind,
    bool AcceptsFiles,
    string Hint);

/// <summary>
/// The ring's fixed action list. Deliberately small: four 90° sectors read far
/// better on a 56 DIP bead than eight 45° slivers, and every one of them is a
/// real action rather than a settings toggle.
/// </summary>
public static class DrawerFanActions
{
    public static IReadOnlyList<DrawerFanAction> All { get; } = new[]
    {
        new DrawerFanAction("stage", "暂存", DrawerFanKind.Stage, true, "移到 资料库\\_暂存"),
        new DrawerFanAction("pending", "待定", DrawerFanKind.Pending, true, "移到 资料库\\_待归类"),
        new DrawerFanAction("organize", "整理", DrawerFanKind.Organize, false, "立刻整理桌面"),
        new DrawerFanAction("screenshot", "截图", DrawerFanKind.Screenshot, false, "全屏截图并复制"),
    };

    /// <summary>Dropping on the bead itself (or in a gap) falls back to staging,
    /// which is what the ball did before sectors existed.</summary>
    public static DrawerFanAction Default => All[0];

    public static DrawerFanAction? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (DrawerFanAction action in All)
        {
            if (string.Equals(action.Id, id, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return null;
    }
}
