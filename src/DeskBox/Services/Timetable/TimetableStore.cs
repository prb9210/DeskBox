using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DeskBox.Models;

namespace DeskBox.Services.Timetable;

/// <summary>
/// 课程表落盘。
///
/// 存放策略（与方案一致，刻意不复用待办的逐条合并）：
/// 1. 课表本体整份放资料库 <c>&lt;库&gt;\_deskbox\timetable\timetable.json</c>，随坚果云双机共用；
/// 2. 本机留一份缓存 <c>%LOCALAPPDATA%\DeskBox\timetable.json</c>，资料库拔盘时照样能看；
/// 3. 冲突按「revision 大者胜、相等取较新文件」整份覆盖，另存一份
///    <c>timetable.conflict-时间.json</c> 供人工比对——逐格合并会把两张课表杂交，
///    比整份覆盖危险得多。
///
/// 全部用 <see cref="JsonNode"/> 读写，不碰 JsonSerializer：仓库有 AOT 告警闸门，
/// 还有「JsonSerializer 调用点数量」的基线测试，多一个调用点就红。
/// </summary>
public sealed class TimetableStore
{
    private const string LibraryFolderName = "timetable";
    private const string FileName = "timetable.json";
    private const string SettingsFileName = "timetable-settings.json";
    private const string LedgerFileName = "timetable-reminders.json";

    private readonly string _dataDirectory;

    public TimetableStore(string? dataDirectory = null)
    {
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : dataDirectory;
    }

    /// <summary>资料库根目录（引擎 /api/status 报的绝对路径）。空 = 只用本机缓存。</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    public string LocalPath => Path.Combine(_dataDirectory, FileName);

    public string SettingsPath => Path.Combine(_dataDirectory, SettingsFileName);

    public string LedgerPath => Path.Combine(_dataDirectory, LedgerFileName);

    public string LibraryPath => string.IsNullOrEmpty(LibraryRoot)
        ? string.Empty
        : Path.Combine(LibraryRoot, "_deskbox", LibraryFolderName, FileName);

    /// <summary>读：两份都存在时取 revision 大的；相等取本机（本机刚导入的那份）。</summary>
    public TimetableDocument Load()
    {
        TimetableDocument local = ReadDocument(LocalPath) ?? TimetableDocument.Empty;
        TimetableDocument library = ReadDocument(LibraryPath) ?? TimetableDocument.Empty;

        if (library.IsEmpty)
        {
            return local;
        }

        if (local.IsEmpty)
        {
            return library;
        }

        return library.Revision > local.Revision ? library : local;
    }

    /// <summary>写：先涨 revision，再写本机缓存与资料库各一份。返回落盘后的文档。</summary>
    public async Task<TimetableDocument> SaveAsync(TimetableDocument document)
    {
        TimetableDocument next = document with
        {
            Revision = Math.Max(1, document.Revision + 1),
            UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        };

        string json = ToJson(next).ToJsonString();
        await ResilientJsonStore.SaveAsync(LocalPath, json).ConfigureAwait(false);

        string libraryPath = LibraryPath;
        if (libraryPath.Length > 0)
        {
            TimetableDocument? existing = ReadDocument(libraryPath);
            if (existing is not null && existing.Revision > next.Revision)
            {
                // 另一台刚导入过更新的：保留对端那份为冲突副本，让用户自己决定。
                await ResilientJsonStore.SaveAsync(BuildConflictPath(libraryPath), json).ConfigureAwait(false);
            }

            await ResilientJsonStore.SaveAsync(libraryPath, json).ConfigureAwait(false);
        }

        return next;
    }

    public TimetableLocalSettings LoadSettings()
    {
        JsonObject? root = ReadObject(SettingsPath);
        if (root is null)
        {
            return TimetableLocalSettings.Default;
        }

        List<TimetablePeriod> periods = new();
        if (root["periods"] is JsonArray periodArray)
        {
            foreach (JsonNode? node in periodArray)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                periods.Add(new TimetablePeriod(
                    Index: Int(item, "index", periods.Count + 1),
                    Start: Text(item, "start"),
                    End: Text(item, "end"),
                    Section: Text(item, "section")));
            }
        }

        List<string> closed = new();
        if (root["closedDates"] is JsonArray closedArray)
        {
            foreach (JsonNode? node in closedArray)
            {
                string value = node?.GetValue<string>() ?? string.Empty;
                if (value.Length > 0)
                {
                    closed.Add(value);
                }
            }
        }

        return new TimetableLocalSettings(
            ActiveClassId: Text(root, "activeClassId"),
            ReminderEnabled: Bool(root, "reminderEnabled", true),
            LeadMinutes: Int(root, "leadMinutes", TimetableLocalSettings.DefaultLeadMinutes),
            Scope: (TimetableReminderScope)Int(root, "scope", (int)TimetableReminderScope.MyClasses),
            MyTeacherName: Text(root, "myTeacherName"),
            Term: Text(root, "term"),
            Periods: periods.Count > 0 ? periods : TimetableSchedule.DefaultPeriods,
            ClosedDates: closed);
    }

    public async Task SaveSettingsAsync(TimetableLocalSettings settings)
    {
        JsonObject root = new()
        {
            ["activeClassId"] = settings.ActiveClassId,
            ["reminderEnabled"] = settings.ReminderEnabled,
            ["leadMinutes"] = settings.LeadMinutes,
            ["scope"] = (int)settings.Scope,
            ["myTeacherName"] = settings.MyTeacherName,
            ["term"] = settings.Term
        };

        JsonArray periods = new();
        foreach (TimetablePeriod period in settings.Periods)
        {
            periods.Add((JsonNode)new JsonObject
            {
                ["index"] = period.Index,
                ["start"] = period.Start,
                ["end"] = period.End,
                ["section"] = period.Section
            });
        }

        root["periods"] = periods;

        JsonArray closed = new();
        foreach (string date in settings.ClosedDates)
        {
            closed.Add((JsonNode?)JsonValue.Create(date));
        }

        root["closedDates"] = closed;
        await ResilientJsonStore.SaveAsync(SettingsPath, root.ToJsonString()).ConfigureAwait(false);
    }

    public TimetableReminderLedger LoadLedger()
    {
        JsonObject? root = ReadObject(LedgerPath);
        if (root is null)
        {
            return TimetableReminderLedger.Empty;
        }

        List<string> keys = new();
        if (root["keys"] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                string value = node?.GetValue<string>() ?? string.Empty;
                if (value.Length > 0)
                {
                    keys.Add(value);
                }
            }
        }

        return new TimetableReminderLedger(keys, Text(root, "updatedAt"));
    }

    /// <summary>去重记录只保留最近 7 天，防止一年后文件里躺着几千个键。</summary>
    public async Task SaveLedgerAsync(TimetableReminderLedger ledger)
    {
        string cutoff = DateTimeOffset.Now.AddDays(-7).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        List<string> kept = new();
        foreach (string key in ledger.Keys)
        {
            // 键格式：yyyyMMdd|节次|班级id
            int bar = key.IndexOf('|');
            string day = bar > 0 ? key[..bar] : key;
            if (string.CompareOrdinal(day, cutoff) >= 0)
            {
                kept.Add(key);
            }
        }

        JsonObject root = new()
        {
            ["updatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        };

        JsonArray array = new();
        foreach (string key in kept)
        {
            array.Add((JsonNode?)JsonValue.Create(key));
        }

        root["keys"] = array;
        await ResilientJsonStore.SaveAsync(LedgerPath, root.ToJsonString()).ConfigureAwait(false);
    }

    private string BuildConflictPath(string libraryPath)
    {
        string directory = Path.GetDirectoryName(libraryPath) ?? _dataDirectory;
        string stamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(directory, "timetable.conflict-" + stamp + ".json");
    }

    private static JsonObject? ReadObject(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimetableDocument? ReadDocument(string path)
    {
        JsonObject? root = ReadObject(path);
        if (root is null || root["classes"] is not JsonArray classArray || classArray.Count == 0)
        {
            return null;
        }

        List<string> days = new();
        if (root["days"] is JsonArray dayArray)
        {
            foreach (JsonNode? node in dayArray)
            {
                days.Add(node?.GetValue<string>() ?? string.Empty);
            }
        }

        List<TimetablePeriod> periods = new();
        if (root["periods"] is JsonArray periodArray)
        {
            foreach (JsonNode? node in periodArray)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                periods.Add(new TimetablePeriod(
                    Int(item, "index", periods.Count + 1),
                    Text(item, "start"),
                    Text(item, "end"),
                    Text(item, "section")));
            }
        }

        List<TimetableClass> classes = new();
        foreach (JsonNode? node in classArray)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            List<TimetableCell> cells = new();
            if (item["cells"] is JsonArray cellArray)
            {
                foreach (JsonNode? cellNode in cellArray)
                {
                    if (cellNode is not JsonObject cell)
                    {
                        continue;
                    }

                    cells.Add(new TimetableCell(
                        Int(cell, "d", 0),
                        Int(cell, "p", 1),
                        Text(cell, "subject"),
                        NullableText(cell, "teacher"),
                        NullableText(cell, "room"),
                        NullableText(cell, "note"),
                        NullableText(cell, "weekParity")));
                }
            }

            classes.Add(new TimetableClass(
                Id: Text(item, "id"),
                Name: Text(item, "name"),
                Grade: NullableText(item, "grade"),
                IsMine: Bool(item, "isMine", false),
                Cells: cells));
        }

        return new TimetableDocument(
            Term: Text(root, "term"),
            School: Text(root, "school"),
            Days: days.Count > 0 ? days : TimetableDocument.Empty.Days,
            Periods: periods.Count > 0 ? periods : TimetableSchedule.DefaultPeriods,
            Classes: classes,
            Issues: [],
            Revision: Int(root, "revision", 0),
            UpdatedAt: Text(root, "updatedAt"));
    }

    private static JsonObject ToJson(TimetableDocument document)
    {
        JsonObject root = new()
        {
            ["schema"] = TimetableDocument.CurrentSchema,
            ["revision"] = document.Revision,
            ["updatedAt"] = document.UpdatedAt,
            ["term"] = document.Term,
            ["school"] = document.School
        };

        JsonArray days = new();
        foreach (string day in document.Days)
        {
            days.Add((JsonNode?)JsonValue.Create(day));
        }

        root["days"] = days;

        JsonArray periods = new();
        foreach (TimetablePeriod period in document.Periods)
        {
            periods.Add((JsonNode)new JsonObject
            {
                ["index"] = period.Index,
                ["start"] = period.Start,
                ["end"] = period.End,
                ["section"] = period.Section
            });
        }

        root["periods"] = periods;

        JsonArray classes = new();
        foreach (TimetableClass item in document.Classes)
        {
            JsonArray cells = new();
            foreach (TimetableCell cell in item.Cells)
            {
                JsonObject entry = new()
                {
                    ["d"] = cell.Day,
                    ["p"] = cell.Period,
                    ["subject"] = cell.Subject
                };

                if (!string.IsNullOrEmpty(cell.Teacher))
                {
                    entry["teacher"] = cell.Teacher;
                }

                if (!string.IsNullOrEmpty(cell.Room))
                {
                    entry["room"] = cell.Room;
                }

                if (!string.IsNullOrEmpty(cell.Note))
                {
                    entry["note"] = cell.Note;
                }

                if (!string.IsNullOrEmpty(cell.WeekParity))
                {
                    entry["weekParity"] = cell.WeekParity;
                }

                cells.Add((JsonNode)entry);
            }

            JsonObject entry2 = new()
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["isMine"] = item.IsMine,
                ["cells"] = cells
            };

            if (!string.IsNullOrEmpty(item.Grade))
            {
                entry2["grade"] = item.Grade;
            }

            classes.Add((JsonNode)entry2);
        }

        root["classes"] = classes;
        return root;
    }

    private static string Text(JsonObject node, string key)
    {
        JsonNode? value = node[key];
        if (value is null)
        {
            return string.Empty;
        }

        return value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString().Trim('"');
    }

    private static string? NullableText(JsonObject node, string key)
    {
        string value = Text(node, key);
        return value.Length == 0 ? null : value;
    }

    private static int Int(JsonObject node, string key, int fallback)
    {
        JsonNode? value = node[key];
        if (value is null)
        {
            return fallback;
        }

        if (value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            value.AsValue().TryGetValue(out int number))
        {
            return number;
        }

        return int.TryParse(value.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    private static bool Bool(JsonObject node, string key, bool fallback)
    {
        JsonNode? value = node[key];
        if (value is null)
        {
            return fallback;
        }

        if (value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            return value.GetValue<bool>();
        }

        return fallback;
    }
}
