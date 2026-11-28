using System;
using System.Threading.Tasks;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

/// <summary>
/// 课程表格子的内容提供者。与天气/万年历同一套接线方式：
/// descriptor 声明种类，provider 造内容，视图与视图模型自己管数据。
/// </summary>
internal sealed class TimetableWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.Timetable;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        return new TimetableWidgetContentAdapter(config);
    }
}

/// <summary>
/// 适配器：把视图模型接到格子宿主上。刷新时机与宿主生命周期一致，
/// 提醒定时器挂在视图模型里，宿主销毁时一并停掉（关格子即停提醒）。
/// </summary>
public sealed class TimetableWidgetContentAdapter : WidgetContentAdapterBase
{
    public TimetableWidgetContentAdapter(WidgetConfig config)
        : this(config, new TimetableWidgetViewModel())
    {
    }

    private TimetableWidgetContentAdapter(WidgetConfig config, TimetableWidgetViewModel viewModel)
        : base(config, () => new TimetableWidgetContent(viewModel))
    {
        ViewModel = viewModel;
    }

    public TimetableWidgetViewModel ViewModel { get; }

    public override Task InitializeAsync() => ViewModel.InitializeAsync();

    public override Task RefreshAsync() => ViewModel.TickAsync();

    public override void OnActivated() => _ = ViewModel.TickAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.Dispose();
        }

        base.Dispose(disposing);
    }
}
