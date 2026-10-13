using FileConverter.Core;
using Xunit;

namespace FileConverter.Tests;

public class ToolLocatorTests
{
    [Theory]
    // what distributions ship
    [InlineData("4.4.2-0ubuntu0.22.04.1", true)]
    [InlineData("5.1.6-0+deb12u1", true)]
    [InlineData("6.1.1-3ubuntu5", true)]
    [InlineData("7.0.2-static", true)]
    [InlineData("n8.0.1", true)]
    // the builds the app downloads, and newer ones
    [InlineData("9.0.2-essentials_build-www.gyan.dev", false)]
    [InlineData("n9.0.2-12-g1a2b3c4d5e-20261001", false)]
    [InlineData("9.1", false)]
    [InlineData("10.0", false)]
    // development snapshots carry no release number: newer than any release
    [InlineData("N-121257-g8a3f5c1e2b", false)]
    [InlineData("2026-08-11-git-43cde54fc1-full_build-www.gyan.dev", false)]
    [InlineData("custom", false)]
    [InlineData(null, false)]
    public void AnInstalledFfmpegFromBeforeTheTestedReleaseIsPassedOver(string? version, bool outdated)
    {
        Assert.Equal(outdated, ToolLocator.IsOutdatedFfmpeg(version));
    }
}
