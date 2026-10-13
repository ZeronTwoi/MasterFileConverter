using FileConverter.Core;
using Xunit;

namespace FileConverter.Tests;

/// <summary>An engine the app downloads has to be the file its publisher made.</summary>
public class DownloadsTests
{
    // SHA-256 of the five bytes "hello"
    private const string Hello = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
    private const string Other = "56e27565e431ed06bff1228f0047ecb78067a78b08269123c3a04e5e057ced78";

    [Fact]
    public void AChecksumFileWithNothingElseInItIsRead()
    {
        Assert.Equal(Hello, Downloads.ParseChecksum(Hello + "\n", "ffmpeg-release-essentials.zip"));
    }

    [Fact]
    public void AListOfChecksumsIsReadForTheFileAskedAbout()
    {
        var list = $"{Other}  ffmpeg-master-latest-win64-gpl.zip\r\n{Hello}  ffmpeg-master-latest-linux64-gpl.tar.xz\r\n";
        Assert.Equal(Hello, Downloads.ParseChecksum(list, "ffmpeg-master-latest-linux64-gpl.tar.xz"));
        // the form sha256sum writes for a file read as binary
        Assert.Equal(Hello, Downloads.ParseChecksum($"{Hello} *engine.zip", "engine.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not found</html>")]
    [InlineData("56e27565e431ed06  too-short.zip")]
    [InlineData("56e27565e431ed06bff1228f0047ecb78067a78b08269123c3a04e5e057ced78  another-file.zip")]
    public void WhatStatesNoChecksumForTheFileGivesNone(string text)
    {
        Assert.Null(Downloads.ParseChecksum(text, "engine.zip"));
    }

    [Fact]
    public void AFileWithItsChecksumIsKept()
    {
        using var folder = TestSetup.NewFolder();
        var file = folder.File("engine.zip");
        File.WriteAllText(file, "hello");

        Downloads.RequireSha256(file, Hello.ToUpperInvariant(), "engine.zip");

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AFileWithAnotherChecksumIsDeletedAndRefused()
    {
        using var folder = TestSetup.NewFolder();
        var file = folder.File("engine.zip");
        File.WriteAllText(file, "hello, but changed on the way");

        var refused = Assert.Throws<FriendlyException>(() => Downloads.RequireSha256(file, Hello, "engine.zip"));

        Assert.Contains("engine.zip", refused.Message);
        Assert.False(File.Exists(file));
    }
}
