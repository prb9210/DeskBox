using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeskBox.Models;

namespace DeskBox.Services.Timetable;

/// <summary>一条待发的上课提醒。</summary>
public sealed record TimetableReminderCandidate(
    string Key,
    string Title,
    string Message,
    TimetableClass Class,
    TimetableCell Cell,
    TimetablePeriod Period,
    int MinutesUntil);

/// <summary>
/// 上课提醒的判定逻辑（纯函数，不碰 UI、不碰文件），便于单测与「同一套口径」复核：
///
/// 1. 只在教学日、且在作息表内的节次上提醒；
/// 2. 只在「距上课 0 &lt; 剩余 &lt;= 提前量」时提醒，同一 (日期, 节次, 班) 只提醒一次；
/// 3. 范围默认「我任教的班」，避免整张总表每节课都响；
/// 4. 停课日名单里的日期整天不提醒。
/// </summary>
public static class TimetableReminderPlanner
{
    public static TimetableReminderCandidate? Evaluate(
        TimetableDocument document,
        TimetableLocalSettings settings,
        DateTimeOffset now,
        ISet<string> alreadyNotified)
    {
        if (document.IsEmpty || !settings.ReminderEnabled)
        {
            return null;
        }

        int day = TimetableSchedule.DayIndex(now);
        if (day < 0)
        {
            return null;
        }

        string today = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (IsClosed(settings.ClosedDates, today))
        {
            return null;
        }

        int lead = Math.Clamp(settings.LeadMinutes, 0, 60);
        int nowMinutes = (now.Hour * 60) + now.Minute;

        IReadOnlyList<TimetableClass> scope = ResolveScope(document, settings);

        TimetableReminderCandidate? best = null;
        foreach (TimetablePeriod period in settings.Periods)
        {
            int start = TimetableSchedule.ParseMinutes(period.Start);
            if (start < 0)
            {
                continue;
            }

            int remaining = start - nowMinutes;
            if (remaining <= 0 || remaining > lead)
            {
                continue;
            }

            foreach (TimetableClass item in scope)
            {
                TimetableCell? cell = item.CellAt(day, period.Index);
                if (cell is null)
                {
                    continue;
                }

                string key = today + "|" + period.Index.ToString(CultureInfo.InvariantCulture) + "|" + item.Id;
                if (alreadyNotified.Contains(key))
                {
                    continue;
                }

                string title = remaining <= 1
                    ? "马上上课：" + item.Name + " 第" + period.Index + "节"
                    : remaining + " 分钟后上课：" + item.Name + " 第" + period.Index + "节";
                string message = BuildMessage(cell, period);

                TimetableReminderCandidate candidate = new(
                    key,
                    title,
                    message,
                    item,
                    cell,
                    period,
                    remaining);

                if (best is null || period.Index < best.Period.Index)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static string BuildMessage(TimetableCell cell, TimetablePeriod period)
    {
        StringBuilder builder = new();
        builder.Append(cell.Subject);
        if (!string.IsNullOrEmpty(cell.Teacher))
        {
            builder.Append(" · ").Append(cell.Teacher);
        }

        if (!string.IsNullOrEmpty(cell.Room))
        {
            builder.Append(" · ").Append(cell.Room);
        }

        if (period.Start.Length > 0)
        {
            builder.Append("　").Append(period.Start).Append('–').Append(period.End);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TimetableClass> ResolveScope(
        TimetableDocument document,
        TimetableLocalSettings settings)
    {
        switch (settings.Scope)
        {
            case TimetableReminderScope.AllClasses:
                return document.Classes;

            case TimetableReminderScope.CurrentClass:
            {
                TimetableClass? current = document.FindClass(settings.ActiveClassId) ?? document.DefaultClass;
                return current is null ? [] : [current];
            }

            default:
                return document.MyClasses;
        }
    }

    private static bool IsClosed(IReadOnlyList<string> closedDates, string today)
    {
        for (int i = 0; i < closedDates.Count; i++)
        {
            string value = closedDates[i].Replace("-", string.Empty, StringComparison.Ordinal).Trim();
            if (string.Equals(value, today, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>格子里「下一节」的文案，界面与提醒共用同一份措辞。</summary>
    public static string? DescribeNextLesson(
        TimetableDocument document,
        TimetableClass? currentClass,
        IReadOnlyList<TimetablePeriod> periods,
        DateTimeOffset now,
        out int minutesUntil)
    {
        minutesUntil = 0;
        if (currentClass is null)
        {
            return null;
        }

        int day = TimetableSchedule.DayIndex(now);
        if (day < 0)
        {
            return null;
        }

        int nowMinutes = (now.Hour * 60) + now.Minute;
        TimetablePeriod? best = null;
        TimetableCell? bestCell = null;
        foreach (TimetablePeriod period in periods)
        {
            int start = TimetableSchedule.ParseMinutes(period.Start);
            if (start <= nowMinutes)
            {
                continue;
            }

            TimetableCell? cell = currentClass.CellAt(day, period.Index);
            if (cell is null)
            {
                continue;
            }

            if (best is null || start < TimetableSchedule.ParseMinutes(best.Start))
            {
                best = period;
                bestCell = cell;
            }
        }

        if (best is null || bestCell is null)
        {
            return null;
        }

        minutesUntil = Math.Max(0, TimetableSchedule.ParseMinutes(best.Start) - nowMinutes);
        string text = "第" + best.Index + "节 " + bestCell.Subject;
        if (!string.IsNullOrEmpty(bestCell.Teacher))
        {
            text += " · " + bestCell.Teacher;
        }

        return text + "　" + best.Start;
    }
}
