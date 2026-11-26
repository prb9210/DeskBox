using Windows.Graphics;

namespace DeskBox.Services;

public static class WidgetCapsuleOrderCalculator
{
    public static IReadOnlyList<string> MoveToNearestSlot(
        IReadOnlyList<string> orderedIds,
        IReadOnlyList<RectInt32> slotBounds,
        string activeId,
        RectInt32 proposedBounds,
        string? direction)
    {
        int activeIndex = IndexOf(orderedIds, activeId);
        if (activeIndex < 0 || orderedIds.Count < 2)
        {
            return orderedIds.ToArray();
        }

        bool vertical = SettingsService.NormalizeWidgetCapsuleBarDirection(direction) ==
            SettingsService.WidgetCapsuleBarDirectionVertical;
        long proposedCenter = GetPrimaryCenter(proposedBounds, vertical);
        int nearestIndex = activeIndex;
        long nearestDistance = long.MaxValue;
        int slotCount = Math.Min(orderedIds.Count, slotBounds.Count);
        for (int index = 0; index < slotCount; index++)
        {
            RectInt32 slot = slotBounds[index];
            long distance = Math.Abs(proposedCenter - GetPrimaryCenter(slot, vertical));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        if (nearestIndex == activeIndex)
        {
            return orderedIds.ToArray();
        }

        var reordered = orderedIds.ToList();
        reordered.RemoveAt(activeIndex);
        reordered.Insert(nearestIndex, activeId);
        return reordered;
    }

    /// <summary>
    /// 按「拖动中心越过了几个格子的中心」决定插入位置（列表排序的通用做法），
    /// 而不是找最近的槽位中心：格子高度不等时，最近中心会让被拖的格子在同一位置
    /// 来回跳，用户很难把它停到想要的那一格。
    ///
    /// <paramref name="stickyGroupIds"/> 是一组必须连在一起的成员（分类文件夹
    /// 01…08）：组外成员不能插进它们中间，只能落在整组之前或之后；组内成员拖动
    /// 时只允许在组内换位。
    /// </summary>
    public static IReadOnlyList<string> MoveToPointer(
        IReadOnlyList<string> orderedIds,
        IReadOnlyList<RectInt32> slotBounds,
        string activeId,
        RectInt32 proposedBounds,
        string? direction,
        IReadOnlySet<string>? stickyGroupIds = null)
    {
        int activeIndex = IndexOf(orderedIds, activeId);
        if (activeIndex < 0 || orderedIds.Count < 2 || slotBounds.Count == 0)
        {
            return orderedIds.ToArray();
        }

        bool vertical = SettingsService.NormalizeWidgetCapsuleBarDirection(direction) ==
            SettingsService.WidgetCapsuleBarDirectionVertical;
        long proposedCenter = GetPrimaryCenter(proposedBounds, vertical);

        // 槽位在索引方向上是递增还是递减（贴底摆放时索引 0 在最下面）。
        bool ascending = slotBounds.Count <= 1 ||
            GetPrimaryCenter(slotBounds[1], vertical) >= GetPrimaryCenter(slotBounds[0], vertical);

        var others = new List<string>(orderedIds.Count);
        var otherCenters = new List<long>(orderedIds.Count);
        for (int index = 0; index < orderedIds.Count; index++)
        {
            if (index == activeIndex)
            {
                continue;
            }

            others.Add(orderedIds[index]);
            otherCenters.Add(index < slotBounds.Count
                ? GetPrimaryCenter(slotBounds[index], vertical)
                : (ascending ? long.MinValue : long.MaxValue));
        }

        int insertion = 0;
        while (insertion < otherCenters.Count &&
               (ascending
                   ? otherCenters[insertion] < proposedCenter
                   : otherCenters[insertion] > proposedCenter))
        {
            insertion++;
        }

        if (stickyGroupIds is { Count: > 0 })
        {
            insertion = ClampToStickyGroup(
                others,
                insertion,
                activeId,
                stickyGroupIds,
                otherCenters,
                proposedCenter,
                ascending);
        }

        var reordered = new List<string>(others);
        reordered.Insert(insertion, activeId);
        return reordered;
    }

    /// <summary>
    /// 把必须相邻的成员收成一段，插在它们原本第一次出现的位置。
    /// 用于修复已被其它功能格子分隔开的顺序（01…08 必须连在一起）。
    /// </summary>
    public static IReadOnlyList<string> GroupContiguous(
        IReadOnlyList<string> orderedIds,
        IReadOnlySet<string> stickyGroupIds)
    {
        if (stickyGroupIds.Count == 0)
        {
            return orderedIds.ToArray();
        }

        var group = new List<string>();
        var rest = new List<string>(orderedIds.Count);
        int insertAt = -1;
        foreach (string id in orderedIds)
        {
            if (stickyGroupIds.Contains(id))
            {
                if (insertAt < 0)
                {
                    insertAt = rest.Count;
                }

                group.Add(id);
            }
            else
            {
                rest.Add(id);
            }
        }

        if (group.Count == 0 || insertAt < 0)
        {
            return orderedIds.ToArray();
        }

        var result = new List<string>(orderedIds.Count);
        result.AddRange(rest.Take(insertAt));
        result.AddRange(group);
        result.AddRange(rest.Skip(insertAt));
        return result;
    }

    public static IReadOnlyList<string> MergeGroupOrder(
        IReadOnlyList<string> completeOrder,
        IReadOnlyList<string> groupOrder)
    {
        var groupIds = groupOrder.ToHashSet(StringComparer.Ordinal);
        var result = completeOrder.ToArray();
        int groupIndex = 0;
        for (int index = 0; index < result.Length && groupIndex < groupOrder.Count; index++)
        {
            if (groupIds.Contains(result[index]))
            {
                result[index] = groupOrder[groupIndex++];
            }
        }

        return result;
    }

    private static int ClampToStickyGroup(
        IReadOnlyList<string> others,
        int insertion,
        string activeId,
        IReadOnlySet<string> stickyGroupIds,
        IReadOnlyList<long> centers,
        long proposedCenter,
        bool ascending)
    {
        int first = -1;
        int last = -1;
        for (int index = 0; index < others.Count; index++)
        {
            if (!stickyGroupIds.Contains(others[index]))
            {
                continue;
            }

            if (first < 0)
            {
                first = index;
            }

            last = index;
        }

        if (first < 0)
        {
            return insertion;
        }

        if (stickyGroupIds.Contains(activeId))
        {
            // 组内换位：只允许落在组内（first … last + 1）。
            return Math.Clamp(insertion, first, last + 1);
        }

        // 组外成员不允许插进组中间：按指针离哪一端近，吸附到组前或组后。
        if (insertion <= first || insertion > last)
        {
            return insertion;
        }

        long blockCenter = (centers[first] + centers[last]) / 2;
        bool beforeBlock = ascending
            ? proposedCenter < blockCenter
            : proposedCenter > blockCenter;
        return beforeBlock ? first : last + 1;
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static long GetPrimaryCenter(RectInt32 bounds, bool vertical) => vertical
        ? (long)bounds.Y * 2 + bounds.Height
        : (long)bounds.X * 2 + bounds.Width;
}
