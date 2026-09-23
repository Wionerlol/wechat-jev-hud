using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Observer.Tests;

public sealed class BubbleCompletenessTests
{
    [Fact]
    public void Actual_reference_bubble_caps_distinguish_full_from_clipped_at_two_pixel_gap()
    {
        var frame = PngFrameReader.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wechat-dark-layout-reference.png"));
        var detected = new BubbleDetectionPipeline(new DarkThemeChatRegionLocator(), new DarkThemeBubbleDetector()).Analyze(frame);
        var analyzer = new BubbleCompletenessAnalyzer();
        // Replay real pixels against simulated viewport boundaries; not a claim of a
        // newly captured private same-message pair or a real scrolling interaction.
        var controls = detected.Bubbles.Where(b => b.Bounds.Y > detected.ChatRegion.Bounds.Y + b.Bounds.Height &&
            b.Bounds.Bottom < detected.ChatRegion.Bounds.Bottom - b.Bounds.Height).ToArray();
        Assert.NotEmpty(controls);
        foreach (var bubble in controls)
        {
            var bottomRoi = new CapturePixelRect(detected.ChatRegion.Bounds.X, detected.ChatRegion.Bounds.Y,
                detected.ChatRegion.Bounds.Width, bubble.Bounds.Bottom + 2 - detected.ChatRegion.Bounds.Y);
            Assert.True(Assert.Single(analyzer.Analyze(frame, bottomRoi, [bubble])).IsFullyVisible,
                $"Full real cap rejected: {bubble.Bounds}");
            var fragment = bubble with { Bounds = bubble.Bounds with { Height = 15 } };
            var clippedRoi = bottomRoi with { Height = fragment.Bounds.Bottom + 2 - bottomRoi.Y };
            Assert.False(Assert.Single(analyzer.Analyze(frame, clippedRoi, [fragment])).IsFullyVisible);
        }
        Assert.Contains(controls, b => b.Bounds.Height <= 60);
    }

    [Theory]
    [InlineData(36, 74)]
    [InlineData(54, 111)]
    public void Complete_single_and_multiline_near_bottom_remain_complete(int singleHeight, int multilineHeight)
    {
        var roi = new CapturePixelRect(0, 120, 700, 421);
        foreach (var height in new[] { singleHeight, multilineHeight })
        {
            var interior = new CapturePixelRect(30, 250, 150, singleHeight);
            var bottom = new CapturePixelRect(300, 539 - height, 200, height);
            var frame = Shapes(roi, [(interior, 5), (bottom, 5)]);
            var results = new BubbleCompletenessAnalyzer().Analyze(frame, roi, [Bubble(interior), Bubble(bottom)]);
            Assert.True(results[1].IsFullyVisible);
            Assert.Equal(singleHeight, results[1].NominalFullBubbleHeight);
            Assert.True(results[1].BoundaryRisk);
            Assert.True(results[1].TopCapClosed);
            Assert.True(results[1].BottomCapClosed);
        }
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 15)]
    [InlineData(false, 60)]
    [InlineData(true, 60)]
    public void Boundary_fragment_is_partial_even_when_taller_than_a_single_line(bool top, int height)
    {
        var roi = new CapturePixelRect(0, 120, 700, 421);
        var interior = new CapturePixelRect(30, 250, 150, 54);
        var fragment = new CapturePixelRect(300, top ? 122 : 539 - height, 200, height);
        var frame = Shapes(roi, [(interior, 6), (fragment, 0)]);
        var results = new BubbleCompletenessAnalyzer().Analyze(frame, roi, [Bubble(interior), Bubble(fragment)]);
        Assert.False(results[1].IsFullyVisible);
        Assert.Equal(top ? 2 : roi.Height - height - 2, results[1].DistanceToTop);
        Assert.Equal(top ? roi.Height - height - 2 : 2, results[1].DistanceToBottom);
        Assert.Equal(height < 54 ? BubbleCompletenessReason.SuspiciouslyShortAtBoundary : BubbleCompletenessReason.ShapeClipped, results[1].Reason);
    }

    [Fact]
    public void Small_interior_bubbles_are_not_globally_filtered()
    {
        var roi = new CapturePixelRect(0, 20, 200, 180);
        var small = new CapturePixelRect(30, 100, 60, 15);
        var result = Assert.Single(new BubbleCompletenessAnalyzer().Analyze(Shapes(roi, [(small, 0)]), roi, [Bubble(small)]));
        Assert.True(result.IsFullyVisible);
        Assert.Equal(BubbleCompletenessReason.Interior, result.Reason);
    }

    private static DetectedBubble Bubble(CapturePixelRect rect) => new(rect, MessageSide.Self, .94);

    internal static CapturedFrame Shapes(CapturePixelRect roi, params (CapturePixelRect Rect, int Radius)[] shapes)
    {
        var width = roi.Right;
        var height = roi.Bottom + 100;
        var pixels = new byte[width * height * 4];
        foreach (var (rect, radius) in shapes)
            for (var y = rect.Y; y < rect.Bottom; y++)
                for (var x = rect.X; x < rect.Right; x++)
                {
                    var cx = Math.Clamp(x + .5, rect.X + radius, rect.Right - radius);
                    var cy = Math.Clamp(y + .5, rect.Y + radius, rect.Bottom - radius);
                    if (radius > 0 && Math.Pow(x + .5 - cx, 2) + Math.Pow(y + .5 - cy, 2) > radius * radius) continue;
                    var index = (y * width + x) * 4;
                    pixels[index] = pixels[index + 1] = pixels[index + 2] = 120;
                    pixels[index + 3] = 255;
                }
        return new(width, height, width * 4, pixels, new DesktopPixelRect(0, 0, width, height), CaptureMethod.RenderWindow, DateTimeOffset.UtcNow, TimeSpan.Zero);
    }
}
