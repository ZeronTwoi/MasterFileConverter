using FileConverter.Core.Converters;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// The search for the best quality that fits a size, run against a recorded table instead of
/// a real encoder: the sizes of one photo as a JPEG at quality 12, 13, … 95.
/// </summary>
public class QualitySearchTests
{
    private static readonly int[] Sizes =
    [
        37279, 39175, 40868, 42575, 44385, 46069, 47770, 49298, 50661, 52348, 53884, 55427, 57038, 58336,
        59646, 61228, 62702, 63806, 65123, 66452, 67635, 69150, 70072, 71342, 72778, 73789, 75088, 76315,
        76851, 78471, 79545, 80174, 81844, 82503, 83996, 84436, 85334, 87124, 87419, 87752, 89732, 90800,
        91269, 92908, 93759, 95524, 96791, 97716, 98768, 100803, 101573, 103516, 104766, 106491, 108431,
        109935, 111712, 114047, 116103, 119133, 120206, 123092, 126748, 127577, 130658, 134774, 138787,
        140616, 146078, 150036, 155353, 160147, 164755, 171583, 179088, 184342, 193955, 200126, 214462,
        223555, 233299, 252957, 280276, 311963
    ];

    private static (int ResultSize, List<uint> Asked) Search(long target)
    {
        var asked = new List<uint>();
        var result = ImageConversion.FitQuality(quality =>
        {
            Assert.InRange(quality, 12u, 95u);
            asked.Add(quality);
            return new byte[Sizes[quality - 12]];
        }, target);
        return (result.Length, asked);
    }

    [Fact]
    public void ItEndsOnTheLargestResultThatFitsInFewerTriesThanHalving()
    {
        int tries = 0, targets = 0;
        for (int percent = 14; percent <= 98; percent += 3)
        {
            long target = (long)Sizes[^1] * percent / 100;
            var (size, asked) = Search(target);

            Assert.Equal(Sizes.Last(s => s <= target), size);
            Assert.Equal(asked.Count, asked.Distinct().Count()); // no quality is encoded twice
            tries += asked.Count;
            targets++;
        }

        // halving the range of 84 qualities needs six or seven encodes every time
        Assert.True(tries <= targets * 5, $"{tries} encodes for {targets} targets");
    }

    [Fact]
    public void WhenNothingFitsTheSmallestResultIsReturned()
    {
        var (size, asked) = Search(1000);

        Assert.Equal(Sizes[0], size);
        Assert.InRange(asked.Count, 1, 4);
    }

    [Fact]
    public void WhenEverythingFitsTheTopQualityIsReturned()
    {
        var (size, asked) = Search(10_000_000);

        Assert.Equal(Sizes[^1], size);
        Assert.InRange(asked.Count, 1, 3);
    }

    /// <summary>
    /// Lossless formats reach a size by shrinking the picture. A file of 4 MB at full size here
    /// shrinks with the scale to the given power — photos measure between 1.6 and 2.1.
    /// </summary>
    [Theory]
    [InlineData(1.6)]
    [InlineData(1.8)]
    [InlineData(2.0)]
    [InlineData(2.1)]
    public void ALosslessPictureIsShrunkToJustFit(double power)
    {
        const int fullSize = 4_000_000;
        foreach (double share in new[] { 0.9, 0.6, 0.3, 0.1, 0.02 })
        {
            long target = (long)(fullSize * share);
            var scales = new List<double>();
            var result = ImageConversion.FitScale(new byte[fullSize], scale =>
            {
                Assert.InRange(scale, 0.05, 1.0);
                scales.Add(scale);
                return new byte[(int)(fullSize * Math.Pow(scale, power))];
            }, target);

            Assert.InRange(result.Length, (long)(target * 0.9), target);
            Assert.InRange(scales.Count, 1, 2);
        }
    }

    [Fact]
    public void APictureThatFitsAlreadyIsNotResized()
    {
        var full = new byte[1000];
        Assert.Same(full, ImageConversion.FitScale(full, _ => throw new InvalidOperationException("no resize expected"), 1000));
    }
}
