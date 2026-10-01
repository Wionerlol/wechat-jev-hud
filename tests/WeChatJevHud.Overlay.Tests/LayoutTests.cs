using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(96, 150)]
    [InlineData(144, 100)]
    public void CapturePixelsBecomeHostLocalDipsAndNegativeDesktopCoordinatesRemainPhysical(uint dpi, double expected)
    {
        var rect = OverlayCoordinateMapper.ToLocal(new CapturePixelRect(150, 150, 300, 60), new DpiSnapshot(dpi, dpi));
        Assert.Equal(expected, rect.X);
        Assert.Equal(expected, rect.Y);
        var desktop = OverlayCoordinateMapper.ToDesktop(new DesktopPixelRect(-2000, -100, 1000, 800), new(150, 150, 300, 60));
        Assert.Equal(-1850, desktop.X);
        Assert.Equal(50, desktop.Y);
    }

    [Fact]
    public void RightRailAvoidsAllBubblesAndMarkersAndIsDeterministic()
    {
        var cards = Enumerable.Range(1, 5).Select(i => new HudCard(new(1, "m" + i), new(50, i * 56, 100, 36),
            new JudgmentComposer().Compose(LifecycleTests.Result())!, i)).ToArray();
        var obstacles = cards.Select(c => c.Bubble).Append(new(600, 480, 160, 36)).ToArray();
        var engine = new OverlayLayoutEngine();
        var result = engine.Layout(cards, new(0, 0, 800, 650), new(96, 96), obstacles);
        var again = engine.Layout(cards, new(0, 0, 800, 650), new(96, 96), obstacles);
        Assert.Equal(result.Items.ToArray(), again.Items.ToArray());
        Assert.Equal(result.Anchors.ToArray(), again.Anchors.ToArray());
        Assert.Equal(result.Overflow, again.Overflow);
        Assert.Equal("m5", result.Items[0].Key.MessageId);
        var bounds = result.Items.Select(i => i.Bounds).Concat(result.Anchors.Select(a => a.Bounds))
            .Concat(result.Overflow is { } indicator ? [indicator.Bounds] : []).ToArray();
        Assert.All(bounds, r => Assert.True(new LocalDipRect(0, 0, 800, 650).Contains(r)));
        Assert.All(bounds, r => Assert.DoesNotContain(obstacles, o => r.Intersects(OverlayCoordinateMapper.ToLocal(o, new(96, 96)))));
        for (var i = 0; i < bounds.Length; i++) for (var j = i + 1; j < bounds.Length; j++)
                Assert.False(bounds[i].Intersects(bounds[j]));
    }

    [Fact]
    public void CrossDpiKeepsRailAndAnchorsInTheSameLocalDipSpace()
    {
        var engine = new OverlayLayoutEngine();
        HudRailLayout? original = null;
        var presentation = new JudgmentComposer().Compose(LifecycleTests.Result())!;
        foreach (var dpi in new uint[] { 144, 96, 144 })
        {
            var scale = dpi / 96d;
            var card = new HudCard(new(1, "a"), new((int)(50 * scale), (int)(100 * scale), (int)(100 * scale), (int)(36 * scale)),
                presentation, 1);
            var result = engine.Layout([card], new(0, 0, (int)(800 * scale), (int)(650 * scale)), new(dpi, dpi), [card.Bubble]);
            Assert.Equal(154, Assert.Single(result.Anchors).Bounds.X);
            Assert.Equal(HudPresentationLevel.Expanded, Assert.Single(result.Items).Level);
            if (original is not null)
            {
                Assert.Equal(original.Items.ToArray(), result.Items.ToArray());
                Assert.Equal(original.Anchors.ToArray(), result.Anchors.ToArray());
            }
            original = result;
        }
    }

    [Fact]
    public void SmallerExpandedWidthIsUsedBeforeCompactAndOlderItemsDegradeFirst()
    {
        var cards = Enumerable.Range(1, 5).Select(i => new HudCard(new(1, "m" + i), new(10, 20 + i * 40, 40, 36),
            new JudgmentComposer().Compose(LifecycleTests.Result())!, i)).ToArray();
        var result = new OverlayLayoutEngine().Layout(cards, new(0, 0, 320, 400), new(96, 96), cards.Select(c => c.Bubble).ToArray());
        Assert.Equal(HudPresentationLevel.Expanded, result.Items[0].Level);
        Assert.Equal("m5", result.Items[0].Key.MessageId);
        Assert.Equal(220, result.Items[0].Bounds.Width);
        Assert.Equal(4, result.Overflow!.Count);
    }

    [Fact]
    public void ConfiguredCompactCapacityAndEmptySceneAreExplicit()
    {
        var engine = new OverlayLayoutEngine(policy: new(new(MaxCompactItems: 1)));
        var cards = Enumerable.Range(1, 5).Select(i => new HudCard(new(1, "m" + i), new(50, i * 56, 50, 36), HudPresentationModel.Pending, i)).ToArray();
        var result = engine.Layout(cards, new(0, 0, 800, 650), new(96, 96), cards.Select(c => c.Bubble).ToArray());
        Assert.Equal(2, result.Items.Length);
        Assert.Equal(3, result.Overflow!.Count);
        Assert.Equal(HudRailLayout.Empty, engine.Layout([], new(0, 0, 800, 650), new(96, 96), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudPresentationPolicy(new(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayLayoutEngine(new(RailWidth: double.NaN)));
    }
}
