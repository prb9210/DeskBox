using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskBox.Services;

/// <summary>一条最近的整理记录（引擎 /api/status 的 recent[]）。</summary>
public sealed record CompanionRecentMove(string Name, string From, string To, string Reason, string At);

/// <summary>One decoded snapshot of the organizer engine's status endpoint.</summary>
public sealed class CompanionStatus
{
    public bool Reachable { get; init; }
    public string Device { get; init; } = string.Empty;
    public string Library { get; init; } = string.Empty;
    public string Desktop { get; init; } = string.Empty;
    public string LastScan { get; init; } = string.Empty;
    public string LastApply { get; init; } = string.Empty;
    public int LastApplyCount { get; init; }
    public int PendingCount { get; init; }
    public IReadOnlyList<CompanionRecentMove> Recent { get; init; } = Array.Empty<CompanionRecentMove>();

    public string LastSync { get; init; } = string.Empty;
    public string LastSyncError { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;

    /// <summary>引擎报告的正常结果（撤销、学到规则、拖回桌面…），不是错误。</summary>
    public string Hint { get; init; } = string.Empty;
    public string SyncNote { get; init; } = string.Empty;
    public string TrashDir { get; init; } = string.Empty;
    public string AiModel { get; init; } = string.Empty;
    public bool AiEnabled { get; init; }
    public int AiCalls { get; init; }
    public bool SyncBusy { get; init; }
    public bool OrgBusy { get; init; }
    public int IntervalMinutes { get; init; }
    public string SyncMode { get; init; } = string.Empty;
}

/// <summary>
/// Talks to the bundled organizer engine over its loopback dashboard.
/// Everything goes through JsonDocument (Native AOT trims reflection-based
/// System.Text.Json deserialization), and every call fails soft so the panel
/// can show "管家未运行" instead of throwing at the user.
/// </summary>
public static class CompanionClient
{
    public const string BaseUrl = "http://127.0.0.1:8765";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<CompanionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using System.Net.Http.HttpResponseMessage response =
                await Http.GetAsync(BaseUrl + "/api/status", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new CompanionStatus { Reachable = false };
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return new CompanionStatus
            {
                Reachable = true,
                Device = GetString(root, "device"),
                Library = GetString(root, "library"),
                Desktop = GetString(root, "desktop"),
                LastScan = FormatTime(GetString(root, "last_scan")),
                LastApply = FormatTime(GetString(root, "last_apply")),
                LastApplyCount = (int)GetNumber(root, "last_apply_count"),
                PendingCount = (int)GetNumber(root, "pending_count"),
                Recent = ParseRecent(root),
                LastSync = FormatTime(GetString(root, "last_sync")),
                LastSyncError = GetString(root, "last_sync_error"),
                LastError = GetString(root, "last_error"),
                Hint = GetString(root, "hint"),
                SyncNote = GetString(root, "sync_note"),
                TrashDir = GetString(root, "trash_dir"),
                AiModel = GetString(root, "ai_model"),
                AiEnabled = GetBool(root, "ai_enabled"),
                AiCalls = (int)GetNumber(root, "ai_calls"),
                SyncBusy = GetBool(root, "sync_busy"),
                OrgBusy = GetBool(root, "org_busy"),
                IntervalMinutes = (int)GetNumber(root, "sync_interval_minutes"),
                SyncMode = GetString(root, "sync_mode")
            };
        }
        catch (Exception)
        {
            return new CompanionStatus { Reachable = false };
        }
    }

    /// <summary>GETs a JSON endpoint; null when the engine is down or the body is not JSON.</summary>
    public static async Task<JsonDocument?> GetJsonAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using System.Net.Http.HttpResponseMessage response =
                await Http.GetAsync(BaseUrl + path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>POSTs a JSON body to an endpoint and returns the response text.</summary>
    public static async Task<(bool Ok, string Message)> PostJsonAsync(
        string path,
        string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using System.Net.Http.HttpResponseMessage response =
                await Http.PostAsync(BaseUrl + path, content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, "连不上管家引擎：" + ex.Message);
        }
    }

    /// <summary>PUTs a JSON body (used by the cross-machine layout channel).</summary>
    public static async Task<(bool Ok, string Message)> PutJsonAsync(
        string path,
        string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using System.Net.Http.HttpResponseMessage response =
                await Http.PutAsync(BaseUrl + path, content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, "连不上管家引擎：" + ex.Message);
        }
    }

    /// <summary>POSTs one of the engine's action endpoints and returns its text.</summary>
    /// <summary>扫描是重活（多页 300dpi 要找纸边、拉正、编码），单独一个长超时
    /// 的 HttpClient，不能用 15 秒的那个。</summary>
    private static readonly HttpClient LongHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(240),
    };

    /// <summary>POST /api/docscan：图片 → 增强 PDF。</summary>
    public static async Task<(bool Ok, string Body)> PostDocScanAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response =
                await LongHttp.PostAsync(BaseUrl + "/api/docscan", content, cancellationToken)
                    .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception)
        {
            return (false, string.Empty);
        }
    }

    public static async Task<(bool Ok, string Message)> PostAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using System.Net.Http.HttpResponseMessage response =
                await Http.PostAsync(BaseUrl + path, null, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, "连不上管家引擎：" + ex.Message);
        }
    }

    public static async Task<(bool Ok, string Message)> SetIntervalMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string payload = "{\"interval_minutes\":" + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using System.Net.Http.HttpResponseMessage response =
                await Http.PostAsync(BaseUrl + "/api/settings", content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, "连不上管家引擎：" + ex.Message);
        }
    }

    /// <summary>recent[] → 悬浮球「最近整理」菜单用的前 8 条。</summary>
    private static IReadOnlyList<CompanionRecentMove> ParseRecent(JsonElement root)
    {
        var list = new List<CompanionRecentMove>();
        if (!root.TryGetProperty("recent", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string from = GetString(item, "from");
            string to = GetString(item, "to");
            string name = string.IsNullOrEmpty(from) ? to : Path.GetFileName(from);
            if (string.IsNullOrEmpty(name))
            {
                name = to;
            }

            list.Add(new CompanionRecentMove(name, from, to, GetString(item, "reason"), FormatTime(GetString(item, "at"))));
            if (list.Count >= 8)
            {
                break;
            }
        }

        return list;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double GetNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>RFC3339 → "MM-dd HH:mm:ss"; the engine's zero time means "never".</summary>
    private static string FormatTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (!DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
        {
            return raw;
        }

        return parsed.Year <= 2000 ? string.Empty : parsed.ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
