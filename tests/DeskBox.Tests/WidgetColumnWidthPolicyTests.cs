using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetColumnWidthPolicyTests
{
    private static WidgetConfig Member(string id, double width, double? stored = null)
    {
        var config = new WidgetConfig { Id = id, Width = width };
        if (stored is not null)
        {
            config.Metadata[WidgetColumnWidthPolicy.MetadataKey] =
                stored.Value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return config;
    }

    [Fact]
    public void FirstRun_UsesWidestMemberAndRecordsItOnEveryMember()
    {
        var members = new List<WidgetConfig>
        {
            Member("folder", 420),
            Member("music", 320),
            Member("todo", 300)
        };

        Assert.True(WidgetColumnWidthPolicy.ApplyAligned(members));

        Assert.All(members, member => Assert.Equal(420, member.Width, 3));
        Assert.All(
            members,
            member => Assert.Equal(
                "420",
                member.Metadata[WidgetColumnWidthPolicy.MetadataKey]));
    }

    [Fact]
    public void NewlyEnabledMember_AdoptsRecordedColumnWidth_InsteadOfWideningTheColumn()
    {
        // 用户把整列设成 400；随后打开「音乐」，它自带 520 的默认展开宽度。
        var members = new List<WidgetConfig>
        {
            Member("folder", 400, stored: 400),
            Member("todo", 400, stored: 400),
            Member("music", 520)
        };

        WidgetColumnWidthPolicy.ApplyAligned(members);

        Assert.All(members, member => Assert.Equal(400, member.Width, 3));
        Assert.Equal(
            "400",
            members[2].Metadata[WidgetColumnWidthPolicy.MetadataKey]);
    }

    [Fact]
    public void UserResize_BecomesTheNewColumnWidthForEveryone()
    {
        // 用户把待办拖到 460（配置变了、记录还是 400）：整列跟随 460。
        var members = new List<WidgetConfig>
        {
            Member("folder", 400, stored: 400),
            Member("todo", 460, stored: 400),
            Member("music", 400, stored: 400)
        };

        WidgetColumnWidthPolicy.ApplyAligned(members);

        Assert.All(members, member => Assert.Equal(460, member.Width, 3));
    }

    [Fact]
    public void AlignedMode_ClearsStaleCompactWidthOverrides()
    {
        var members = new List<WidgetConfig>
        {
            Member("folder", 400, stored: 400),
            Member("todo", 400, stored: 400)
        };
        members[1].CompactWidth = 248;

        WidgetColumnWidthPolicy.ApplyAligned(members);

        Assert.Null(members[1].CompactWidth);
    }
}
