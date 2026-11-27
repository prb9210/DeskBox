using System.Threading.Tasks;

namespace DeskBox.Services;

/// <summary>
/// 手动入口（设置页两个按钮）。真正的同步由 <see cref="LayoutSync.LayoutAutoSync"/>
/// 每 20 秒自动跑；这里只保留「强制以本机为准 / 强制以另一台为准」的兜底，
/// 用于两端都改过、自动合并结果不是用户想要的那种情况。
/// </summary>
public static class LayoutSyncService
{
    public sealed class Result
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
        public int Rev { get; init; }
    }

    /// <summary>把这台机器的布局/样式/悬浮球外观强行推上去。</summary>
    public static async Task<Result> PushAsync(App app, string library)
    {
        if (app.WidgetManager is null)
        {
            return new Result { Ok = false, Message = "格子管理器还没就绪。" };
        }

        (bool ok, string message, int rev) = await LayoutSync.LayoutAutoSync.ForcePushAsync(app, library);
        return new Result { Ok = ok, Message = message, Rev = rev };
    }

    /// <summary>忽略时间戳，整份按另一台的文档覆盖本机。</summary>
    public static async Task<Result> PullAsync(App app, string library)
    {
        if (app.WidgetManager is null)
        {
            return new Result { Ok = false, Message = "格子管理器还没就绪。" };
        }

        (bool ok, string message, int rev) = await LayoutSync.LayoutAutoSync.ForcePullAsync(app, library);
        return new Result { Ok = ok, Message = message, Rev = rev };
    }
}
