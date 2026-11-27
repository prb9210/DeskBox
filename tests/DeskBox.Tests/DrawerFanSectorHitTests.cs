using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// 扇片的拖放命中只由「松手时的角度」决定，与窗口尺寸、DPI、扇片视觉无关，
/// 所以这段数学可以在 Linux 上直接单测——WinUI 的拖放本身只能靠用户截图验收，
/// 但「松手落在哪个扇片」必须先在 CI 里钉死，否则改一次角度就可能整体错位一格。
/// </summary>
public class DrawerFanSectorHitTests
{
    [Fact]
    public void FirstSectorSitsAtTwelveOClockAndTheRestGoClockwise()
    {
        Assert.Equal("stage", DrawerFanActions.All[0].Id);
        Assert.Equal(0, DrawerFanActions.IndexAt(-90, 4)); // 正上
        Assert.Equal(1, DrawerFanActions.IndexAt(0, 4));   // 正右
        Assert.Equal(2, DrawerFanActions.IndexAt(90, 4));  // 正下
        Assert.Equal(3, DrawerFanActions.IndexAt(180, 4)); // 正左
    }

    [Theory]
    [InlineData(-89.9, 0)]
    [InlineData(-46.0, 0)]
    [InlineData(-44.0, 1)]
    [InlineData(0.0, 1)]
    [InlineData(44.0, 1)]
    [InlineData(46.0, 2)]
    [InlineData(89.0, 2)]
    [InlineData(134.0, 2)]
    [InlineData(136.0, 3)]
    [InlineData(179.0, 3)]
    [InlineData(226.0, 0)]
    [InlineData(269.0, 0)]
    [InlineData(314.0, 0)]
    public void WedgesCoverTheWholeCircleWithoutGaps(double degrees, int expected)
    {
        Assert.Equal(expected, DrawerFanActions.IndexAt(degrees, 4));
    }

    [Fact]
    public void SixSectorsKeepEachMidpointInsideItsOwnWedge()
    {
        for (int i = 0; i < 6; i++)
        {
            double mid = DrawerFanActions.MidAngle(i, 6);
            Assert.Equal(i, DrawerFanActions.IndexAt(mid, 6));
            Assert.Equal(i, DrawerFanActions.IndexAt(mid + 20, 6));
            Assert.Equal(i, DrawerFanActions.IndexAt(mid - 20, 6));
        }
    }

    [Fact]
    public void BeadDropFallsBackToASectorThatAcceptsFiles()
    {
        // 落在球心/空隙时用 Default；它必须接受文件，否则「拖到球上松手」会静默什么都不做。
        Assert.Equal("stage", DrawerFanActions.Default.Id);
        Assert.True(DrawerFanActions.Default.AcceptsFiles);
    }
}
