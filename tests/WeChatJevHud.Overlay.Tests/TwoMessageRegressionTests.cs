using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Overlay;
using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class TwoMessageRegressionTests
{
    // Real no-image geometry replay: 96 DPI, two adjacent Remote bubble widths 127/113.
    // The newer card at x=439 overlaps the older bubble ending at x=443 when shifted up.
    private static readonly CapturePixelRect Usable = new(251, 80, 707, 344);
    private static readonly CapturePixelRect Older = new(316, 318, 127, 36);
    private static readonly CapturePixelRect Newer = new(316, 374, 113, 36);

    private static JevAnalysisResult Result(string id, string choice, double expects) => LifecycleTests.Result(id) with
    {
        Evaluation = LifecycleTests.Result(id).Evaluation with
        {
            Judgments = LifecycleTests.Result(id).Evaluation.Judgments! with
            {
                ExpectsResponse = new(expects),
                SpeechAct = new(choice, new Dictionary<string, double> { [choice] = 1 }.ToImmutableDictionary(), .99)
            }
        }
    };

    [Fact]
    public void RealAdjacentWidthsNewestReadyMustNotLoseToOlderReady()
    {
        var hud = new HudLifecycle();
        var olderBeforeAppend = Older with { Y = Newer.Y };
        hud.Observe(1, [LifecycleTests.Visible("m31") with { BubbleRect = olderBeforeAppend }], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("m31") with { BubbleRect = olderBeforeAppend }, JevStatus.Queued));
        Assert.True(hud.Apply(Result("m31", "question", .96)));
        var engine = new OverlayLayoutEngine();
        Assert.Equal("m31", Assert.Single(engine.Layout(hud.Cards, Usable, new(96, 96), [olderBeforeAppend])).Key.MessageId);

        var visible = new[] { LifecycleTests.Visible("m31") with { BubbleRect = Older },
            LifecycleTests.Visible("m32") with { BubbleRect = Newer } };
        hud.Observe(1, visible, true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("m32") with { BubbleRect = Newer }, JevStatus.Queued));
        Assert.Contains(engine.Layout(hud.Cards, Usable, new(96, 96), [Older, Newer]), c => c.Key.MessageId == "m32");
        for (var i = 0; i < 3; i++) hud.Observe(1, visible, false, false);
        Assert.True(hud.Apply(Result("m32", "request", .56)));

        var scene = engine.Layout(hud.Cards, Usable, new(96, 96), [Older, Newer]);
        var newest = Assert.Single(scene.Where(c => c.Key.MessageId == "m32"));
        Assert.Equal(new HudRow("请求", "100%"), newest.Presentation.Rows[0]);
        Assert.Equal(new HudRow("期待回应", "56%"), newest.Presentation.Rows[1]);
        Assert.All(scene, c => Assert.True(OverlayCoordinateMapper.ToLocal(Usable, new(96, 96)).Contains(c.Bounds)));
        Assert.All(scene, c => Assert.False(c.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(Older, new(96, 96)))));
        Assert.All(scene, c => Assert.False(c.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(Newer, new(96, 96)))));
        Assert.Equal(scene.Length, scene.Select(c => c.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void NearbyHorizontalAlternativeIsScaleInvariantAndStillFavorsNewest(uint dpi)
    {
        CapturePixelRect Scale(CapturePixelRect r) => new((int)Math.Round(r.X * dpi / 96d), (int)Math.Round(r.Y * dpi / 96d),
            (int)Math.Round(r.Width * dpi / 96d), (int)Math.Round(r.Height * dpi / 96d));
        var composer = new JudgmentComposer();
        HudCard[] cards = [new(new(1, "m31"), Scale(Older), composer.Compose(Result("m31", "question", .96))!, 1),
            new(new(1, "m32"), Scale(Newer), composer.Compose(Result("m32", "request", .56))!, 2)];
        var engine = new OverlayLayoutEngine();
        var output = engine.Layout(cards, Scale(Usable), new(dpi, dpi), cards.Select(c => c.Bubble).ToArray());
        Assert.Equal("m32", Assert.Single(output).Key.MessageId);
        var originalRight = OverlayCoordinateMapper.ToLocal(Scale(Newer), new(dpi, dpi)).Right + 10;
        Assert.InRange(output[0].Bounds.X - originalRight, 0, 20);
        Assert.Equal(output.ToArray(), engine.Layout(cards, Scale(Usable), new(dpi, dpi), cards.Select(c => c.Bubble).ToArray()).ToArray());
    }

    [Fact]
    public void HorizontalAlternativeCannotJumpToDistantPartOfViewport()
    {
        var card = new HudCard(new(1, "m32"), Newer, new JudgmentComposer().Compose(Result("m32", "request", .56))!, 2);
        var distantObstacle = Older with { Width = 200 };
        Assert.Empty(new OverlayLayoutEngine().Layout([card], Usable, new(96, 96), [Newer, distantObstacle]));
    }
}
