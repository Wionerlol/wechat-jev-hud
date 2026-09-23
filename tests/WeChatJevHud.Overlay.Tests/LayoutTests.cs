using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public class LayoutTests
{
    [Fact]
    public void RightPlacementAvoidsCardsAndBubblesAndIsDeterministic()
    {
        HudCard[] cards = [new(new(1,"a"), new(50,50,100,40), HudPresentationModel.Pending, 2),
            new(new(1,"b"), new(50,80,100,40), HudPresentationModel.Pending, 1)];
        var engine = new OverlayLayoutEngine();
        var result = engine.Layout(cards, new(0, 0, 800, 600), new(96, 96), cards.Select(c => c.Bubble).ToArray());
        Assert.Equal(2, result.Length);
        Assert.Equal(160, result[0].Bounds.X);
        Assert.False(result[0].Bounds.Intersects(result[1].Bounds));
        Assert.Equal(result.ToArray(), engine.Layout(cards, new(0, 0, 800, 600), new(96, 96), cards.Select(c => c.Bubble).ToArray()).ToArray());
    }

    [Fact]
    public void OverflowUsesNearbyLeftAndImpossibleAreaHides()
    {
        HudCard[] cards = [new(new(1, "a"), new(600, 50, 100, 40), HudPresentationModel.Pending, 1)];
        var engine = new OverlayLayoutEngine();
        Assert.Equal(380, Assert.Single(engine.Layout(cards, new(0, 0, 720, 600), new(96, 96), [cards[0].Bubble])).Bounds.X);
        Assert.Empty(engine.Layout(cards, new(600, 50, 100, 40), new(96, 96), [cards[0].Bubble]));
    }

    [Theory]
    [InlineData(96, 150)]
    [InlineData(144, 100)]
    public void CapturePixelsBecomeHostLocalDips(uint dpi, double expected)
    {
        var rect = OverlayCoordinateMapper.ToLocal(new CapturePixelRect(150, 150, 300, 60), new DpiSnapshot(dpi, dpi));
        Assert.Equal(expected, rect.X);
        Assert.Equal(expected, rect.Y);
        var desktop = OverlayCoordinateMapper.ToDesktop(new DesktopPixelRect(-2000, -100, 1000, 800), new(150, 150, 300, 60));
        Assert.Equal(-1850, desktop.X);
        Assert.Equal(50, desktop.Y);
    }

    [Fact]
    public void CrossDpiKeepsTenDipGapAndNewestThreeCards()
    {
        var engine = new OverlayLayoutEngine();
        foreach (var dpi in new uint[] { 144, 96, 144 })
        {
            var scale = dpi / 96d;
            var cards = Enumerable.Range(1, 5).Select(i => new HudCard(new(1, i.ToString()),
                new(50, (int)(i * 100 * scale), 100, 40), HudPresentationModel.Pending, i)).ToArray();
            var result = engine.Layout(cards, new(0, 0, 1200, 1200), new(dpi, dpi), cards.Select(c => c.Bubble).ToArray());
            Assert.Equal(new[] { "5", "4", "3" }, result.Select(c => c.Key.MessageId));
            Assert.All(result, c => Assert.Equal(150 / scale + 10, c.Bounds.X, 6));
        }
    }

    [Fact]
    public void DoesNotCoverUnrelatedBubble()
    {
        var card = new HudCard(new(1, "a"), new(50, 50, 100, 40), HudPresentationModel.Pending, 1);
        var result = new OverlayLayoutEngine().Layout([card], new(0, 0, 400, 100), new(96, 96), [card.Bubble, new(160, 0, 240, 100)]);
        Assert.Empty(result);
    }
}
