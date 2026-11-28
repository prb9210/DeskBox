using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.LayoutSync;
using DeskBox.Services.Timetable;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// 课程表格子的视图模型：加载/导入/切班/提醒全在这里，视图只负责画。
///
/// 提醒刻意放在格子里而不是全局服务：用户定的规则是「关掉格子就停提醒」，
/// 放在这儿天然满足，也不必去改 App 启动流程。toast 仍复用 App 的
/// <see cref="NativeAppNotificationService"/>，与待办提醒同一个通道、不同 Group。
/// </summary>
public sealed class TimetableWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TimetableStore _store;
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer? _timer;

    private TimetableDocument _document = TimetableDocument.Empty;
    private TimetableLocalSettings _settings = TimetableLocalSettings.Default;
    private TimetableClass? _currentClass;
    private bool _isTodayView;
    private bool _initialized;
    private bool _disposed;
    private int _lastRunningPeriod = -1;
    private bool _ledgerLoaded;

    public TimetableWidgetViewModel()
    {
        _store = new TimetableStore();
        if (DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(30);
            _timer.Tick += (_, _) => _ = TickAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>视图监听它重画网格（切班、导入、跨节次都要重画）。</summary>
    public event EventHandler? ContentChanged;

    public TimetableDocument Document => _document;

    public TimetableLocalSettings Settings => _settings;

    public IReadOnlyList<TimetableClass> Classes => _document.Classes;

    public TimetableClass? CurrentClass => _currentClass;

    public bool IsEmpty => _document.IsEmpty;

    public bool IsTodayView => _isTodayView;

    public string StatusText { get; private set; } = "还没有课程表，点「导入课表」开始。";

    public string NextLessonText { get; private set; } = string.Empty;

    public string ClassSummary
    {
        get
        {
            if (_currentClass is null)
            {
                return "未选择班级";
            }

            string name = _currentClass.Name;
            return _settings.ReminderEnabled
                ? name + " · 课前 " + _settings.LeadMinutes + " 分钟提醒"
                : name;
        }
    }

    public string TermText => _document.Term.Length > 0 ? _document.Term : "未命名单学期";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _store.LibraryRoot = ItemSyncBridge.LibraryRoot;
        await ReloadAsync().ConfigureAwait(true);
        _timer?.Start();
    }

    public async Task ReloadAsync()
    {
        try
        {
            _store.LibraryRoot = ItemSyncBridge.LibraryRoot;
            _document = _store.Load();
            _settings = _store.LoadSettings();
            RefreshRunningPeriod(raise: false);

            if (_document.IsEmpty)
            {
                StatusText = "还没有课程表，点「导入课表」开始。";
                _currentClass = null;
                RaiseAll();
                return;
            }

            _currentClass = _document.FindClass(_settings.ActiveClassId) ?? _document.DefaultClass;
            if (_currentClass is not null && _currentClass.Id != _settings.ActiveClassId)
            {
                _settings = _settings with { ActiveClassId = _currentClass.Id };
                await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
            }

            int issues = _document.Issues.Count;
            StatusText = "共 " + _document.Classes.Count + " 个班"
                         + (_currentClass is null ? string.Empty : "，当前 " + _currentClass.Name)
                         + (issues > 0 ? "，导入时记下 " + issues + " 处待核" : string.Empty);
            RefreshNextLesson();
            RaiseAll();
        }
        catch (Exception ex)
        {
            StatusText = "读取课程表失败：" + ex.Message;
            App.Log("[Timetable] reload failed: " + ex.Message);
            RaiseAll();
        }
    }

    /// <summary>从剪贴板/文本框导入的制表符文本。</summary>
    public async Task<(bool Ok, string Message)> ImportTextAsync(string text)
    {
        IReadOnlyList<IReadOnlyList<string>> grid = TimetableTsv.Parse(text);
        return await ImportGridAsync(grid, "粘贴").ConfigureAwait(true);
    }

    public async Task<(bool Ok, string Message)> ImportFileAsync(string path)
    {
        if (path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "老式 .xls 请用 WPS「另存为 .xlsx」，或整块复制后点「从剪贴板导入」。");
        }

        XlsxSheetReader.Result sheet = XlsxSheetReader.ReadFirstSheet(path);
        if (!sheet.Ok)
        {
            return (false, sheet.Error ?? "读不出工作表。");
        }

        return await ImportGridAsync(sheet.Rows, path);
    }

    private async Task<(bool Ok, string Message)> ImportGridAsync(
        IReadOnlyList<IReadOnlyList<string>> grid,
        string source)
    {
        TimetableParseResult parsed = TimetableGridParser.Parse(
            grid,
            new TimetableParseOptions(
                MyTeacherName: _settings.MyTeacherName,
                Term: _settings.Term,
                SingleClassName: _currentClass?.Name));

        if (!parsed.Ok)
        {
            return (false, parsed.Error ?? "解析失败。");
        }

        TimetableParseReport report = parsed.Report;
        TimetableDocument document = parsed.Document with
        {
            Periods = _settings.Periods.Count > 0 ? _settings.Periods : parsed.Document.Periods
        };

        try
        {
            _document = await _store.SaveAsync(document).ConfigureAwait(true);
            _currentClass = _document.FindClass(_settings.ActiveClassId) ?? _document.DefaultClass;
            _settings = _settings with
            {
                ActiveClassId = _currentClass?.Id ?? string.Empty,
                Term = _document.Term
            };
            await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
            _notified.Clear();
            RefreshNextLesson();
            StatusText = "已导入（" + source + "）：" + TimetableGridParser.Describe(report);
            RaiseAll();
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            App.Log("[Timetable] save failed: " + ex.Message);
            return (false, "写入失败：" + ex.Message);
        }
    }

    public async Task SelectClassAsync(string classId)
    {
        TimetableClass? target = _document.FindClass(classId);
        if (target is null)
        {
            return;
        }

        _currentClass = target;
        _settings = _settings with { ActiveClassId = target.Id };
        await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
        RefreshNextLesson();
        OnPropertyChanged(nameof(ClassSummary));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetTodayView(bool today)
    {
        if (_isTodayView == today)
        {
            return;
        }

        _isTodayView = today;
        OnPropertyChanged(nameof(IsTodayView));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetReminderEnabledAsync(bool enabled)
    {
        _settings = _settings with { ReminderEnabled = enabled };
        await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
        OnPropertyChanged(nameof(ClassSummary));
    }

    public async Task SetLeadMinutesAsync(int minutes)
    {
        _settings = _settings with { LeadMinutes = Math.Clamp(minutes, 0, 60) };
        await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
    }

    public async Task SetMyTeacherNameAsync(string name)
    {
        _settings = _settings with { MyTeacherName = name.Trim() };
        await _store.SaveSettingsAsync(_settings).ConfigureAwait(true);
    }

    /// <summary>30 秒一次：刷新「下一节」文案与当前节高亮，必要时发提醒。</summary>
    public async Task TickAsync()
    {
        if (_disposed || _document.IsEmpty)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        RefreshNextLesson();
        RefreshRunningPeriod(raise: true);

        if (!_settings.ReminderEnabled)
        {
            return;
        }

        if (!_ledgerLoaded)
        {
            TimetableReminderLedger ledger = _store.LoadLedger();
            foreach (string key in ledger.Keys)
            {
                _notified.Add(key);
            }

            _ledgerLoaded = true;
        }

        TimetableReminderCandidate? candidate = TimetableReminderPlanner.Evaluate(_document, _settings, now, _notified);
        if (candidate is null)
        {
            return;
        }

        _notified.Add(candidate.Key);
        bool shown = ShowToast(candidate);
        if (!shown)
        {
            StatusText = "系统通知不可用，只在格子里提示下一节。";
            OnPropertyChanged(nameof(StatusText));
        }

        await _store.SaveLedgerAsync(new TimetableReminderLedger(_notified.ToList(), now.ToString("o"))).ConfigureAwait(true);
        App.Log("[Timetable] reminder fired key=" + candidate.Key + " shown=" + shown);
    }

    private bool ShowToast(TimetableReminderCandidate candidate)
    {
        try
        {
            NativeAppNotificationService? service = (App.Current as App)?.NativeNotificationService;
            if (service is null)
            {
                return false;
            }

            return service.TryShow(
                candidate.Title,
                candidate.Message,
                new Dictionary<string, string>
                {
                    ["kind"] = "timetable",
                    ["classId"] = candidate.Class.Id,
                    ["day"] = candidate.Cell.Day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["period"] = candidate.Period.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                actions: null,
                comboBoxes: null,
                options: new NativeAppNotificationOptions(Tag: "timetable", Group: "timetable"));
        }
        catch (Exception ex)
        {
            App.Log("[Timetable] toast failed: " + ex.Message);
            return false;
        }
    }

    private void RefreshNextLesson()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string? text = TimetableReminderPlanner.DescribeNextLesson(
            _document,
            _currentClass,
            _settings.Periods,
            now,
            out int minutes);
        TimetablePeriod? running = TimetableSchedule.RunningPeriod(_settings.Periods, now);
        TimetableCell? runningCell = running is null || _currentClass is null
            ? null
            : _currentClass.CellAt(TimetableSchedule.DayIndex(now), running.Index);

        string value;
        if (running is not null && runningCell is not null)
        {
            value = "正在上 第" + running.Index + "节 " + runningCell.Subject
                    + (string.IsNullOrEmpty(runningCell.Teacher) ? string.Empty : " · " + runningCell.Teacher);
            if (!string.IsNullOrEmpty(text) && minutes > 0)
            {
                value += "　|　下一节 " + text + "（" + minutes + " 分钟）";
            }
        }
        else if (!string.IsNullOrEmpty(text))
        {
            value = "下一节 " + text + "（" + minutes + " 分钟后）";
        }
        else
        {
            value = TimetableSchedule.DayIndex(now) < 0 ? "今天休息" : "今天没有更多课了";
        }

        if (NextLessonText == value)
        {
            return;
        }

        NextLessonText = value;
        OnPropertyChanged(nameof(NextLessonText));
    }

    private void RefreshRunningPeriod(bool raise)
    {
        TimetablePeriod? running = TimetableSchedule.RunningPeriod(_settings.Periods, DateTimeOffset.Now);
        int index = running?.Index ?? -1;
        if (index == _lastRunningPeriod)
        {
            return;
        }

        _lastRunningPeriod = index;
        if (raise)
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ClassSummary));
        OnPropertyChanged(nameof(TermText));
        OnPropertyChanged(nameof(NextLessonText));
        OnPropertyChanged(nameof(CurrentClass));
        OnPropertyChanged(nameof(Classes));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();
    }
}
