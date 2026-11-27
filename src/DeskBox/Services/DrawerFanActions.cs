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

    /// <summary>拖图片 → 扫描成增强 PDF（坚果云\扫描资料）；点击 → 打开扫描资料。</summary>
    Scan,

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
        new DrawerFanAction("scan", "扫描", DrawerFanKind.Scan, true, "把图片扫成 PDF"),
        new DrawerFanAction("stage", "暂存", DrawerFanKind.Stage, true, "移到 资料库\\_暂存"),
        new DrawerFanAction("pending", "待定", DrawerFanKind.Pending, true, "移到 资料库\\_待归类"),
        new DrawerFanAction("organize", "整理", DrawerFanKind.Organize, false, "立刻整理桌面"),
        new DrawerFanAction("screenshot", "截图", DrawerFanKind.Screenshot, false, "全屏截图并复制"),
    };

    /// <summary>Dropping on the bead itself (or in a gap) falls back to staging,
    /// which is what the ball did before sectors existed. Deliberately not
    /// All[0]: the first sector is 扫描 now, and a dropped .docx must not be
    /// dragged into the image pipeline.</summary>
    public static DrawerFanAction Default => Find("stage") ?? All[0];

    /// <summary>Extensions the engine can decode itself; anything else has to be
    /// transcoded to JPEG first (HEIC from an iPhone, BMP/TIFF from a scanner).</summary>
    public static bool IsImage(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
            case ".png":
            case ".gif":
            case ".bmp":
            case ".tif":
            case ".tiff":
            case ".heic":
            case ".heif":
            case ".webp":
                return true;
            default:
                return false;
        }
    }

    /// <summary>True when the engine's Go decoders handle the file as-is.</summary>
    public static bool IsDirectImage(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
            case ".png":
            case ".gif":
                return true;
            default:
                return false;
        }
    }

    /// <summary>扇片 0 在正上方，其余顺时针排（与扇片的绘制角度同一套坐标）。</summary>
    public static double MidAngle(int index, int count) =>
        count <= 0 ? 0 : -90 + ((360.0 / count) * index);

    /// <summary>
    /// 角度 → 扇片序号。单独抽出来是为了能在 Linux 上跑单测：拖放命中
    /// 只看「松手时的角度 + 半径」，与窗口、DPI、扇片视觉都无关。
    /// </summary>
    public static int IndexAt(double degrees, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        double step = 360.0 / count;
        double fromTop = (((degrees + 90) % 360) + 360) % 360;
        int index = (int)Math.Floor((fromTop + (step / 2)) / step);
        return ((index % count) + count) % count;
    }

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
