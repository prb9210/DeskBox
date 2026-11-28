using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DeskBox.Models;

namespace DeskBox.Services.Timetable;

public sealed record TimetableParseOptions(
    string? MyTeacherName = null,
    string? Term = null,
    string? School = null,
    string? SingleClassName = null);

/// <summary>导入体检的量化结果。UI 只展示这些数字，不在界面里重新算。</summary>
public sealed record TimetableParseReport(
    string Structure,
    int ClassCount,
    int DayCount,
    int PeriodsPerDay,
    int FilledCells,
    int EmptyCells,
    int ConflictCount,
    int MisalignedCount,
    IReadOnlyList<string> ClassNames,
    IReadOnlyList<string> Warnings)
{
    public bool HasFatalIssue => ClassCount == 0;
}

public sealed record TimetableParseResult(
    bool Ok,
    string? Error,
    TimetableDocument Document,
    TimetableParseReport Report)
{
    public IReadOnlyList<TimetableIssue> Issues => Document.Issues;

    public static TimetableParseResult Fail(string error) => new(
        Ok: false,
        Error: error,
        Document: TimetableDocument.Empty,
        Report: new TimetableParseReport(
            Structure: "unknown",
            ClassCount: 0,
            DayCount: 0,
            PeriodsPerDay: 0,
            FilledCells: 0,
            EmptyCells: 0,
            ConflictCount: 0,
            MisalignedCount: 0,
            ClassNames: [],
            Warnings: [error]));
}

/// <summary>
/// 总课程表结构探测 + 解析。刻意不写死列号：先找星期表头，再找节次行，
/// 最后按「班级标签」切出每个班，所以换学期换了排版也能吃进去。
///
/// 支持三种排布：
/// 1. 两行一班（校总表：课程行 + 教师行）；
/// 2. 单班表（只有一张 8×5 网格）；
/// 3. 竖排表（星期在行、节次在列）—— 转置后走同一管线。
/// </summary>
public static class TimetableGridParser
{
    private const int MaxHeaderSearchRows = 12;
    private const int MaxPeriodSearchRows = 4;

    public static TimetableParseResult Parse(
        IReadOnlyList<IReadOnlyList<string>> rawGrid,
        TimetableParseOptions? options = null)
    {
        options ??= new TimetableParseOptions();
        if (rawGrid is null || rawGrid.Count == 0)
        {
            return TimetableParseResult.Fail("没有读到任何内容：请确认粘贴的是表格区域（含星期表头）。");
        }

        List<List<string>> grid = NormalizeGrid(rawGrid);
        TimetableParseResult result = ParseCore(grid, options, allowTranspose: true, structure: "calendar-table");
        return result;
    }

    private static TimetableParseResult ParseCore(
        List<List<string>> grid,
        TimetableParseOptions options,
        bool allowTranspose,
        string structure)
    {
        int headerRow = FindDayHeaderRow(grid);
        if (headerRow < 0)
        {
            if (allowTranspose)
            {
                List<List<string>> transposed = Transpose(grid);
                TimetableParseResult second = ParseCore(transposed, options, allowTranspose: false, structure: "transposed");
                if (second.Ok)
                {
                    return second;
                }
            }

            return TimetableParseResult.Fail(
                "没找到星期表头（星期一…星期五）。请连表头一起复制，或只选含星期的数据区。");
        }

        List<int> dayColumns = FindDayColumns(grid[headerRow]);
        if (dayColumns.Count < 3)
        {
            if (allowTranspose)
            {
                List<List<string>> transposed = Transpose(grid);
                TimetableParseResult second = ParseCore(transposed, options, allowTranspose: false, structure: "transposed");
                if (second.Ok)
                {
                    return second;
                }
            }

            return TimetableParseResult.Fail("星期的列太少（少于 3 天），无法判断这是课程表。");
        }

        int width = grid.Max(row => row.Count);
        // 两种排布：宽表（每天占多列，节次在列，校总表就是这个形态）与
        // 高表（每天只占一列，节次在行）。判据是星期表头是否逐列相邻。
        bool tallLayout = IsConsecutive(dayColumns);
        int periodsPerDay = tallLayout
            ? CountTallPeriods(grid, headerRow + 1, dayColumns)
            : (dayColumns.Count > 1 ? dayColumns[1] - dayColumns[0] : Math.Max(1, width - dayColumns[0]));
        if (periodsPerDay <= 0)
        {
            return TimetableParseResult.Fail("星期表头下方的列数不齐，无法确定每天几节。");
        }

        int periodRow = tallLayout ? -1 : FindPeriodRow(grid, headerRow, dayColumns, periodsPerDay);
        // 节次行可以没有（有些表直接画 8 行），此时按行数推算。
        int firstDataRow = periodRow > headerRow ? periodRow + 1 : headerRow + 1;

        List<(string Name, int Row)> classRows = FindClassRows(grid, firstDataRow, dayColumns[0]);
        List<string> warnings = new();
        List<TimetableIssue> issues = new();
        List<TimetableClass> classes = new();

        if (classRows.Count == 0)
        {
            // 单班表：整张网格就是一个班。
            string name = TimetableText.Normalize(options.SingleClassName);
            if (name.Length == 0)
            {
                name = "本班";
            }

            TimetableClass single = tallLayout
                ? BuildClassTall(grid, name, firstDataRow, dayColumns, periodsPerDay, options, issues)
                : BuildClass(
                    grid,
                    name,
                    firstDataRow,
                    teacherRow: -1,
                    dayColumns,
                    periodsPerDay,
                    options,
                    issues,
                    out _);
            classes.Add(single);
            warnings.Add("没找到班级标签列，按「单班表」解析，班级名可用设置改成实际班级。");
        }
        else
        {
            foreach ((string name, int row) in classRows)
            {
                if (tallLayout)
                {
                    // 高表里「一行一个节次」，班级标签只出现在表头，不会按行重复，
                    // 所以走到这里基本不会命中；保留分支以免漏判。
                    TimetableClass tallClass = BuildClassTall(grid, name, row, dayColumns, periodsPerDay, options, issues);
                    if (tallClass.CellCount > 0)
                    {
                        classes.Add(tallClass);
                    }

                    continue;
                }

                int teacherRow = DetectTeacherRow(grid, row, dayColumns, periodsPerDay);
                TimetableClass item = BuildClass(
                    grid,
                    name,
                    row,
                    teacherRow,
                    dayColumns,
                    periodsPerDay,
                    options,
                    issues,
                    out int filled);
                if (filled == 0)
                {
                    warnings.Add(name + " 一行里没有读到任何课程，已跳过。");
                    continue;
                }

                classes.Add(item);
            }
        }

        if (classes.Count == 0)
        {
            return TimetableParseResult.Fail("识别到了星期表头，但没有任何一个班读到课程。请检查是否只复制了教师行。");
        }

        // 同一老师同一节出现在两个班：真冲突，必须人工看。
        int conflicts = AddConflictIssues(classes, issues);

        // 全班同一个空节（如周五第 8 节）是正常安排，单独汇总成一条提示。
        int emptyCells = classes.Count * dayColumns.Count * periodsPerDay;
        foreach (TimetableClass item in classes)
        {
            emptyCells -= item.CellCount;
        }

        AddCommonEmptyPeriodNotes(classes, dayColumns.Count, periodsPerDay, warnings);

        TimetableDocument document = new(
            Term: TimetableText.Normalize(options.Term),
            School: TimetableText.Normalize(options.School),
            Days: BuildDayNames(dayColumns.Count),
            Periods: BuildPeriods(periodsPerDay),
            Classes: classes,
            Issues: issues,
            Revision: 1,
            UpdatedAt: DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        int filledTotal = classes.Sum(item => item.CellCount);
        TimetableParseReport report = new(
            Structure: structure,
            ClassCount: classes.Count,
            DayCount: dayColumns.Count,
            PeriodsPerDay: periodsPerDay,
            FilledCells: filledTotal,
            EmptyCells: emptyCells,
            ConflictCount: conflicts,
            MisalignedCount: issues.Count(issue => issue.Kind == TimetableIssue.KindMisaligned),
            ClassNames: classes.Select(item => item.Name).ToList(),
            Warnings: warnings);

        return new TimetableParseResult(true, null, document, report);
    }

    private static bool IsConsecutive(List<int> columns)
    {
        for (int i = 1; i < columns.Count; i++)
        {
            if (columns[i] - columns[i - 1] != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>高表（节次在行）每天几节：从表头往下数「课程列里有内容」的行。</summary>
    private static int CountTallPeriods(List<List<string>> grid, int fromRow, List<int> dayColumns)
    {
        int count = 0;
        for (int r = fromRow; r < grid.Count; r++)
        {
            bool any = false;
            foreach (int column in dayColumns)
            {
                if (column < grid[r].Count && grid[r][column].Length > 0)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>高表：每行一个节次，每列一天。节次号取行首标签，取不到就按行序。</summary>
    private static TimetableClass BuildClassTall(
        List<List<string>> grid,
        string className,
        int firstDataRow,
        List<int> dayColumns,
        int periodsPerDay,
        TimetableParseOptions options,
        List<TimetableIssue> issues)
    {
        List<TimetableCell> cells = new();
        bool isMine = false;
        int period = 0;

        for (int r = firstDataRow; r < grid.Count && period < periodsPerDay; r++)
        {
            bool any = false;
            foreach (int column in dayColumns)
            {
                if (column < grid[r].Count && grid[r][column].Length > 0)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                break;
            }

            period++;
            int labelPeriod = period;
            for (int c = 0; c < dayColumns[0] && c < grid[r].Count; c++)
            {
                if (int.TryParse(grid[r][c], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                    parsed > 0)
                {
                    labelPeriod = parsed;
                }
            }

            for (int day = 0; day < dayColumns.Count; day++)
            {
                int column = dayColumns[day];
                string raw = column < grid[r].Count ? grid[r][column] : string.Empty;
                if (raw.Length == 0)
                {
                    continue;
                }

                string subject;
                string? teacher = null;
                if (TimetableText.TrySplitSubjectAndTeacher(raw, out string splitSubject, out string splitTeacher))
                {
                    subject = splitSubject;
                    teacher = splitTeacher;
                }
                else
                {
                    subject = TimetableText.CanonicalSubject(raw);
                }

                if (subject.Length == 0)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(options.MyTeacherName) &&
                    teacher is not null &&
                    string.Equals(teacher, options.MyTeacherName, StringComparison.Ordinal))
                {
                    isMine = true;
                }

                cells.Add(new TimetableCell(day, labelPeriod, subject, teacher));
            }
        }

        return new TimetableClass(
            Id: TimetableText.ClassId(className),
            Name: className,
            Grade: TimetableText.GradeOf(className),
            IsMine: isMine,
            Cells: cells);
    }

    private static List<List<string>> NormalizeGrid(IReadOnlyList<IReadOnlyList<string>> rawGrid)
    {
        List<List<string>> grid = new(rawGrid.Count);
        foreach (IReadOnlyList<string> row in rawGrid)
        {
            List<string> cells = new(row.Count);
            foreach (string cell in row)
            {
                cells.Add(TimetableText.Normalize(cell));
            }

            grid.Add(cells);
        }

        return grid;
    }

    private static List<List<string>> Transpose(List<List<string>> grid)
    {
        int width = grid.Max(row => row.Count);
        List<List<string>> result = new(width);
        for (int c = 0; c < width; c++)
        {
            List<string> row = new(grid.Count);
            for (int r = 0; r < grid.Count; r++)
            {
                row.Add(c < grid[r].Count ? grid[r][c] : string.Empty);
            }

            result.Add(row);
        }

        return result;
    }

    private static readonly string[] s_dayNames =
    [
        "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日", "星期天"
    ];

    private static readonly string[] s_dayShortNames =
    [
        "周一", "周二", "周三", "周四", "周五", "周六", "周日"
    ];

    /// <summary>返回 0..6 的星期索引；不是星期表头返回 -1。</summary>
    private static int DayIndexOf(string text)
    {
        string value = TimetableText.Normalize(text);
        if (value.Length == 0)
        {
            return -1;
        }

        for (int i = 0; i < s_dayNames.Length; i++)
        {
            if (value.Contains(s_dayNames[i], StringComparison.Ordinal))
            {
                return i == 7 ? 6 : i;
            }
        }

        for (int i = 0; i < s_dayShortNames.Length; i++)
        {
            if (value.Contains(s_dayShortNames[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindDayHeaderRow(List<List<string>> grid)
    {
        int limit = Math.Min(grid.Count, MaxHeaderSearchRows);
        for (int r = 0; r < limit; r++)
        {
            int hits = grid[r].Count(cell => DayIndexOf(cell) >= 0);
            if (hits >= 3)
            {
                return r;
            }
        }

        return -1;
    }

    private static List<int> FindDayColumns(List<string> headerRow)
    {
        List<(int Day, int Column)> found = new();
        for (int c = 0; c < headerRow.Count; c++)
        {
            int day = DayIndexOf(headerRow[c]);
            if (day >= 0)
            {
                found.Add((day, c));
            }
        }

        // 同一星期只保留第一次出现，且必须按天递增（合并表头只在首列有值）。
        List<int> columns = new();
        int lastDay = -1;
        foreach ((int day, int column) in found)
        {
            if (day <= lastDay)
            {
                continue;
            }

            lastDay = day;
            columns.Add(column);
        }

        return columns;
    }

    private static int FindPeriodRow(
        List<List<string>> grid,
        int headerRow,
        List<int> dayColumns,
        int periodsPerDay)
    {
        int limit = Math.Min(grid.Count, headerRow + 1 + MaxPeriodSearchRows);
        for (int r = headerRow + 1; r < limit; r++)
        {
            int matches = 0;
            for (int p = 0; p < periodsPerDay; p++)
            {
                int column = dayColumns[0] + p;
                if (column >= grid[r].Count)
                {
                    continue;
                }

                if (int.TryParse(grid[r][column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                    value == p + 1)
                {
                    matches++;
                }
            }

            if (matches >= Math.Min(3, periodsPerDay))
            {
                return r;
            }
        }

        return -1;
    }

    private static List<(string Name, int Row)> FindClassRows(
        List<List<string>> grid,
        int firstDataRow,
        int firstDayColumn)
    {
        List<(string, int)> rows = new();
        for (int r = firstDataRow; r < grid.Count; r++)
        {
            string label = string.Empty;
            for (int c = 0; c < firstDayColumn && c < grid[r].Count; c++)
            {
                if (grid[r][c].Length > 0)
                {
                    label = grid[r][c];
                    break;
                }
            }

            if (label.Length == 0 || !TimetableText.IsClassLabel(label))
            {
                continue;
            }

            // 表头角落那一格常写成「时间 / 班级」，会被 IsClassLabel 当班级名；
            // 纵向合并展开时教师行还会带着同一个班级名。两种都跳过。
            if (label.Contains("时间", StringComparison.Ordinal) ||
                string.Equals(label, "班级", StringComparison.Ordinal) ||
                (rows.Count > 0 && string.Equals(rows[^1].Item1, label, StringComparison.Ordinal) && rows[^1].Item2 == r - 1))
            {
                continue;
            }

            rows.Add((label, r));
        }

        return rows;
    }

    /// <summary>
    /// 课程行的下一行是不是教师行：本行班级格为空，且课程列里至少有一格像人名。
    /// </summary>
    private static int DetectTeacherRow(
        List<List<string>> grid,
        int classRow,
        List<int> dayColumns,
        int periodsPerDay)
    {
        int row = classRow + 1;
        if (row >= grid.Count)
        {
            return -1;
        }

        for (int p = 0; p < periodsPerDay; p++)
        {
            int column = dayColumns[0] + p;
            if (column < grid[row].Count && grid[row][column].Length > 0)
            {
                return IsRowMostlyNames(grid[row], dayColumns, periodsPerDay) ? row : -1;
            }
        }

        return -1;
    }

    private static bool IsRowMostlyNames(List<string> row, List<int> dayColumns, int periodsPerDay)
    {
        int names = 0;
        int subjects = 0;
        for (int p = 0; p < periodsPerDay; p++)
        {
            int column = dayColumns[0] + p;
            if (column >= row.Count)
            {
                continue;
            }

            string cell = row[column];
            if (cell.Length == 0)
            {
                continue;
            }

            if (TimetableText.LooksLikeSubject(cell))
            {
                subjects++;
            }
            else if (cell.Contains(' '))
            {
                // 「电脑 白少锋」这种合成格，含科目也算教师行。
                if (TimetableText.TrySplitSubjectAndTeacher(cell, out _, out _))
                {
                    subjects++;
                }
            }
            else if (TimetableText.LooksLikePersonName(cell))
            {
                names++;
            }
        }

        return names > 0 && names >= subjects;
    }

    private static TimetableClass BuildClass(
        List<List<string>> grid,
        string className,
        int classRow,
        int teacherRow,
        List<int> dayColumns,
        int periodsPerDay,
        TimetableParseOptions options,
        List<TimetableIssue> issues,
        out int filledCells)
    {
        List<TimetableCell> cells = new();
        filledCells = 0;
        bool isMine = false;

        for (int day = 0; day < dayColumns.Count; day++)
        {
            for (int period = 1; period <= periodsPerDay; period++)
            {
                int column = dayColumns[day] + period - 1;
                string subjectRaw = column < grid[classRow].Count ? grid[classRow][column] : string.Empty;
                string teacherRaw = teacherRow >= 0 && column < grid[teacherRow].Count
                    ? grid[teacherRow][column]
                    : string.Empty;

                if (subjectRaw.Length == 0 && teacherRaw.Length == 0)
                {
                    continue;
                }

                string subject;
                string? teacher = null;
                string? note = null;

                if (TimetableText.TrySplitSubjectAndTeacher(subjectRaw, out string splitSubject, out string splitTeacher))
                {
                    subject = splitSubject;
                    teacher = splitTeacher;

                    if (teacherRaw.Length > 0)
                    {
                        if (TimetableText.TrySplitSubjectAndTeacher(teacherRaw, out string rowSubject, out string rowTeacher))
                        {
                            // 两行都写成了「科目 + 老师」：科目栏与教师栏互相串位，
                            // 不猜谁对，记一条可疑项并把两种可能都留在 note 里。
                            note = "教师栏写的是「" + teacherRaw + "」";
                            issues.Add(new TimetableIssue(
                                TimetableIssue.SeverityWarning,
                                TimetableIssue.KindMisaligned,
                                className + " 第" + (day + 1) + "天第" + period + "节：科目栏「" + subjectRaw +
                                "」与教师栏「" + teacherRaw + "」都含科目名，疑似串位（已按科目栏取 " + subject +
                                " / " + teacher + "，另一可能是 " + rowSubject + " / " + rowTeacher + "）",
                                className,
                                day,
                                period));
                        }
                        else if (TimetableText.LooksLikePersonName(teacherRaw) &&
                                 !string.Equals(teacherRaw, teacher, StringComparison.Ordinal))
                        {
                            note = "科目栏另写了老师「" + teacher + "」";
                            issues.Add(new TimetableIssue(
                                TimetableIssue.SeverityWarning,
                                TimetableIssue.KindMisaligned,
                                className + " 第" + (day + 1) + "天第" + period + "节：科目栏带老师「" + teacher +
                                "」，教师栏是「" + teacherRaw + "」，已按教师栏为准",
                                className,
                                day,
                                period));
                            teacher = teacherRaw;
                        }
                    }
                }
                else if (subjectRaw.Length == 0)
                {
                    subject = TimetableText.CanonicalSubject(teacherRaw);
                    teacher = TimetableText.LooksLikeSubject(teacherRaw) ? null : teacherRaw;
                    if (teacher is null)
                    {
                        note = "教师栏写的是科目，已并入科目";
                    }

                    issues.Add(new TimetableIssue(
                        TimetableIssue.SeverityWarning,
                        TimetableIssue.KindMisaligned,
                        className + " 第" + (day + 1) + "天第" + period + "节：科目栏空，教师栏是「" + teacherRaw + "」",
                        className,
                        day,
                        period));
                }
                else
                {
                    subject = TimetableText.CanonicalSubject(subjectRaw);
                    string normalizedTeacher = teacherRaw.Length > 0
                        ? (TimetableText.TrySplitSubjectAndTeacher(teacherRaw, out _, out string onlyTeacher)
                            ? onlyTeacher
                            : teacherRaw)
                        : string.Empty;
                    teacher = normalizedTeacher.Length > 0 ? normalizedTeacher : null;

                    if (teacherRaw.Length > 0 && TimetableText.LooksLikeSubject(teacherRaw))
                    {
                        issues.Add(new TimetableIssue(
                            TimetableIssue.SeverityWarning,
                            TimetableIssue.KindMisaligned,
                            className + " 第" + (day + 1) + "天第" + period + "节：教师栏写的是科目「" + teacherRaw + "」",
                            className,
                            day,
                            period));
                    }
                }

                if (teacher is null && teacherRaw.Length > 0 && TimetableText.LooksLikePersonName(teacherRaw))
                {
                    teacher = teacherRaw;
                }

                if (subject.Length == 0)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(options.MyTeacherName) &&
                    teacher is not null &&
                    string.Equals(TimetableText.Normalize(teacher), TimetableText.Normalize(options.MyTeacherName), StringComparison.Ordinal))
                {
                    isMine = true;
                }

                cells.Add(new TimetableCell(day, period, subject, teacher, Room: null, Note: note));
                filledCells++;
            }
        }

        return new TimetableClass(
            Id: TimetableText.ClassId(className),
            Name: className,
            Grade: TimetableText.GradeOf(className),
            IsMine: isMine,
            Cells: cells);
    }

    private static int AddConflictIssues(List<TimetableClass> classes, List<TimetableIssue> issues)
    {
        Dictionary<string, List<string>> seen = new(StringComparer.Ordinal);
        int conflicts = 0;
        foreach (TimetableClass item in classes)
        {
            foreach (TimetableCell cell in item.Cells)
            {
                if (cell.Teacher is null)
                {
                    continue;
                }

                string key = cell.Teacher + "|" + cell.Day.ToString(CultureInfo.InvariantCulture) + "|" +
                             cell.Period.ToString(CultureInfo.InvariantCulture);
                if (!seen.TryGetValue(key, out List<string>? list))
                {
                    list = new List<string>();
                    seen[key] = list;
                }

                list.Add(item.Name + " " + cell.Subject);
            }
        }

        foreach (KeyValuePair<string, List<string>> pair in seen)
        {
            if (pair.Value.Count <= 1)
            {
                continue;
            }

            string[] parts = pair.Key.Split('|');
            issues.Add(new TimetableIssue(
                TimetableIssue.SeverityWarning,
                TimetableIssue.KindConflict,
                parts[0] + " 在第" + (int.Parse(parts[1], CultureInfo.InvariantCulture) + 1) + "天第" + parts[2] +
                "节同时出现在：" + string.Join("、", pair.Value),
                ClassName: null,
                Day: int.Parse(parts[1], CultureInfo.InvariantCulture),
                Period: int.Parse(parts[2], CultureInfo.InvariantCulture)));
            conflicts++;
        }

        return conflicts;
    }

    private static void AddCommonEmptyPeriodNotes(
        List<TimetableClass> classes,
        int dayCount,
        int periodsPerDay,
        List<string> warnings)
    {
        for (int day = 0; day < dayCount; day++)
        {
            for (int period = 1; period <= periodsPerDay; period++)
            {
                int empty = 0;
                foreach (TimetableClass item in classes)
                {
                    if (item.CellAt(day, period) is null)
                    {
                        empty++;
                    }
                }

                if (empty == classes.Count && classes.Count > 1)
                {
                    warnings.Add("全部 " + classes.Count + " 个班在第" + (day + 1) + "天第" + period +
                                 "节都没有排课（按不排课处理，界面显示为空槽）。");
                }
            }
        }
    }

    private static List<string> BuildDayNames(int count)
    {
        List<string> days = new(count);
        for (int i = 0; i < count; i++)
        {
            days.Add(i < s_dayShortNames.Length ? s_dayShortNames[i] : "第" + (i + 1) + "天");
        }

        return days;
    }

    private static List<TimetablePeriod> BuildPeriods(int count)
    {
        List<TimetablePeriod> periods = new(count);
        IReadOnlyList<TimetablePeriod> defaults = TimetableSchedule.DefaultPeriods;
        for (int i = 1; i <= count; i++)
        {
            TimetablePeriod? match = defaults.FirstOrDefault(period => period.Index == i);
            periods.Add(match ?? new TimetablePeriod(i, string.Empty, string.Empty, i <= 4 ? "上午" : "下午"));
        }

        return periods;
    }

    /// <summary>把解析结果压成可粘贴的一行摘要，写日志与体检卡都能用。</summary>
    public static string Describe(TimetableParseReport report)
    {
        StringBuilder builder = new();
        builder.Append("识别 ").Append(report.ClassCount).Append(" 个班 / 每天 ")
            .Append(report.PeriodsPerDay).Append(" 节 / 共 ")
            .Append(report.FilledCells).Append(" 节有课");
        if (report.ConflictCount > 0)
        {
            builder.Append("；教师同节冲突 ").Append(report.ConflictCount).Append(" 处");
        }

        if (report.MisalignedCount > 0)
        {
            builder.Append("；可疑串位 ").Append(report.MisalignedCount).Append(" 处");
        }

        return builder.ToString();
    }
}
