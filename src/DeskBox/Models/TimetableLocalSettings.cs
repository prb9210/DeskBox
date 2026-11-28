using System;
using System.Collections.Generic;

namespace DeskBox.Models;

/// <summary>提醒范围。默认只提醒「我任教的班」，避免整张总表每节课都响。</summary>
public enum TimetableReminderScope
{
    MyClasses = 0,
    CurrentClass = 1,
    AllClasses = 2
}

/// <summary>
/// 本机设置（不随库同步）：看哪个班、提醒开关、作息时间、停课日。
/// 刻意不进 AppSettings —— 设置 schema 已冻结，新增透传字段会撞四组契约测试，
/// 而课程表只需要自己的一个小文件。
/// </summary>
public sealed record TimetableLocalSettings(
    string ActiveClassId,
    bool ReminderEnabled,
    int LeadMinutes,
    TimetableReminderScope Scope,
    string MyTeacherName,
    string Term,
    IReadOnlyList<TimetablePeriod> Periods,
    IReadOnlyList<string> ClosedDates)
{
    public const int DefaultLeadMinutes = 5;

    public static TimetableLocalSettings Default { get; } = new(
        ActiveClassId: string.Empty,
        ReminderEnabled: true,
        LeadMinutes: DefaultLeadMinutes,
        Scope: TimetableReminderScope.MyClasses,
        MyTeacherName: string.Empty,
        Term: string.Empty,
        Periods: TimetableSchedule.DefaultPeriods,
        ClosedDates: []);

    public TimetableLocalSettings WithPeriods(IReadOnlyList<TimetablePeriod> periods) => this with { Periods = periods };
}

/// <summary>提醒去重记录：同一次课（日期 + 节次 + 班）只响一次，重启也不重复。</summary>
public sealed record TimetableReminderLedger(IReadOnlyList<string> Keys, string UpdatedAt)
{
    public static TimetableReminderLedger Empty { get; } = new([], string.Empty);
}
