namespace CentreStage.Core.Tests;

public class RasterMetricsTests
{
    [Fact]
    public void Psnr_IdenticalRasters_IsInfinite()
    {
        byte[] raster = [1, 2, 3, 250, 251, 252];
        Assert.Equal(double.PositiveInfinity, RasterMetrics.Psnr(raster, raster.ToArray()));
    }

    [Fact]
    public void Psnr_KnownMeanSquaredError_MatchesFormula()
    {
        // One channel differs by 10, the rest match: MSE = 100/6 over 6 samples.
        byte[] candidate = [0, 0, 0, 0, 0, 0];
        byte[] reference = [10, 0, 0, 0, 0, 0];
        var mse = 100.0 / 6.0;

        Assert.Equal(10.0 * Math.Log10(255.0 * 255.0 / mse), RasterMetrics.Psnr(candidate, reference),
            9);
    }

    [Fact]
    public void PsnrRegion_MatchesFullPsnrWhenRegionIsWholeImage()
    {
        var candidate = Pattern(4, 3, seed: 7);
        var reference = Pattern(4, 3, seed: 11);

        var whole = RasterMetrics.PsnrRegion(candidate, reference, 4, 3, 0, 0, 4, 3);

        Assert.Equal(RasterMetrics.Psnr(candidate, reference), whole, 12);
    }

    [Fact]
    public void PsnrRegion_OnlyReadsTheRegion()
    {
        // Only the top-left 1x1 differs; the rest of the candidate is untouched.
        var reference = new byte[4 * 2 * 3];
        var candidate = new byte[4 * 2 * 3];
        candidate[0] = 50;

        // A region that excludes the differing pixel is a perfect match...
        Assert.Equal(double.PositiveInfinity,
            RasterMetrics.PsnrRegion(candidate, reference, 4, 2, 1, 0, 3, 2));
        // ...and one that includes it is not.
        Assert.True(RasterMetrics.PsnrRegion(candidate, reference, 4, 2, 0, 0, 1, 1) < 20);
    }

    [Fact]
    public void PsnrRegion_UsesTopDownRows()
    {
        // Put the difference on the last row: a bottom-up read would land on row 0.
        var reference = new byte[4 * 3 * 3];
        var candidate = new byte[4 * 3 * 3];
        candidate[(2 * 4 + 1) * 3] = 200;

        var topRow = RasterMetrics.PsnrRegion(candidate, reference, 4, 3, 0, 0, 4, 1);
        var bottomRow = RasterMetrics.PsnrRegion(candidate, reference, 4, 3, 0, 2, 4, 1);

        Assert.Equal(double.PositiveInfinity, topRow);
        Assert.True(bottomRow < 20);
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, -1, 2, 2)]
    [InlineData(3, 0, 2, 2)]
    [InlineData(0, 3, 2, 2)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(0, 0, 5, 1)]
    public void PsnrRegion_RejectsRegionsOutsideTheImage(int x, int y, int width, int height)
    {
        var raster = new byte[4 * 4 * 3];

        Assert.Throws<ArgumentException>(() =>
            RasterMetrics.PsnrRegion(raster, raster, 4, 4, x, y, width, height));
    }

    [Fact]
    public void PsnrRegion_AcceptsRegionFillingTheLastPixel()
    {
        var raster = Pattern(4, 4, seed: 3);

        var value = RasterMetrics.PsnrRegion(raster, raster, 4, 4, 3, 3, 1, 1);

        Assert.Equal(double.PositiveInfinity, value);
    }

    [Fact]
    public void PsnrRegion_RejectsBufferThatDoesNotMatchDimensions()
    {
        var raster = new byte[4 * 4 * 3];

        Assert.Throws<ArgumentException>(() =>
            RasterMetrics.PsnrRegion(raster[..^3], raster[..^3], 4, 4, 0, 0, 1, 1));
        Assert.Throws<ArgumentException>(() =>
            RasterMetrics.PsnrRegion(raster, raster[..^3], 4, 4, 0, 0, 4, 4));
    }

    private static byte[] Pattern(int width, int height, int seed)
    {
        var raster = new byte[width * height * 3];
        var state = (uint)seed;
        for (var i = 0; i < raster.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            raster[i] = (byte)(state >> 24);
        }

        return raster;
    }
}