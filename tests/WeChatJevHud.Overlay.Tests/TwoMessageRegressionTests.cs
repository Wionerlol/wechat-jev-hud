using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Overlay;
using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class TwoMessageRegressionTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void RealConstrainedAdjacentWidthsAlwaysRepresentNewestInsteadOfStaleOlder(uint dpi)
    {
        CapturePixelRect Scale(CapturePixelRect r) => new((int)Math.Round(r.X * dpi / 96d), (int)Math.Round(r.Y * dpi / 96d),
            (int)Math.Round(r.Width * dpi / 96d), (int)Math.Round(r.Height * dpi / 96d));
        var older = Scale(new(316, 318, 127, 36));
        var newer = Scale(new(316, 374, 113, 36));
        var hud = new HudLifecycle();
        hud.Observe(1, [LifecycleTests.Visible("m31") with { BubbleRect = older }, LifecycleTests.Visible("m32") with { BubbleRect = newer }], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("m31"), JevStatus.Queued));
        Assert.True(hud.Apply(LifecycleTests.Result("m31")));
        Assert.True(hud.Schedule(LifecycleTests.Message("m32"), JevStatus.Queued));
        Assert.True(hud.Apply(LifecycleTests.Result("m32")));
        var result = new OverlayLayoutEngine().Layout(hud.Cards, Scale(new(251, 80, 707, 344)), new(dpi, dpi), [older, newer]);
        Assert.Equal("m32", result.Items[0].Key.MessageId);
        Assert.Equal(2, result.Items.Length);
        Assert.Equal(2, result.Anchors.Length);
        Assert.All(result.Items, card => Assert.False(card.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(older, new(dpi, dpi)))));
        Assert.All(result.Items, card => Assert.False(card.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(newer, new(dpi, dpi)))));
    }
}
