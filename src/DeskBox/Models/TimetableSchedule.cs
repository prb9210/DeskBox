using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeskBox.Models;

/// <summary>
/// 作息时间与「现在上哪节 / 下一节是哪个」的纯计算。不碰 UI、不读文件，
/// 提醒服务与格子视图共用同一套判断，避免两处口径漂移。
/// </summary>
public static class TimetableSchedule
{
    /// <summary>
    /// 学校默认作息（上午 4 节 / 下午 4 节）。真实时间由用户在设置里改，
    /// 这里只保证首次导入就有可用值，且不会因为空数组导致除零/越界。
    /// </summary>
    public static IReadOnlyList<TimetablePeriod> DefaultPeriods { get; } =
    [
        new(1, "07:40", "08:20", "上午"),
        new(2, "08:30", "09:10", "上午"),
        new(3, "09:35", "10:15", "上午"),
        new(4, "10:25", "11:05", "上午"),
        new(5, "14:30", "15:10", "下午"),
        new(6, "15:20", "16:00", "下午"),
        new(7, "16:20", "17:00", "下午"),
        new(8, "17:10", "17:50", "下午")
    ];

    /// <summary>星期索引（0 = 周一）到数组下标；周末返回 -1。</summary>
    public static int DayIndex(DateTimeOffset moment)
    {
        return moment.DayOfWeek switch
        {
            DayOfWeek.Monday => 0,
            DayOfWeek.Tuesday => 1,
            DayOfWeek.Wednesday => 2,
            DayOfWeek.Thursday => 3,
            DayOfWeek.Friday => 4,
            _ => -1
        };
    }

    /// <summary>把 "07:40" 解析成当日分钟数；解析不出来返回 -1。</summary>
    public static int ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }

        string trimmed = text.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return -1;
        }

        if (!int.TryParse(trimmed.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour))
        {
            return -1;
        }

        if (!int.TryParse(trimmed.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute))
        {
            return -1;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return -1;
        }

        return (hour * 60) + minute;
    }

    public static bool TryGetPeriod(IReadOnlyList<TimetablePeriod> periods, int index, out TimetablePeriod period)
    {
        for (int i = 0; i < periods.Count; i++)
        {
            if (periods[i].Index == index)
            {
                period = periods[i];
                return true;
            }
        }

        period = new TimetablePeriod(index, string.Empty, string.Empty, string.Empty);
        return false;
    }

    /// <summary>某天某节是否在时间窗内（用于「现在」高亮）。</summary>
    public static bool IsRunning(TimetablePeriod period, DateTimeOffset moment, out int remainingMinutes)
    {
        int start = ParseMinutes(period.Start);
        int end = ParseMinutes(period.End);
        int now = (moment.Hour * 60) + moment.Minute;
        remainingMinutes = 0;
        if (start < 0 || end <= start)
        {
            return false;
        }

        if (now < start || now >= end)
        {
            return false;
        }

        remainingMinutes = end - now;
        return true;
    }

    /// <summary>
    /// 找出「下一节」：当日尚未开始的最近一节。当天已全部结束时返回 null。
    /// 只看作息表，不看有没有课——有没有课由调用方按班级判断。
    /// </summary>
    public static TimetablePeriod? NextPeriod(IReadOnlyList<TimetablePeriod> periods, DateTimeOffset moment)
    {
        int now = (moment.Hour * 60) + moment.Minute;
        TimetablePeriod? best = null;
        for (int i = 0; i < periods.Count; i++)
        {
            int start = ParseMinutes(periods[i].Start);
            if (start < 0 || start <= now)
            {
                continue;
            }

            if (best is null || ParseMinutes(best.Start) > start)
            {
                best = periods[i];
            }
        }

        return best;
    }

    public static TimetablePeriod? RunningPeriod(IReadOnlyList<TimetablePeriod> periods, DateTimeOffset moment)
    {
        for (int i = 0; i < periods.Count; i++)
        {
            if (IsRunning(periods[i], moment, out _))
            {
                return periods[i];
            }
        }

        return null;
    }

    /// <summary>
    /// 课表格子的推荐默认尺寸（DIP）。周视图要放下 5 列 × 8 行，
    /// 5 列每列约 92、8 行每行约 34，加上表头与底栏。
    /// </summary>
    public const double DefaultWidth = 520;

    public const double DefaultHeight = 430;
}
