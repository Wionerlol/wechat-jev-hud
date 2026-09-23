using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Observer.Tests;

public sealed class SemanticEdgeGuardTests
{
    [Theory]
    [InlineData(96, 5, true, 6)]
    [InlineData(96, 6, false, 6)]
    [InlineData(144, 8, true, 9)]
    [InlineData(144, 9, false, 9)]
    [InlineData(192, 11, true, 12)]
    [InlineData(192, 12, false, 12)]
    public void Six_dips_scale_to_capture_pixels_on_both_edges(uint dpi, int distance, bool excluded, int pixels)
    {
        var roi = new CapturePixelRect(377, 120, 741, 748);
        foreach (var y in new[] { roi.Y + distance, roi.Bottom - distance - 54 })
        {
            var result = SemanticEdgeGuard.Evaluate(new(515, y, 496, 54), roi, dpi);
            Assert.Equal(pixels, result.GuardPixels);
            Assert.Equal(excluded, result.Excluded);
        }
    }

    [Fact]
    public void Real_paired_geometry_rejects_fragment_but_allows_full_at_144_dpi()
    {
        var roi = new CapturePixelRect(377, 120, 741, 748);
        Assert.True(SemanticEdgeGuard.Evaluate(new(515, 840, 496, 26), roi, 144).Excluded);
        Assert.False(SemanticEdgeGuard.Evaluate(new(515, 744, 496, 111), roi, 144).Excluded);
    }
}
