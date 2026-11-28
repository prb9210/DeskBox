using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services.Timetable;

namespace DeskBox.Tests;

public sealed class TimetableParserTests
{
    /// <summary>
    /// 校总表形态：三天 × 三节，每个班两行（课程行 + 教师行）。
    /// 刻意埋了两个真实场景：①某格科目栏写成「地理 黎科」而教师栏写另一门课；
    /// ②同一老师同一节出现在两个班（真冲突）。
    /// </summary>
    private static List<IReadOnlyList<string>> BuildWideGrid()
    {
        List<IReadOnlyList<string>> grid = new()
        {
            new List<string> { "", "星期一", "", "", "星期二", "", "", "星期三", "", "" },
            new List<string> { "", "1", "2", "3", "1", "2", "3", "1", "2", "3" },
            new List<string> { "初三1", "语文", "数学", "英语", "物理", "化学", "历史", "地理 黎科", "体育", "数学" },
            new List<string> { "", "郑甲棉", "潘荣波", "文柳", "卢荣飞", "张军连", "莫培英", "电脑 白少锋", "苏强", "潘荣波" },
            new List<string> { "初三2", "数学", "数学", "英语", "化学", "物理", "历史", "生物", "地理", "体育" },
            new List<string> { "", "张三", "潘荣波", "王五", "张军连", "卢荣飞", "赵六", "李舒诗", "黎科", "苏强" }
        };
        return grid;
    }

    [Fact]
    public void Parse_ReadsTwoRowPerClassLayout()
    {
        TimetableParseResult result = TimetableGridParser.Parse(BuildWideGrid());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Report.ClassCount);
        Assert.Equal(3, result.Report.DayCount);
        Assert.Equal(3, result.Report.PeriodsPerDay);
        Assert.Equal(18, result.Report.FilledCells);

        TimetableClass first = result.Document.Classes.Single(item => item.Name == "初三1");
        TimetableCell mondayFirst = Assert.IsType<TimetableCell>(first.CellAt(0, 1));
        Assert.Equal("语文", mondayFirst.Subject);
        Assert.Equal("郑甲棉", mondayFirst.Teacher);
    }

    [Fact]
    public void Parse_SplitsSubjectAndTeacherInOneCell_AndFlagsIt()
    {
        TimetableParseResult result = TimetableGridParser.Parse(BuildWideGrid());

        TimetableClass first = result.Document.Classes.Single(item => item.Name == "初三1");
        TimetableCell wednesdayFirst = Assert.IsType<TimetableCell>(first.CellAt(2, 1));
        Assert.Equal("地理", wednesdayFirst.Subject);
        Assert.Equal("黎科", wednesdayFirst.Teacher);
        Assert.Contains(result.Document.Issues, issue => issue.Kind == TimetableIssue.KindMisaligned);
        Assert.Equal(1, result.Report.MisalignedCount);
    }

    [Fact]
    public void Parse_FlagsSameTeacherInTwoClassesAtSameSlot()
    {
        TimetableParseResult result = TimetableGridParser.Parse(BuildWideGrid());

        Assert.Equal(1, result.Report.ConflictCount);
        TimetableIssue conflict = result.Document.Issues.Single(issue => issue.Kind == TimetableIssue.KindConflict);
        Assert.Contains("潘荣波", conflict.Message);
    }

    [Fact]
    public void Parse_ReadsTallLayout_WherePeriodsAreRows()
    {
        List<IReadOnlyList<string>> grid = new()
        {
            new List<string> { "", "星期一", "星期二", "星期三" },
            new List<string> { "1", "语文", "数学", "英语" },
            new List<string> { "2", "数学", "语文", "数学" },
            new List<string> { "3", "英语", "英语", "语文" }
        };

        TimetableParseResult result = TimetableGridParser.Parse(
            grid,
            new TimetableParseOptions(SingleClassName: "初三2"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.Report.ClassCount);
        Assert.Equal(3, result.Report.PeriodsPerDay);
        Assert.Equal(9, result.Report.FilledCells);
        Assert.Equal("初三2", result.Document.Classes[0].Name);
    }

    [Fact]
    public void Parse_HandlesTransposedLayout()
    {
        List<IReadOnlyList<string>> grid = new()
        {
            new List<string> { "星期", "1", "2", "3" },
            new List<string> { "星期一", "语文", "数学", "英语" },
            new List<string> { "星期二", "数学", "语文", "英语" },
            new List<string> { "星期三", "英语", "数学", "语文" }
        };

        TimetableParseResult result = TimetableGridParser.Parse(grid);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Report.DayCount);
        Assert.Equal(9, result.Report.FilledCells);
        Assert.Equal("transposed", result.Report.Structure);
    }

    [Fact]
    public void Parse_RejectsInputWithoutDayHeader()
    {
        List<IReadOnlyList<string>> grid = new()
        {
            new List<string> { "语文", "数学" },
            new List<string> { "英语", "物理" }
        };

        TimetableParseResult result = TimetableGridParser.Parse(grid);

        Assert.False(result.Ok);
        Assert.Contains("星期", result.Error);
    }

    [Fact]
    public void Tsv_ParsesQuotedCellsAndTrimsTrailingBlankRows()
    {
        const string text = "星期一\t星期二\n语文\t\"数\t学\"\n\n";
        IReadOnlyList<IReadOnlyList<string>> rows = TimetableTsv.Parse(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal("星期一", rows[0][0]);
        Assert.Equal("数\t学", rows[1][1]);
    }

    [Fact]
    public void Text_KeepsPoliticsAndRuleOfLawApart()
    {
        Assert.Equal("政治", TimetableText.CanonicalSubject("政治"));
        Assert.Equal("法治", TimetableText.CanonicalSubject("法治"));
        Assert.Equal("信息技术", TimetableText.CanonicalSubject("电脑"));
    }

    [Fact]
    public void Planner_FiresOnceInsideLeadWindow()
    {
        TimetableDocument document = BuildDocument();
        TimetableLocalSettings settings = TimetableLocalSettings.Default with
        {
            LeadMinutes = 5,
            ReminderEnabled = true,
            MyTeacherName = "潘荣波",
            Scope = TimetableReminderScope.MyClasses
        };
        HashSet<string> seen = new(StringComparer.Ordinal);

        // 周一 07:36 —— 第 1 节 07:40 开始，落在 5 分钟窗口内。
        DateTimeOffset before = new(2026, 11, 30, 7, 36, 0, TimeSpan.FromHours(8));
        TimetableReminderCandidate? first = TimetableReminderPlanner.Evaluate(document, settings, before, seen);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Period.Index);
        Assert.Contains("4 分钟", first.Title);

        seen.Add(first.Key);
        Assert.Null(TimetableReminderPlanner.Evaluate(document, settings, before, seen));

        // 07:30 —— 还有 10 分钟，窗口外。
        DateTimeOffset tooEarly = new(2026, 11, 30, 7, 30, 0, TimeSpan.FromHours(8));
        Assert.Null(TimetableReminderPlanner.Evaluate(document, settings, tooEarly, new HashSet<string>()));
    }

    [Fact]
    public void Planner_RespectsWeekendClosedDatesAndOffSwitch()
    {
        TimetableDocument document = BuildDocument();
        TimetableLocalSettings settings = TimetableLocalSettings.Default with
        {
            LeadMinutes = 5,
            ReminderEnabled = true
        };

        DateTimeOffset saturday = new(2026, 12, 5, 7, 36, 0, TimeSpan.FromHours(8));
        Assert.Null(TimetableReminderPlanner.Evaluate(document, settings, saturday, new HashSet<string>()));

        settings = settings with { ClosedDates = ["2026-11-30"] };
        DateTimeOffset monday = new(2026, 11, 30, 7, 36, 0, TimeSpan.FromHours(8));
        Assert.Null(TimetableReminderPlanner.Evaluate(document, settings, monday, new HashSet<string>()));

        settings = settings with { ClosedDates = [], ReminderEnabled = false };
        Assert.Null(TimetableReminderPlanner.Evaluate(document, settings, monday, new HashSet<string>()));
    }

    [Fact]
    public void Schedule_ReportsRunningAndNextPeriod()
    {
        DateTimeOffset at0835 = new(2026, 11, 30, 8, 35, 0, TimeSpan.FromHours(8));
        Assert.Equal(2, TimetableSchedule.RunningPeriod(TimetableSchedule.DefaultPeriods, at0835)?.Index);
        Assert.Equal(3, TimetableSchedule.NextPeriod(TimetableSchedule.DefaultPeriods, at0835)?.Index);
        Assert.Equal(460, TimetableSchedule.ParseMinutes("07:40"));
        Assert.Equal(0, TimetableSchedule.DayIndex(at0835));
        Assert.Equal(-1, TimetableSchedule.DayIndex(new DateTimeOffset(2026, 12, 5, 9, 0, 0, TimeSpan.FromHours(8))));
    }

    [Fact]
    public void Store_RoundTripsDocumentAndSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "deskbox-timetable-" + Guid.NewGuid().ToString("N"));
        try
        {
            TimetableStore store = new(root);
            TimetableDocument document = BuildDocument();
            TimetableDocument saved = store.SaveAsync(document).GetAwaiter().GetResult();
            Assert.True(saved.Revision >= 1);

            TimetableDocument loaded = store.Load();
            Assert.Equal(1, loaded.Classes.Count);
            Assert.Equal("数学", loaded.Classes[0].CellAt(0, 1)!.Subject);

            TimetableLocalSettings settings = TimetableLocalSettings.Default with
            {
                ActiveClassId = "初三2",
                LeadMinutes = 10,
                ReminderEnabled = false,
                MyTeacherName = "潘荣波",
                ClosedDates = ["2026-12-01"]
            };
            store.SaveSettingsAsync(settings).GetAwaiter().GetResult();

            TimetableLocalSettings reloaded = store.LoadSettings();
            Assert.Equal("初三2", reloaded.ActiveClassId);
            Assert.Equal(10, reloaded.LeadMinutes);
            Assert.False(reloaded.ReminderEnabled);
            Assert.Equal("潘荣波", reloaded.MyTeacherName);
            Assert.Single(reloaded.ClosedDates);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TimetableDocument BuildDocument()
    {
        TimetableClass mine = new(
            Id: "初三2",
            Name: "初三2",
            Grade: "九年级",
            IsMine: true,
            Cells:
            [
                new TimetableCell(0, 1, "数学", "潘荣波"),
                new TimetableCell(0, 2, "语文", "郑甲棉")
            ]);

        return new TimetableDocument(
            Term: "2026-2027第一学期",
            School: "高山市平山中学",
            Days: ["周一", "周二", "周三", "周四", "周五"],
            Periods: TimetableSchedule.DefaultPeriods,
            Classes: [mine],
            Issues: [],
            Revision: 0,
            UpdatedAt: string.Empty);
    }
}
