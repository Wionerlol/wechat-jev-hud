using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Ocr;
using WeChatJevHud.Overlay;
using WeChatJevHud.TypeSafe;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public class LifecycleTests
{
    internal static ObservedMessage Message(string id = "a", MessageSide side = MessageSide.Remote) =>
        new(id, 1, side, "synthetic", "synthetic", OcrTextStatus.Recognized, null, new(50, 100, 100, 40),
            DateTimeOffset.UtcNow, MessageObservationKind.LiveNew, "visual", true, SemanticRegionVerified: true, OutsideSemanticEdgeGuard: true);
    internal static VisibleMessageSnapshot Visible(string id = "a", int y = 100) => new(id, MessageSide.Remote, new(50, y, 100, 40), "visual");
    internal static JevAnalysisResult Result(string id = "a", long epoch = 1, JevStatus status = JevStatus.Success)
    {
        var score = new ScoreJudgment(1.42, new Dictionary<int, double> { { 0, 0 }, { 1, .58 }, { 2, .42 }, { 3, 0 } }.ToImmutableDictionary(),
            ImmutableDictionary<int, string>.Empty, .2);
        var judgments = new JevJudgments(new(.91), new(.79), new(.1), new(.2), new(.3),
            new("question", new Dictionary<string, double> { { "question", .88 }, { "other", .12 } }.ToImmutableDictionary(), .4), score, score);
        return new(epoch, id, "jev-v0.1", "fingerprint", status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new(status, judgments), 0, 10, false, 0, 0, 0);
    }
    [Fact]
    public void QueuedRemoteBecomesReadyMovesThenRetiresWithoutResurrection()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [Visible()], true, false);
        Assert.True(hud.Schedule(Message(), JevStatus.Queued));
        Assert.Equal("Jev · 分析中…", Assert.Single(hud.Cards).Presentation.Heading);
        Assert.True(hud.Apply(Result()));
        hud.Observe(1, [Visible(y: 50)], true, false);
        Assert.Equal(50, Assert.Single(hud.Cards).Bubble.Y);
        hud.Observe(1, [], true, false);
        Assert.Single(hud.Cards);
        Assert.False(hud.Apply(Result())); // Late result has no current visible target.
        hud.Observe(1, [], false, false);
        Assert.Empty(hud.Cards); // Static missing view hides the grace anchor, not a persistent ghost.
        hud.Observe(1, [], true, false);
        Assert.Empty(hud.Cards);
        hud.Observe(1, [Visible()], true, false);
        Assert.False(hud.Schedule(Message(), JevStatus.Queued));
        Assert.False(hud.Apply(Result()));
    }
    [Fact]
    public void TemporaryHidingDoesNotRetireButEpochSwitchClears()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [Visible()], true, false);
        hud.Schedule(Message(), JevStatus.Queued);
        for (var i = 0; i < 5; i++) hud.Observe(1, [], true, true);
        Assert.Empty(hud.Cards);
        hud.Observe(1, [Visible(y: 80)], true, false);
        Assert.Single(hud.Cards);
        hud.Observe(2, [], true, true);
        Assert.False(hud.Apply(Result()));
        Assert.Empty(hud.Cards);
    }

    [Fact]
    public void OneMissThenUnchangedViewHidesWithoutRetiringAndCanRecover()
    {
        var hud = new HudLifecycle(); hud.Observe(1, [Visible()], true, false); hud.Schedule(Message(), JevStatus.Queued);
        hud.Apply(Result()); hud.Observe(1, [], true, false); Assert.Single(hud.Cards);
        hud.Observe(1, [], false, false); Assert.Empty(hud.Cards);
        hud.Observe(1, [Visible(y: 80)], true, false);
        Assert.Equal("Jev", Assert.Single(hud.Cards).Presentation.Heading);
    }
    [Fact]
    public void OnlyQueuedEligibleTargetsCreateCardsAndFailuresHide()
    {
        var hud = new HudLifecycle(); hud.Observe(1, [Visible()], true, false);
        Assert.False(hud.Schedule(Message(side: MessageSide.Self), JevStatus.Queued));
        Assert.False(hud.Schedule(Message(), JevStatus.QueueFull));
        Assert.False(hud.Schedule(Message() with { Origin = MessageObservationKind.History }, JevStatus.Queued));
        Assert.True(hud.Schedule(Message(), JevStatus.Queued));
        Assert.False(hud.Apply(Result(status: JevStatus.Timeout)));
        Assert.Empty(hud.Cards);
    }
    [Fact]
    public void ComposerUsesSelectedProbabilityAndWeightedScoreNotConfidence()
    {
        var rows = new JudgmentComposer().Compose(Result())!.Rows;
        Assert.Equal(new HudRow("询问", "88%"), rows[0]);
        Assert.Equal(new HudRow("期待回应", "91%"), rows[1]);
        Assert.Equal(new HudRow("紧迫度", "1.4/3"), rows[3]);
        Assert.Null(new JudgmentComposer().Compose(Result(status: JevStatus.MalformedResponse)));
    }

    [Fact]
    public void IndependentRapidTargetsRetainTheirOwnResults()
    {
        var hud = new HudLifecycle();
        hud.Observe(1, [Visible("a"), Visible("b", 200), Visible("c", 300)], true, false);
        foreach (var id in new[] { "a", "b", "c" }) Assert.True(hud.Schedule(Message(id), JevStatus.Queued));
        Assert.True(hud.Apply(Result("c")));
        Assert.True(hud.Apply(Result("a")));
        Assert.Equal("Jev · 分析中…", hud.Cards.Single(c => c.Key.MessageId == "b").Presentation.Heading);
        Assert.Equal("Jev", hud.Cards.Single(c => c.Key.MessageId == "a").Presentation.Heading);
        Assert.False(hud.Apply(Result("unknown")));
        Assert.False(hud.Apply(Result("b", epoch: 2)));
    }

    [Theory]
    [InlineData(JevStatus.Timeout)]
    [InlineData(JevStatus.RateLimited)]
    [InlineData(JevStatus.Unauthorized)]
    [InlineData(JevStatus.ServiceUnavailable)]
    [InlineData(JevStatus.MalformedResponse)]
    [InlineData(JevStatus.NotConfigured)]
    public void FailuresNeverFabricateRows(JevStatus failure)
    {
        var hud = new HudLifecycle(); hud.Observe(1, [Visible()], true, false); hud.Schedule(Message(), JevStatus.Queued);
        Assert.False(hud.Apply(Result(status: failure))); Assert.Empty(hud.Cards);
    }

    [Fact]
    public void MalformedPresentationDoesNotThrow()
    {
        var result = Result();
        var invalid = result with
        {
            Evaluation = result.Evaluation with
            {
                Judgments = result.Evaluation.Judgments! with
                {
                    SpeechAct = new("missing", ImmutableDictionary<string, double>.Empty, 1)
                }
            }
        };
        Assert.Null(new JudgmentComposer().Compose(invalid));
    }

    [Theory]
    [InlineData(WeChatJevHud.Capture.CaptureMethod.RenderWindow, false, false)]
    [InlineData(WeChatJevHud.Capture.CaptureMethod.RenderWindow, true, true)]
    [InlineData(WeChatJevHud.Capture.CaptureMethod.VisibleDesktopFallback, true, false)]
    [InlineData(WeChatJevHud.Capture.CaptureMethod.VisibleDesktopFallback, false, false)]
    public void UnverifiedCaptureNeverReachesPerception(WeChatJevHud.Capture.CaptureMethod method, bool verified, bool expected)
        => Assert.Equal(expected, OverlayCapturePolicy.CanProcess(method, verified));
}
