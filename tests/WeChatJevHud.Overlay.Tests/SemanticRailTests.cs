using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class SemanticRailTests
{
    private static HudCard Ready(int number, int y = 100) => new(new(1, "m" + number),
        new(50, y, 70, 36), new JudgmentComposer().Compose(LifecycleTests.Result("m" + number))!, number);
    private static HudRailLayout Layout(params HudCard[] items) => new OverlayLayoutEngine().Layout(items,
        new(0, 0, 800, 650), new(96, 96), items.Select(c => c.Bubble).ToArray());

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 1, 0)]
    [InlineData(3, 2, 0)]
    [InlineData(5, 2, 2)]
    public void NewestExpandedPriorCompactAndOverflowRemainMessageKeyed(int count, int compact, int overflow)
    {
        var items = Enumerable.Range(1, count).Select(i => Ready(i, 60 + i * 56)).ToArray();
        var scene = Layout(items);
        Assert.Equal(items[^1].Key, scene.LatestKey);
        Assert.Equal(HudPresentationLevel.Expanded, scene.Items[0].Level);
        Assert.Equal(items[^1].Key, scene.Items[0].Key);
        Assert.Equal(compact, scene.Items.Count(i => i.Level == HudPresentationLevel.Compact));
        Assert.Equal(overflow, scene.Overflow?.Count ?? 0);
        Assert.Equal(count, scene.Anchors.Length);
        Assert.Equal(count, scene.Items.Length + (scene.Overflow?.Count ?? 0));
        Assert.Equal(8, scene.Items[0].Presentation.Details.Sum(g => g.Rows.Length));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void RealShortTailNoLongerNeedsFullCardBesideIt(uint dpi)
    {
        CapturePixelRect Scale(CapturePixelRect r) => new((int)(r.X * dpi / 96d), (int)(r.Y * dpi / 96d),
            (int)(r.Width * dpi / 96d), (int)(r.Height * dpi / 96d));
        var older = Ready(19) with { Bubble = Scale(new(316, 482, 225, 36)) };
        var latest = Ready(20) with { Bubble = Scale(new(316, 538, 57, 36)) };
        var scene = new OverlayLayoutEngine().Layout([older, latest], Scale(new(251, 80, 649, 508)), new(dpi, dpi), [older.Bubble, latest.Bubble]);
        Assert.Equal(latest.Key, scene.Items[0].Key);
        Assert.Equal(HudPresentationLevel.Expanded, scene.Items[0].Level);
        Assert.Contains(scene.Items, i => i.Key == older.Key && i.Level == HudPresentationLevel.Compact);
        Assert.Equal(2, scene.Anchors.Length);
        Assert.All(scene.Items, i => Assert.False(i.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(latest.Bubble, new(dpi, dpi)))));
        Assert.All(scene.Items, i => Assert.False(i.Bounds.Intersects(OverlayCoordinateMapper.ToLocal(older.Bubble, new(dpi, dpi)))));
    }

    [Fact]
    public void NewReadyImmediatelyReplacesOldExpandedEvenWhenResultsArriveOutOfOrder()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [LifecycleTests.Visible("m31"), LifecycleTests.Visible("m32", 156)], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("m31"), TypeSafe.JevStatus.Queued));
        Assert.True(hud.Apply(LifecycleTests.Result("m31")));
        Assert.True(hud.Schedule(LifecycleTests.Message("m32"), TypeSafe.JevStatus.Queued));
        Assert.Equal("m32", Layout(hud.Cards.ToArray()).Items[0].Key.MessageId);
        var newerResult = LifecycleTests.Result("m32");
        newerResult = newerResult with
        {
            Evaluation = newerResult.Evaluation with
            {
                Judgments = newerResult.Evaluation.Judgments! with
                {
                    SpeechAct = new("request", new Dictionary<string, double> { ["request"] = 1 }.ToImmutableDictionary(), .99),
                    ExpectsResponse = new(.56)
                }
            }
        };
        Assert.True(hud.Apply(newerResult));
        Assert.True(hud.Apply(LifecycleTests.Result("m31")));
        var scene = Layout(hud.Cards.ToArray());
        Assert.Equal("m32", scene.Items[0].Key.MessageId);
        Assert.False(scene.Items[0].Presentation.Rows.IsEmpty);
        Assert.Equal(new HudRow("请求", "100%"), scene.Items[0].Presentation.Rows[0]);
        Assert.Equal(new HudRow("期待回应", "56%"), scene.Items[0].Presentation.Rows[1]);
        Assert.Equal("m31", scene.Items[1].Key.MessageId);
        Assert.Equal(HudPresentationLevel.Compact, scene.Items[1].Level);
    }

    [Fact]
    public void GeometryChangesAnchorsNotChronologicalRailOrder()
    {
        var first = Layout(Ready(1, 200), Ready(2, 100));
        var moved = Layout(Ready(1, 160), Ready(2, 60));
        Assert.Equal(first.Items.Select(i => i.Key), moved.Items.Select(i => i.Key));
        foreach (var anchor in first.Anchors)
            Assert.Equal(anchor.Bounds.Y - 40, moved.Anchors.Single(a => a.Key == anchor.Key).Bounds.Y);
    }

    [Fact]
    public void NarrowPaneDegradesToCompactThenIndicatorWithoutShowingOlderAsLatest()
    {
        var items = new[] { Ready(1, 20), Ready(2, 76) };
        var compact = new OverlayLayoutEngine().Layout(items, new(0, 0, 230, 260), new(96, 96), items.Select(i => i.Bubble).ToArray());
        Assert.Equal(HudPresentationLevel.Compact, compact.Items[0].Level);
        Assert.Equal(items[1].Key, compact.Items[0].Key);
        var tiny = new OverlayLayoutEngine().Layout(items, new(0, 0, 180, 160), new(96, 96), items.Select(i => i.Bubble).ToArray());
        Assert.Empty(tiny.Items);
        Assert.NotEmpty(tiny.Anchors);
        Assert.Equal(items[1].Key, tiny.LatestKey);
        Assert.Equal(2, tiny.Overflow!.Count);
        Assert.True(tiny.Overflow.IncludesLatest);
    }

    [Fact]
    public void SourceCoveringAvailableRailNeverCausesUnsafePlacement()
    {
        var item = Ready(1) with { Bubble = new(0, 0, 180, 100) };
        var scene = new OverlayLayoutEngine().Layout([item], new(0, 0, 180, 100), new(96, 96), [item.Bubble]);
        Assert.True(scene.IsEmpty);
        Assert.Equal(item.Key, scene.LatestKey);
        Assert.Equal(HudRailDensity.AnchorsOnly, scene.Density);
    }

    [Fact]
    public void ExpandedUsesAllFiveNoulAndBothScoreValuesWithoutConfidenceSubstitution()
    {
        var presentation = new JudgmentComposer().Compose(LifecycleTests.Result())!;
        var rows = presentation.Details.SelectMany(g => g.Rows).ToArray();
        Assert.Equal(new[] { "主要", "对话", "强度" }, presentation.Details.Select(g => g.Label));
        Assert.Equal(new HudRow("询问", "88%"), rows[0]); // Choice confidence is .4, not .88.
        Assert.Equal(new HudRow("期待回应", "91%"), rows[1]);
        Assert.Equal(new HudRow("依赖前文", "79%"), rows[2]);
        Assert.Equal(new HudRow("直接请求", "10%"), rows[3]);
        Assert.Equal(new HudRow("异议/纠正", "20%"), rows[4]);
        Assert.Equal(new HudRow("时间/计划", "30%"), rows[5]);
        Assert.Equal(new HudRow("紧迫度", "1.4/3"), rows[6]);
        Assert.Equal(new HudRow("情感表达", "1.4/3"), rows[7]); // Score confidence is .2.
        var invalid = LifecycleTests.Result();
        invalid = invalid with
        {
            Evaluation = invalid.Evaluation with
            {
                Judgments = invalid.Evaluation.Judgments! with
                { EmotionalIntensity = invalid.Evaluation.Judgments!.EmotionalIntensity with { Score = double.NaN } }
            }
        };
        Assert.Null(new JudgmentComposer().Compose(invalid));
    }

    [Fact]
    public void RailNeverResurrectsRetiredHistoryAndTemporaryHidingDoesNotRetire()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [LifecycleTests.Visible("a")], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message(), TypeSafe.JevStatus.Queued));
        Assert.True(hud.Apply(LifecycleTests.Result()));
        Assert.Single(Layout(hud.Cards.ToArray()).Items);
        hud.Observe(1, [], true, true);
        Assert.True(Layout(hud.Cards.ToArray()).IsEmpty);
        hud.Observe(1, [LifecycleTests.Visible("a")], true, false);
        Assert.Single(Layout(hud.Cards.ToArray()).Items);
        hud.Observe(1, [], true, false);
        hud.Observe(1, [], true, false);
        hud.Observe(1, [LifecycleTests.Visible("a")], true, false);
        Assert.False(hud.Schedule(LifecycleTests.Message(), TypeSafe.JevStatus.Queued));
        Assert.False(hud.Apply(LifecycleTests.Result()));
        Assert.True(Layout(hud.Cards.ToArray()).IsEmpty);
    }
}
