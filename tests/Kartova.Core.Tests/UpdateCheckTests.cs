using Kartova.App.Services;
using Xunit;

namespace Kartova.Core.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0")]   // the ordinary case
    [InlineData("1.0.1", "1.0.0")]    // tags without the v prefix
    [InlineData("V2.0.0", "1.9.9")]   // capital V
    [InlineData("v1.1.0", "1.0.9")]   // minor beats a higher patch
    [InlineData("v1.0.0.1", "1.0.0")] // four-part versions
    [InlineData(" v1.0.1 ", "1.0.0")] // stray whitespace in the tag
    public void A_newer_tag_is_reported_as_an_update(string tag, string current)
    {
        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateChecker.CompareVersions(tag, current));
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]   // same version
    [InlineData("v0.9.9", "1.0.0")]   // older release still published
    [InlineData("v1.0.0", "1.0.1")]   // running ahead of the release, as a dev build does
    public void Anything_not_newer_is_up_to_date(string tag, string current)
    {
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.CompareVersions(tag, current));
    }

    [Theory]
    [InlineData("v1.1.0-beta.1", "1.0.0", UpdateStatus.UpdateAvailable)]
    [InlineData("v1.0.0-rc1", "1.0.0", UpdateStatus.UpToDate)]
    [InlineData("v1.0.1+build.7", "1.0.0", UpdateStatus.UpdateAvailable)]
    public void Prerelease_and_build_suffixes_are_ignored(string tag, string current, UpdateStatus expected)
    {
        // The suffix is dropped rather than parsed: 1.1.0-beta is still evidence that 1.1.0
        // exists, and comparing suffixes properly would need full semver ordering.
        Assert.Equal(expected, UpdateChecker.CompareVersions(tag, current));
    }

    [Theory]
    [InlineData("nightly", "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("v", "1.0.0")]
    [InlineData("release-candidate", "1.0.0")]
    public void An_unreadable_tag_fails_rather_than_claiming_either_answer(string tag, string current)
    {
        // Reporting "up to date" here would be a false assurance, which is worse than
        // admitting the check did not work.
        Assert.Equal(UpdateStatus.Failed, UpdateChecker.CompareVersions(tag, current));
    }

    [Fact]
    public void The_shipped_version_parses()
    {
        // If AppInfo.Version ever stops being a plain version, every check silently fails.
        Assert.Equal(UpdateStatus.UpToDate, UpdateChecker.CompareVersions(AppInfo.Version, AppInfo.Version));
    }

    [Fact]
    public void The_releases_url_sits_under_the_repository()
    {
        Assert.StartsWith(AppInfo.RepositoryUrl, AppInfo.ReleasesUrl, StringComparison.Ordinal);
        Assert.EndsWith("/releases", AppInfo.ReleasesUrl, StringComparison.Ordinal);
    }
}
