using FileConverter.Core.Diagnostics;
using Xunit;

namespace FileConverter.Tests;

public class ConversionMatrixTests
{
    /// <summary>
    /// Every conversion the format registry offers, on generated samples, with each result
    /// inspected — the same run as the app's own "--self-test". Conversions that need a tool this
    /// machine lacks are skipped by the self-test and listed in the output; anything else must pass.
    /// </summary>
    [Fact(Timeout = 15 * 60 * 1000)]
    public async Task EveryOfferedConversionProducesAValidResult()
    {
        using var folder = TestSetup.NewFolder();
        var results = await SelfTest.RunAsync(folder.Path, filter: null, report: null, TestContext.Current.CancellationToken);

        foreach (var skipped in results.Where(r => r.Outcome == SelfTestOutcome.Skipped).GroupBy(r => r.Detail))
            TestContext.Current.TestOutputHelper?.WriteLine($"skipped {skipped.Count()}: {skipped.Key}");

        var failures = results.Where(r => r.Outcome == SelfTestOutcome.Failed).ToList();
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {results.Count} conversions failed:\n" +
            string.Join("\n", failures.Select(f => $"  {f.Name}: {f.Detail}")));
        Assert.True(results.Count(r => r.Outcome == SelfTestOutcome.Passed) > 50, "the self-test barely ran anything");
    }
}
