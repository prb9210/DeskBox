using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 一列格子必须同宽（用户口径：无论增加或关闭功能格子，所有格子都保持
/// 用户设定的那个宽度，不能有的长有的短）。
///
/// 对齐宽度模式（默认）下，胶囊宽度直接取各自的 <see cref="WidgetConfig.Width"/>，
/// 而每个格子（分类格子、待办、音乐、天气、搜索…）的展开宽度是各自创建时的默认值，
/// 于是「打开音乐」这类动作把新格子并入右侧列后，列里就出现多种宽度。
/// 共用排版虽然会按最宽值摆位，但它只约束窗口这一次的尺寸，从不把宽度写回配置，
/// 下一次重排又按各自 Width 算回去 —— 这才是「怎么调都参差不齐」的根因。
///
/// 这里把列宽当成列级属性：以 <see cref="MetadataKey"/> 记在成员的 Metadata 上
/// （随 widget-layout.json 持久化，不动设置 schema）。规则：
/// <list type="bullet">
/// <item>已有记录时以记录为列宽 —— 新加入的格子（没有记录）一律服从它，不会把整列撑宽；</item>
/// <item>某个成员自己的宽度与记录不同，说明是用户刚拖过它的宽度 —— 以它为新的列宽，
/// 并同步给全列（实时跟随）；</item>
/// <item>整列都没有记录时取最宽者作为初始列宽。</item>
/// </list>
/// </summary>
internal static class WidgetColumnWidthPolicy
{
    /// <summary>列宽的持久化键（WidgetConfig.Metadata）。</summary>
    public const string MetadataKey = "CapsuleColumnWidth";

    private const double Tolerance = 1.0;

    /// <summary>
    /// 解析并写回整列的统一宽度。仅在「对齐宽度」模式下调用；自适应模式沿用
    /// <see cref="WidgetConfig.CompactWidth"/> 的既有逻辑。
    /// </summary>
    /// <returns>是否有成员的配置被改动（调用方据此决定要不要保存）。</returns>
    public static bool ApplyAligned(IReadOnlyList<WidgetConfig> members)
    {
        if (members.Count == 0)
        {
            return false;
        }

        double width = ResolveAlignedWidth(members);
        if (!(width > 0) || !double.IsFinite(width))
        {
            return false;
        }

        string text = width.ToString("0.##", CultureInfo.InvariantCulture);
        bool changed = false;
        foreach (WidgetConfig member in members)
        {
            if (!NearlyEqual(member.Width, width))
            {
                member.Width = width;
                changed = true;
            }

            // 对齐模式忽略 CompactWidth；残留值会在切回自适应模式时突然生效。
            if (member.CompactWidth is not null)
            {
                member.CompactWidth = null;
                changed = true;
            }

            if (!member.Metadata.TryGetValue(MetadataKey, out string? current) ||
                !string.Equals(current, text, StringComparison.Ordinal))
            {
                member.Metadata[MetadataKey] = text;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>列宽：用户刚调整过的那一份优先，其次取已记录的列宽，最后取最宽成员。</summary>
    public static double ResolveAlignedWidth(IReadOnlyList<WidgetConfig> members)
    {
        double? stored = null;
        double? userAdjusted = null;
        double widest = 0;
        foreach (WidgetConfig member in members)
        {
            double own = member.Width;
            if (double.IsFinite(own) && own > widest)
            {
                widest = own;
            }

            if (!TryReadStored(member, out double storedForMember))
            {
                continue;
            }

            stored = stored is null ? storedForMember : Math.Max(stored.Value, storedForMember);
            // 用户拖动宽度后：配置里的 Width 与记录值不再一致（同一次重排里
            // 我们只改宽度、不改记录，所以这个差值只可能来自用户手势）。
            if (double.IsFinite(own) && Math.Abs(own - storedForMember) > Tolerance)
            {
                userAdjusted = userAdjusted is null ? own : Math.Max(userAdjusted.Value, own);
            }
        }

        double resolved = userAdjusted ?? stored ?? widest;
        return Math.Clamp(
            resolved,
            WidgetCompactBoundsCalculator.MinWidth,
            WidgetCompactBoundsCalculator.MaxAlignedWidth);
    }

    private static bool TryReadStored(WidgetConfig member, out double width)
    {
        width = 0;
        return member.Metadata.TryGetValue(MetadataKey, out string? raw) &&
            double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out width) &&
            double.IsFinite(width) &&
            width > 0;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.01;
}
