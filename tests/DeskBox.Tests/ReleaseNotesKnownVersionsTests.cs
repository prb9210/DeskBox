using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// The release-notes window's version rail unions the published list with
/// each machine's notes cache, so every machine shows the same versions.
/// These pins keep the list strictly to versions that have a GitHub
/// release (their notes can resolve through the tag) and keep it ordered.
/// </summary>
public sealed class ReleaseNotesKnownVersionsTests
{
    [Fact]
    public void KnownReleaseVersions_AreNewestFirst()
    {
        var versions = ReleaseNotesService.KnownReleaseVersions;
        for (int i = 1; i < versions.Count; i++)
        {
            Version previous = Version.Parse(versions[i - 1]);
            Version current = Version.Parse(versions[i]);
            Assert.True(
                previous > current,
                $"{versions[i - 1]} must sort newer than {versions[i]}.");
        }
    }

    [Fact]
    public void KnownReleaseVersions_CoverCurrentReleaseAndTheOldestLiveOne()
    {
        // 当前版本必须在表里（NewestMatchesCurrentRelease 还要求它在第一位），
        // 再挑一个本仓库确实发过的老版本兜底，防止整表被清空。
        Assert.Contains(AppUpdateService.GetCurrentAppVersion(), ReleaseNotesService.KnownReleaseVersions);
        Assert.Contains("1.5.2", ReleaseNotesService.KnownReleaseVersions);
        Assert.Contains("1.5.9", ReleaseNotesService.KnownReleaseVersions);
    }

    [Fact]
    public void KnownReleaseVersions_NewestMatchesCurrentRelease()
    {
        // Appending the new version at release time is a manual step; this
        // pin turns a forgotten append into a red suite on the first build
        // of the next version instead of a silently missing rail entry on
        // every 1.5.9 machine.
        string current = AppUpdateService.GetCurrentAppVersion();
        Assert.NotEqual("0.0.0", current);
        Assert.Equal(current, ReleaseNotesService.KnownReleaseVersions[0]);
    }

    [Fact]
    public void KnownReleaseVersions_ExcludeVersionsWithoutAGitHubRelease()
    {
        // v1.4.4 was never released; v1.4.5 exists as a tag only, so its
        // release notes cannot resolve through the tag URL.
        Assert.DoesNotContain("1.4.4", ReleaseNotesService.KnownReleaseVersions);
        Assert.DoesNotContain("1.4.5", ReleaseNotesService.KnownReleaseVersions);
        Assert.DoesNotContain("1.4.5-rc1", ReleaseNotesService.KnownReleaseVersions);
        // 本项目（fork）没给这些版本发过 Release —— 与上游有 Release 的版本号不能混用。
        Assert.DoesNotContain("1.4.0", ReleaseNotesService.KnownReleaseVersions);
        Assert.DoesNotContain("1.5.1", ReleaseNotesService.KnownReleaseVersions);
        Assert.DoesNotContain("1.5.4", ReleaseNotesService.KnownReleaseVersions);
    }
}
