using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetCapsuleOrderCalculatorTests
{
    [Fact]
    public void HorizontalDrag_MovesMemberToNearestLogicalSlot()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40),
            new(176, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, result);
    }

    [Fact]
    public void ReversedPhysicalLayout_StillUsesPersistedLogicalSlots()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(176, 0, 80, 40),
            new(88, 0, 80, 40),
            new(0, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(4, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, result);
    }

    [Fact]
    public void VerticalDrag_UsesVerticalAxisOnly()
    {
        string[] order = ["one", "two", "three"];
        RectInt32[] slots =
        [
            new(20, 0, 120, 40),
            new(20, 48, 120, 40),
            new(20, 96, 120, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "three",
            new RectInt32(400, 0, 120, 40),
            SettingsService.WidgetCapsuleBarDirectionVertical);

        Assert.Equal(new[] { "three", "one", "two" }, result);
    }

    [Fact]
    public void SmallDrag_KeepsCurrentOrder()
    {
        string[] order = ["one", "two"];
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            order,
            slots,
            "one",
            new RectInt32(12, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(order, result);
    }

    [Fact]
    public void RepeatedDrag_UsesStableSlotsAfterFirstReorder()
    {
        RectInt32[] slots =
        [
            new(0, 0, 80, 40),
            new(88, 0, 80, 40),
            new(176, 0, 80, 40)
        ];
        IReadOnlyList<string> first = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            ["one", "two", "three"],
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        IReadOnlyList<string> second = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            first,
            slots,
            "one",
            new RectInt32(170, 0, 80, 40),
            SettingsService.WidgetCapsuleBarDirectionHorizontal);

        Assert.Equal(new[] { "two", "three", "one" }, second);
    }

    [Fact]
    public void MergeGroupOrder_PreservesMembersFromOtherDisplays()
    {
        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MergeGroupOrder(
            ["left-one", "right-one", "left-two", "right-two"],
            ["left-two", "left-one"]);

        Assert.Equal(
            new[] { "left-two", "right-one", "left-one", "right-two" },
            result);
    }

    [Fact]
    public void PointerCrossing_UsesSlotCenters_NotNearestSlot()
    {
        // 高矮不一的格子：只越过一格中心时，就应当换到那格之后，
        // 不能因为「离自己原来的槽更近」而弹回去。
        string[] order = ["tall", "short", "medium"];
        RectInt32[] slots =
        [
            new(0, 0, 200, 120),
            new(0, 128, 200, 42),
            new(0, 178, 200, 64)
        ];

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToPointer(
            order,
            slots,
            "tall",
            new RectInt32(0, 110, 200, 120),
            SettingsService.WidgetCapsuleBarDirectionVertical);

        Assert.Equal(new[] { "short", "tall", "medium" }, result);
    }

    [Fact]
    public void FeatureWidget_CannotBeInsertedInsideTheFolderBlock()
    {
        string[] order = ["folder-1", "folder-2", "folder-3", "todo"];
        RectInt32[] slots =
        [
            new(0, 0, 200, 60),
            new(0, 68, 200, 60),
            new(0, 136, 200, 60),
            new(0, 204, 200, 120)
        ];
        var folders = new HashSet<string>(
            ["folder-1", "folder-2", "folder-3"],
            StringComparer.Ordinal);

        // 把待办拖到第二个文件夹的位置（块内部）→ 只能吸到块前或块后。
        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToPointer(
            order,
            slots,
            "todo",
            new RectInt32(0, 70, 200, 120),
            SettingsService.WidgetCapsuleBarDirectionVertical,
            folders);

        Assert.Equal(new[] { "folder-1", "folder-2", "folder-3", "todo" }, result);
    }

    [Fact]
    public void FolderMember_StaysInsideTheBlock()
    {
        string[] order = ["weather", "folder-1", "folder-2", "folder-3"];
        RectInt32[] slots =
        [
            new(0, 0, 200, 60),
            new(0, 68, 200, 60),
            new(0, 136, 200, 60),
            new(0, 204, 200, 60)
        ];
        var folders = new HashSet<string>(
            ["folder-1", "folder-2", "folder-3"],
            StringComparer.Ordinal);

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.MoveToPointer(
            order,
            slots,
            "folder-1",
            new RectInt32(0, -40, 200, 60),
            SettingsService.WidgetCapsuleBarDirectionVertical,
            folders);

        Assert.Equal(new[] { "weather", "folder-1", "folder-2", "folder-3" }, result);
    }

    [Fact]
    public void GroupContiguous_PullsSplitFoldersBackTogether()
    {
        var folders = new HashSet<string>(
            ["f1", "f2", "f3"],
            StringComparer.Ordinal);

        IReadOnlyList<string> result = WidgetCapsuleOrderCalculator.GroupContiguous(
            ["weather", "f1", "todo", "f2", "f3", "search"],
            folders);

        Assert.Equal(new[] { "weather", "f1", "f2", "f3", "todo", "search" }, result);
    }
}
