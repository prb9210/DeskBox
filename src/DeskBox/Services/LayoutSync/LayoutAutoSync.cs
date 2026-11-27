using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services.LayoutSync;

/// <summary>
/// 双机自动同步：本机布局/样式/悬浮球外观一改，就写进库里的 layout.json；
/// 另一台在 20 秒内自己看到并应用。没有按钮、不需要用户操作。
///
/// 只做两件事：本地有变化才 PUT（并把引擎合并后的结果立刻应用回来），
/// 平时按节奏 GET，拿到比自己新的条目就应用。两台机各自改各自的格子时
/// 互不覆盖——合并粒度是「一条格子 + 一个时间戳」。
/// </summary>
internal sealed class LayoutAutoSync
{
    private static LayoutAutoSync? s_current;

    private readonly App _app;
    private readonly LayoutSyncState _state;
    private DispatcherQueueTimer? _timer;
    private bool _busy;
    private int _ticks;
    private string _library = string.Empty;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
    private DateTimeOffset _libraryAt = DateTimeOffset.MinValue;

    private LayoutAutoSync(App app)
    {
        _app = app;
        _state = LayoutSyncState.Load();
    }

    public static void Start(App app)
    {
        if (s_current is not null)
        {
            return;
        }

        s_current = new LayoutAutoSync(app);
        s_current.Begin();
        App.Log("[LayoutSync] auto sync started");
    }

    private void Begin()
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            App.Log("[LayoutSync] no dispatcher queue; auto sync disabled");
            return;
        }

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(8); // 第一轮早点跑
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_timer is not null && _timer.Interval != TimeSpan.FromSeconds(20))
            {
                _timer.Interval = TimeSpan.FromSeconds(20);
            }

            // 库路径每 10 分钟重问一次：引擎那边迁移过库 / 换了同步根，这里
            // 缓存住旧路径会一直往错的地方写。
            if (_library.Length > 0 && DateTimeOffset.UtcNow - _libraryAt > TimeSpan.FromMinutes(10))
            {
                _library = string.Empty;
            }

            if (_library.Length == 0)
            {
                CompanionStatus status = await CompanionClient.GetStatusAsync();
                if (!status.Reachable || status.Library.Length == 0)
                {
                    return; // 引擎没起来：下一轮再试
                }

                _library = status.Library;
                _libraryAt = DateTimeOffset.UtcNow;
            }

            // 第一次跑：先拉不推。本机状态是空的，直接推会把出厂默认样式
            // 盖上现在时间戳，把另一台调好的材质/球外观冲掉。
            if (_state.IsNew)
            {
                JsonElement? seed = await PullAsync();
                if (seed is JsonElement seedDoc && LayoutDocBuilder.HasContent(seedDoc))
                {
                    LayoutApplier.Result seeded =
                        await LayoutApplier.ApplyAsync(_app, seedDoc, _state, _library);
                    if (seeded.Rev > _state.RemoteRev)
                    {
                        _state.RemoteRev = seeded.Rev;
                    }

                    _state.IsNew = false;
                    _state.Save();
                    App.Log(
                        $"[LayoutSync] 首次同步以另一台为准：接收 {seeded.Applied} 条、" +
                        $"新建 {seeded.Created} 个");
                    return;
                }

                _state.IsNew = false;
            }

            _ticks++;
            LayoutDocBuilder.BuildResult build = LayoutDocBuilder.Build(_app, _library, _state);
            JsonElement? incoming = null;

            // 本地有改动，或者刚启动、距上次推送超过 3 分钟（顺带心跳一次）。
            bool heartbeat = DateTimeOffset.UtcNow - _lastPush > TimeSpan.FromMinutes(3);
            if (build.Changed || heartbeat)
            {
                incoming = await PushAsync(build.Document);
                _state.Save();
            }
            else if (_ticks % 3 == 0)
            {
                incoming = await PullAsync();
            }

            if (incoming is JsonElement doc)
            {
                LayoutApplier.Result applied =
                    await LayoutApplier.ApplyAsync(_app, doc, _state, _library);
                if (applied.Rev > _state.RemoteRev)
                {
                    _state.RemoteRev = applied.Rev;
                }

                _state.Save();
                if (applied.Anything)
                {
                    App.Log(
                        $"[LayoutSync] applied from {applied.UpdatedBy}: " +
                        $"{applied.Applied} moved, {applied.Created} created, " +
                        $"{applied.Removed} removed, style={applied.StyleApplied}, " +
                        $"drawer={applied.DrawerApplied}, capsule={applied.CapsuleApplied}");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("[LayoutSync] tick failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<JsonElement?> PushAsync(JsonObject document)
    {
        (bool ok, string body) = await CompanionClient.PutJsonAsync("/api/layout", document.ToJsonString());
        if (!ok)
        {
            _library = string.Empty; // 下轮重新问一次库路径，可能是引擎重启换了库
            App.Log("[LayoutSync] push failed: " + body);
            return null;
        }

        _lastPush = DateTimeOffset.UtcNow;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<JsonElement?> PullAsync()
    {
        using JsonDocument? parsed = await CompanionClient.GetJsonAsync("/api/layout");
        return parsed?.RootElement.Clone();
    }

    /// <summary>手动「推送到另一台」：把本机全部条目的时间戳顶到当前，
    /// 保证合并时本机赢（这正是按钮的语义）。</summary>
    public static async Task<(bool Ok, string Message, int Rev)> ForcePushAsync(App app, string library)
    {
        if (library.Length == 0)
        {
            return (false, "还没确定共享库位置，稍后再试。", 0);
        }

        LayoutSyncState state = s_current?._state ?? LayoutSyncState.Load();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (LayoutSyncState.WidgetState widget in state.Widgets.Values)
        {
            widget.UpdatedAt = now;
        }

        state.ShellAt = now;
        state.DrawerAt = now;
        state.CapsuleAt = now;
        LayoutDocBuilder.BuildResult build = LayoutDocBuilder.Build(app, library, state);
        (bool ok, string body) = await CompanionClient.PutJsonAsync(
            "/api/layout", build.Document.ToJsonString());
        state.Save();
        if (!ok)
        {
            return (false, "推送失败：" + body, 0);
        }

        int rev = 0;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body);
            JsonElement doc = parsed.RootElement.Clone();
            LayoutApplier.Result applied = await LayoutApplier.ApplyAsync(app, doc, state, library);
            rev = (int)applied.Rev;
            state.Save();
        }
        catch (JsonException)
        {
            // 引擎返回体坏掉不影响「已经推上去」这个事实。
        }

        return (true, $"已把这台机器的布局推上去（rev {rev}）。", rev);
    }

    /// <summary>手动「以另一台为准」：忽略时间戳，整份按对端文档覆盖。</summary>
    public static async Task<(bool Ok, string Message, int Rev)> ForcePullAsync(App app, string library)
    {
        if (library.Length == 0)
        {
            return (false, "还没确定共享库位置，稍后再试。", 0);
        }

        using JsonDocument? parsed = await CompanionClient.GetJsonAsync("/api/layout");
        if (parsed is null)
        {
            return (false, "没读到共享布局：管家引擎没在运行，或另一台还没推送过。", 0);
        }

        LayoutSyncState state = s_current?._state ?? LayoutSyncState.Load();
        LayoutApplier.Result applied =
            await LayoutApplier.ApplyAsync(app, parsed.RootElement, state, library, force: true);
        state.RemoteRev = Math.Max(state.RemoteRev, applied.Rev);
        state.Save();
        string who = applied.UpdatedBy.Length == 0 ? "另一台" : applied.UpdatedBy;
        return (true,
            $"已按 {who} 的布局（rev {applied.Rev}）调整 {applied.Applied + applied.Created} 个格子。",
            (int)applied.Rev);
    }
}
