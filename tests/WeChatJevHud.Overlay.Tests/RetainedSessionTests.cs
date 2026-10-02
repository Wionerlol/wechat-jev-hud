using WeChatJevHud.Overlay;
using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public class RetainedSessionTests
{
    private static HudRailLayout Layout(HudLifecycle hud) => new OverlayLayoutEngine().Layout(hud.Cards,
        new(0, 0, 900, 700), new(96, 96), hud.Cards.Where(c => c.CurrentBubbleRect is not null).Select(c => c.Bubble).ToArray());

    private static HudLifecycle ThreeItems()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, Enumerable.Range(1, 3).Select(i => LifecycleTests.Visible("m" + i, i * 80)).ToArray(), true, false);
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(hud.Schedule(LifecycleTests.Message("m" + i), JevStatus.Queued));
            Assert.True(hud.Apply(LifecycleTests.Result("m" + i)));
        }
        return hud;
    }

    [Fact]
    public void PushedOrScrolledOutItemRetainsResultAndReattachesWithoutScheduling()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [LifecycleTests.Visible("m1"), LifecycleTests.Visible("m2", 180)], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("m1"), JevStatus.Queued));
        Assert.True(hud.Apply(LifecycleTests.Result("m1")));
        Assert.True(hud.Schedule(LifecycleTests.Message("m2"), JevStatus.Queued));
        var original = hud.Cards.Single(c => c.Key.MessageId == "m1");
        for (var i = 0; i < 3; i++) hud.Observe(1, [LifecycleTests.Visible("m2")], true, false);
        Assert.Equal(original.Presentation, hud.Cards.Single(c => c.Key.MessageId == "m1").Presentation);
        hud.Observe(1, [LifecycleTests.Visible("m1", 60), LifecycleTests.Visible("m2", 140)], true, false);
        Assert.Equal(original.Sequence, hud.Cards.Single(c => c.Key.MessageId == "m1").Sequence);
        Assert.False(hud.Schedule(LifecycleTests.Message("m1"), JevStatus.Queued));
    }

    [Fact]
    public void PushOutPreservesPresentationOrdinalAndOnlyCurrentAnchors()
    {
        var hud = ThreeItems();
        var original = hud.TrackedItems[0];
        hud.Observe(1, [LifecycleTests.Visible("m2"), LifecycleTests.Visible("m3", 180)], true, false);
        var first = hud.TrackedItems[0];
        Assert.Equal(original.Presentation, first.Presentation);
        Assert.Equal(original.LastKnownBubbleRect, first.LastKnownBubbleRect);
        Assert.Equal(1, first.DisplayOrdinal);
        Assert.Equal(HudVisibilityState.OffscreenRetained, first.Visibility);
        Assert.Null(first.CurrentBubbleRect);
        Assert.Equal(new[] { 3, 2 }, Layout(hud).Anchors.Select(a => a.Ordinal));
        Assert.Equal(new[] { 3, 2, 1 }, Layout(hud).Items.Select(i => i.Ordinal));
    }

    [Fact]
    public void ScrollBelowThenExactIdReturnsRestoresOriginalAnchorAndResult()
    {
        var hud = ThreeItems();
        var original = hud.TrackedItems[2];
        hud.Observe(1, [LifecycleTests.Visible("m1")], true, false);
        var scene = Layout(hud);
        Assert.Equal("m3", scene.LatestKey!.Value.MessageId);
        Assert.Equal(HudVisibilityState.OffscreenRetained, scene.Items[0].Visibility);
        Assert.Equal(3, scene.Items[0].Ordinal);
        Assert.Single(scene.Anchors);
        hud.Observe(1, [LifecycleTests.Visible("m2"), LifecycleTests.Visible("m3", 180)], true, false);
        Assert.Equal(new[] { 3, 2 }, Layout(hud).Anchors.Select(a => a.Ordinal));
        Assert.Equal(original.Presentation, hud.TrackedItems[2].Presentation);
        Assert.False(hud.Schedule(LifecycleTests.Message("m3"), JevStatus.Queued));
    }

    [Fact]
    public void DifferentIdNeverInheritsOffscreenSemanticResult()
    {
        var hud = ThreeItems();
        hud.Observe(1, [LifecycleTests.Visible("m99", 240)], true, false);
        Assert.All(hud.TrackedItems, c => Assert.Equal(HudVisibilityState.OffscreenRetained, c.Visibility));
        Assert.Empty(Layout(hud).Anchors);
        Assert.DoesNotContain(hud.TrackedItems, c => c.Key.MessageId == "m99");
        Assert.Equal(3, hud.TrackedItems.Length);
    }

    [Fact]
    public void PendingResultMayCompleteWhileOffscreenWithoutLosingAssociation()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [LifecycleTests.Visible("a")], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message(), JevStatus.Queued));
        hud.Observe(1, [], true, false);
        Assert.True(hud.Apply(LifecycleTests.Result()));
        var scene = Layout(hud);
        Assert.Empty(scene.Anchors);
        Assert.Equal("Jev", Assert.Single(scene.Items).Presentation.Heading);
        Assert.Equal(1, scene.Items[0].Ordinal);
    }

    [Fact]
    public void CapacityEvictsOldestWithoutRenumberingOrReattachment()
    {
        var hud = new HudLifecycle(maxTrackedSemanticItems: 2);
        for (var i = 1; i <= 4; i++)
        {
            hud.Observe(1, [LifecycleTests.Visible("m" + i)], true, false);
            Assert.True(hud.Schedule(LifecycleTests.Message("m" + i), JevStatus.Queued));
        }
        Assert.Equal(new[] { 3, 4 }, hud.TrackedItems.Select(c => c.DisplayOrdinal));
        Assert.False(hud.Apply(LifecycleTests.Result("m1")));
        hud.Observe(1, [LifecycleTests.Visible("m1")], true, false);
        Assert.Empty(Layout(hud).Anchors);
        Assert.False(hud.Schedule(LifecycleTests.Message("m1"), JevStatus.Queued));
    }

    [Fact]
    public void EpochChangeAndExplicitResetClearItemsAndResetOrdinal()
    {
        var hud = ThreeItems();
        hud.Observe(2, [LifecycleTests.Visible("new")], true, true);
        Assert.Empty(hud.TrackedItems);
        Assert.False(hud.Apply(LifecycleTests.Result("m3")));
        hud.Observe(2, [LifecycleTests.Visible("new")], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("new") with { ConversationEpochId = 2 }, JevStatus.Queued));
        Assert.Equal(1, Assert.Single(hud.TrackedItems).DisplayOrdinal);
        hud.Clear();
        Assert.Empty(hud.TrackedItems);
        hud.Observe(2, [LifecycleTests.Visible("reset")], true, false);
        Assert.True(hud.Schedule(LifecycleTests.Message("reset") with { ConversationEpochId = 2 }, JevStatus.Queued));
        Assert.Equal(1, Assert.Single(hud.TrackedItems).DisplayOrdinal);
    }

    [Fact]
    public void OverflowCountsRetainedSemanticItemsNotAnchors()
    {
        var hud = new HudLifecycle();
        for (var i = 1; i <= 25; i++)
        {
            hud.Observe(1, [LifecycleTests.Visible("m" + i)], true, false);
            Assert.True(hud.Schedule(LifecycleTests.Message("m" + i), JevStatus.Queued));
            Assert.True(hud.Apply(LifecycleTests.Result("m" + i)));
        }
        hud.Observe(1, [], true, false);
        var scene = Layout(hud);
        Assert.Equal(new[] { 25, 24, 23 }, scene.Items.Select(i => i.Ordinal));
        Assert.Equal(22, scene.Overflow!.Count);
        Assert.Empty(scene.Anchors);
        Assert.Equal(25, scene.ActiveCount);
    }

    [Fact]
    public void InvalidCapacityRejectedAndUnavailableResultRetainsIdentityOnly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudLifecycle(maxTrackedSemanticItems: 0));
        var hud = ThreeItems();
        Assert.False(hud.Apply(LifecycleTests.Result("m3", status: JevStatus.Timeout)));
        Assert.Equal(3, hud.TrackedItems.Length);
        Assert.True(hud.TrackedItems[2].IsUnavailable);
        Assert.DoesNotContain(hud.Cards, c => c.Key.MessageId == "m3");
    }

    [Fact]
    public void LayoutUsesChronologicalOrdinalNotNewestFirstArrayIndex()
    {
        var presentation = new JudgmentComposer().Compose(LifecycleTests.Result())!;
        HudCard[] cards = Enumerable.Range(1, 5).Select(i => new HudCard(new(1, "m" + i),
            new(20, 70 * i, 80, 40), presentation, i)).ToArray();
        var layout = new OverlayLayoutEngine().Layout(cards, new(0, 0, 900, 700), new(96, 96), cards.Select(c => c.Bubble).ToArray());
        Assert.Equal(new[] { 5, 4, 3 }, layout.Items.Select(i => i.Ordinal));
        Assert.Equal(5, layout.Anchors.Single(a => a.Key.MessageId == "m5").Ordinal);
    }
}
