using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeskBox.Models;

/// <summary>
/// 课程表数据模型。整份课表是一棵不可变记录树，读出来后只替换整棵，
/// 不做逐格原地修改——跨机同步按「整份取新」处理，见 <c>TimetableStore</c>。
/// </summary>
public sealed record TimetablePeriod(int Index, string Start, string End, string Section)
{
    public string Label => "第" + Index.ToString(CultureInfo.InvariantCulture) + "节";
}

public sealed record TimetableCell(
    int Day,
    int Period,
    string Subject,
    string? Teacher = null,
    string? Room = null,
    string? Note = null,
    string? WeekParity = null);

public sealed record TimetableClass(
    string Id,
    string Name,
    string? Grade,
    bool IsMine,
    IReadOnlyList<TimetableCell> Cells)
{
    public TimetableCell? CellAt(int day, int period)
    {
        for (int i = 0; i < Cells.Count; i++)
        {
            TimetableCell cell = Cells[i];
            if (cell.Day == day && cell.Period == period)
            {
                return cell;
            }
        }

        return null;
    }

    public int CellCount => Cells.Count;
}

public sealed record TimetableIssue(
    string Severity,
    string Kind,
    string Message,
    string? ClassName = null,
    int? Day = null,
    int? Period = null)
{
    public const string SeverityWarning = "warning";
    public const string SeverityError = "error";

    public const string KindMisaligned = "misaligned";
    public const string KindConflict = "conflict";
    public const string KindEmpty = "empty";
    public const string KindDuplicate = "duplicate";
    public const string KindStructure = "structure";
}

public sealed record TimetableDocument(
    string Term,
    string School,
    IReadOnlyList<string> Days,
    IReadOnlyList<TimetablePeriod> Periods,
    IReadOnlyList<TimetableClass> Classes,
    IReadOnlyList<TimetableIssue> Issues,
    long Revision,
    string UpdatedAt)
{
    public const int CurrentSchema = 1;

    public static TimetableDocument Empty { get; } = new(
        Term: string.Empty,
        School: string.Empty,
        Days: ["周一", "周二", "周三", "周四", "周五"],
        Periods: TimetableSchedule.DefaultPeriods,
        Classes: [],
        Issues: [],
        Revision: 0,
        UpdatedAt: string.Empty);

    public bool IsEmpty => Classes.Count == 0;

    public TimetableClass? FindClass(string? classId)
    {
        if (string.IsNullOrEmpty(classId))
        {
            return null;
        }

        for (int i = 0; i < Classes.Count; i++)
        {
            if (string.Equals(Classes[i].Id, classId, StringComparison.Ordinal))
            {
                return Classes[i];
            }
        }

        return null;
    }

    public TimetableClass? FindClassByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        for (int i = 0; i < Classes.Count; i++)
        {
            if (string.Equals(Classes[i].Name, trimmed, StringComparison.Ordinal))
            {
                return Classes[i];
            }
        }

        return null;
    }

    /// <summary>没有显式指定班级时优先「我的班」，否则第一个班。</summary>
    public TimetableClass? DefaultClass
    {
        get
        {
            for (int i = 0; i < Classes.Count; i++)
            {
                if (Classes[i].IsMine)
                {
                    return Classes[i];
                }
            }

            return Classes.Count > 0 ? Classes[0] : null;
        }
    }

    public IReadOnlyList<TimetableClass> MyClasses
    {
        get
        {
            List<TimetableClass> mine = new();
            for (int i = 0; i < Classes.Count; i++)
            {
                if (Classes[i].IsMine)
                {
                    mine.Add(Classes[i]);
                }
            }

            return mine.Count > 0 ? mine : Classes;
        }
    }

    public TimetableDocument WithClasses(IReadOnlyList<TimetableClass> classes, IReadOnlyList<TimetableIssue> issues) =>
        this with { Classes = classes, Issues = issues };

    /// <summary>把某个班标记（或取消）为「我的班」，其余班级保持不变。</summary>
    public TimetableDocument WithMine(string classId, string? teacherName, bool mark)
    {
        List<TimetableClass> next = new(Classes.Count);
        foreach (TimetableClass item in Classes)
        {
            bool isMine = string.Equals(item.Id, classId, StringComparison.Ordinal) ? mark : item.IsMine;
            next.Add(item with { IsMine = isMine });
        }

        return this with { Classes = next };
    }

    public IReadOnlyList<TimetableCell> CellsTaughtBy(string teacherName)
    {
        List<TimetableCell> list = new();
        if (string.IsNullOrWhiteSpace(teacherName))
        {
            return list;
        }

        foreach (TimetableClass item in Classes)
        {
            foreach (TimetableCell cell in item.Cells)
            {
                if (string.Equals(cell.Teacher, teacherName, StringComparison.Ordinal))
                {
                    list.Add(cell);
                }
            }
        }

        return list;
    }
}
