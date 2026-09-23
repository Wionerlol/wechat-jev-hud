using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class SemanticRegionInspectorTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void Single_background_text_passes_but_embedded_panel_does_not(uint dpi)
    {
        var width = (int)(240 * dpi / 96); var height = (int)(100 * dpi / 96);
        var pixels = Enumerable.Repeat((byte)48, width * height * 4).ToArray();
        // Sparse independent glyph strokes, including multiple text lines.
        for (var y = 15; y < height - 15; y += 30)
            for (var x = 15; x < width - 15; x += 18)
                for (var dy = 0; dy < 10; dy++)
                    for (var dx = 0; dx < 3; dx++)
                        Set(x + dx, y + dy, 230);
        var frame = new CapturedFrame(width, height, width * 4, pixels, new(0, 0, width, height),
            CaptureMethod.RenderWindow, DateTimeOffset.UtcNow, TimeSpan.Zero, dpi);
        var crop = new ImageCrop(frame, new(0, 0, width, height));
        Assert.True(SemanticRegionInspector.Inspect(crop).Verified);
        for (var y = height / 2; y < height - 12; y++)
            for (var x = 12; x < width - 12; x++) Set(x, y, 80);
        Assert.False(SemanticRegionInspector.Inspect(crop).Verified);
        void Set(int x, int y, byte value)
        {
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255;
        }
    }

    [Fact]
    public void Tiny_or_ambiguous_input_fails_closed()
    {
        var frame = new CapturedFrame(8, 8, 32, new byte[256], new DesktopPixelRect(0, 0, 8, 8),
            CaptureMethod.RenderWindow, DateTimeOffset.UtcNow, TimeSpan.Zero);
        Assert.False(SemanticRegionInspector.Inspect(new(frame, new(0, 0, 8, 8))).Verified);
    }
}
