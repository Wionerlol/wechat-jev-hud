using System.Text.Json;
using WeChatJevHud.Overlay;
using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class HudTraceTests
{
    [Fact]
    public void TraceExplainsScheduleApplyAndRetirementWithoutSourceText()
    {
        var events = new List<HudTrace>();
        var hud = new HudLifecycle(trace: events.Add);
        var message = LifecycleTests.Message() with { RawText = "DO_NOT_LOG_SOURCE", NormalizedText = "DO_NOT_LOG_SOURCE" };
        hud.Observe(1, [LifecycleTests.Visible()], true, false);
        Assert.False(hud.Schedule(message, JevStatus.QueueFull));
        Assert.True(hud.Schedule(message, JevStatus.Queued));
        Assert.False(hud.Schedule(message, JevStatus.Queued));
        Assert.False(hud.Apply(LifecycleTests.Result(epoch: 2)));
        Assert.True(hud.Apply(LifecycleTests.Result()));
        hud.Observe(1, [], true, false);
        Assert.False(hud.Apply(LifecycleTests.Result()));
        hud.Observe(1, [], true, false);
        Assert.False(hud.Apply(LifecycleTests.Result()));
        Assert.Contains(events, e => e.Stage == "hud_schedule" && Reason(e) == "jev_not_queued");
        Assert.Contains(events, e => e.Stage == "hud_schedule" && Reason(e) == "already_used");
        Assert.Contains(events, e => e.Stage == "hud_apply" && Reason(e) == "wrong_epoch");
        Assert.Contains(events, e => e.Stage == "hud_apply" && Reason(e) == "success");
        Assert.Contains(events, e => e.Stage == "hud_apply" && Reason(e) == "target_not_visible");
        Assert.Contains(events, e => e.Stage == "hud_apply" && Reason(e) == "item_missing");
        Assert.Contains(events, e => e.Stage == "hud_lifecycle" && !JsonSerializer.SerializeToElement(e.Data).GetProperty("exists").GetBoolean());
        Assert.DoesNotContain("DO_NOT_LOG_SOURCE", JsonSerializer.Serialize(events));
    }

    [Fact]
    public void LayoutTracingPreservesOutputAndRecordsDroppedLimit()
    {
        var cards = Enumerable.Range(1, 4).Select(i => new HudCard(new(1, i.ToString()), new(50, i * 100, 100, 40), HudPresentationModel.Pending, i)).ToArray();
        var engine = new OverlayLayoutEngine();
        var events = new List<HudTrace>();
        var expected = engine.Layout(cards, new(0, 0, 1000, 1000), new(96, 96), cards.Select(c => c.Bubble).ToArray());
        var actual = engine.Layout(cards, new(0, 0, 1000, 1000), new(96, 96), cards.Select(c => c.Bubble).ToArray(), events.Add);
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(4, events.Count(e => e.Stage == "hud_layout_attempt"));
        Assert.Contains(events, e => e.Stage == "hud_layout_attempt" && Reason(e) == "card_limit");
        Assert.Equal(actual.Length, events.Count(e => e.Stage == "hud_layout_output"));
    }
    private static string? Reason(HudTrace e) => JsonSerializer.SerializeToElement(e.Data).GetProperty("reason").GetString();
}
