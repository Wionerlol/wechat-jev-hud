using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Ocr;
using Xunit;

namespace WeChatJevHud.TypeSafe.Tests;

public class CoordinatorTests
{
    internal sealed class FakeClient : ITypeSafeClient
    {
        public List<JevSemanticState> States { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }
        public async Task<TypeSafeEvaluation> EvaluateAsync(JevSemanticState state, JevQuestionSet questions, CancellationToken cancellationToken)
        {
            States.Add(state);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task; // Deliberately ignores cancellation to exercise late completion.
            return new(JevStatus.Success, RequestCount: 1);
        }
    }

    [Fact]
    public async Task Trusted_remote_target_is_batched_once_while_self_never_calls_service()
    {
        var client = new FakeClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        var self = ContextTests.Message("self", side: MessageSide.Self);
        var remote = ContextTests.Message("remote");
        Assert.Equal(JevStatus.SkippedNotEligible, coordinator.TrySchedule([self, remote], self));
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([self, remote], remote));
        Assert.Equal(JevStatus.Duplicate, coordinator.TrySchedule([self, remote], remote));
        await coordinator.CompleteAsync();
        Assert.Single(client.States);
        Assert.Equal(JevStatus.Success, Assert.Single(coordinator.DrainResults()).Status);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("history")]
    [InlineData("bootstrap")]
    [InlineData("low")]
    [InlineData("empty")]
    [InlineData("unsupported")]
    [InlineData("edge")]
    [InlineData("region")]
    [InlineData("incomplete")]
    [InlineData("partial")]
    [InlineData("fallback")]
    [InlineData("epoch")]
    public async Task Ineligible_targets_never_call_jev(string condition)
    {
        var message = ContextTests.Message("m");
        message = condition switch
        {
            "self" => message with { Side = MessageSide.Self },
            "history" => message with { Origin = MessageObservationKind.History },
            "bootstrap" => message with { Origin = MessageObservationKind.Bootstrap },
            "low" => message with { OcrStatus = OcrTextStatus.LowConfidence },
            "empty" => message with { OcrStatus = OcrTextStatus.NoText },
            "unsupported" => message with { OcrStatus = OcrTextStatus.Unsupported },
            "edge" => message with { OutsideSemanticEdgeGuard = false },
            "region" => message with { SemanticRegionVerified = false },
            "incomplete" => message with { HasCompleteText = false },
            "partial" => message with { IsFullyVisible = false },
            "fallback" => message with { OcrDiagnostics = new OcrDiagnostics(default, default, [], TimeSpan.Zero, RuntimeFallback: true) },
            "epoch" => message with { ConversationEpochId = 2 },
            _ => throw new InvalidOperationException(),
        };
        var client = new FakeClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        Assert.Equal(JevStatus.SkippedNotEligible, coordinator.TrySchedule([message], message));
        await coordinator.CompleteAsync();
        Assert.Empty(client.States);
    }

    [Fact]
    public async Task Equal_text_distinct_ids_are_two_requests_with_prior_only_sibling_context()
    {
        var client = new FakeClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        var a = ContextTests.Message("a");
        var b = ContextTests.Message("b");
        coordinator.TrySchedule([a, b], a);
        coordinator.TrySchedule([a, b], b);
        await coordinator.CompleteAsync();
        Assert.Equal(2, client.States.Count);
        Assert.Empty(client.States[0].RecentMessages);
        Assert.Single(client.States[1].RecentMessages);
        Assert.Equal(new[] { "a", "b" }, coordinator.DrainResults().Select(r => r.MessageId));
    }

    [Fact]
    public async Task Late_A_result_and_queued_A_work_never_publish_in_B()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { Release = release };
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        var a = ContextTests.Message("a");
        coordinator.TrySchedule([a], a);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = ContextTests.Message("queued");
        coordinator.TrySchedule([a, queued], queued);
        coordinator.SetEpoch(2);
        var b = ContextTests.Message("b", epoch: 2);
        coordinator.TrySchedule([b], b);
        release.SetResult();
        await coordinator.CompleteAsync();
        Assert.Equal("b", Assert.Single(coordinator.DrainResults()).MessageId);
        Assert.Equal(2, client.States.Count);
        Assert.Equal(2, coordinator.Counters.Stale);
    }

    [Fact]
    public async Task Queue_full_is_explicit_nonblocking_and_dedupe_is_not_evicted()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { Release = release };
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client), options: new(1, 32, 3));
        coordinator.SetEpoch(1);
        var a = ContextTests.Message("a");
        coordinator.TrySchedule([a], a);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = ContextTests.Message("b");
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([b], b));
        var c = ContextTests.Message("c");
        Assert.Equal(JevStatus.QueueFull, coordinator.TrySchedule([c], c));
        Assert.Equal(JevStatus.Duplicate, coordinator.TrySchedule([c], c));
        var d = ContextTests.Message("d");
        Assert.Equal(JevStatus.DedupCapacityExceeded, coordinator.TrySchedule([d], d));
        release.SetResult();
        await coordinator.CompleteAsync();
        Assert.Equal(2, client.States.Count);
    }

    [Fact]
    public async Task Version_and_context_change_dedupe_key()
    {
        var client = new FakeClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        var target = ContextTests.Message("target");
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([target], target));
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([target], target, new("jev-test-version")));
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([ContextTests.Message("prior"), target], target));
        await coordinator.CompleteAsync();
        Assert.Equal(3, client.States.Count);
    }

    [Fact]
    public async Task Repeated_observer_snapshots_without_new_events_do_not_schedule_and_switch_clears_results()
    {
        var client = new FakeClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        var message = ContextTests.Message("remote");
        var state = new RecentConversationSnapshot(new(1, new Identity(), DateTimeOffset.UtcNow), [message], []);
        for (var i = 0; i < 50; i++) Assert.Empty(coordinator.AcceptObservation(state, []));
        Assert.Empty(client.States);
        Assert.Equal(JevStatus.Queued, Assert.Single(coordinator.AcceptObservation(state, [message])));
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SetEpoch(2);
        await coordinator.CompleteAsync();
        Assert.Empty(coordinator.DrainResults());
        Assert.Single(client.States);
    }

    [Fact]
    public async Task Missing_configuration_through_real_service_does_not_interrupt_observation_submission()
    {
        using var http = TypeSafeHttpClient.CreateHttpClient();
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(new TypeSafeHttpClient(http, null)));
        coordinator.SetEpoch(1);
        var target = ContextTests.Message("m");
        Assert.Equal(JevStatus.Queued, coordinator.TrySchedule([target], target));
        Assert.Equal(JevStatus.Duplicate, coordinator.TrySchedule([target], target));
        await coordinator.CompleteAsync();
        var result = Assert.Single(coordinator.DrainResults());
        Assert.Equal(JevStatus.NotConfigured, result.Status);
        Assert.Null(result.Evaluation.Judgments);
        Assert.Equal(0, coordinator.Counters.Requests);
    }

    [Fact]
    public async Task Error_client_does_not_fault_worker_or_leak_exception_into_diagnostics()
    {
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(new ThrowingClient()));
        coordinator.SetEpoch(1);
        var target = ContextTests.Message("m", "private conversation");
        coordinator.TrySchedule([target], target);
        await coordinator.CompleteAsync();
        var result = Assert.Single(coordinator.DrainResults());
        Assert.Equal(JevStatus.ServiceUnavailable, result.Status);
        var diagnostic = JevDiagnosticFormatter.Format(result);
        Assert.DoesNotContain("secret-key", diagnostic);
        Assert.DoesNotContain("private conversation", diagnostic);
    }

    private sealed class ThrowingClient : ITypeSafeClient
    {
        public Task<TypeSafeEvaluation> EvaluateAsync(JevSemanticState state, JevQuestionSet questions, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret-key private conversation");
    }
    private sealed class Identity : IConversationIdentityEvidence { }
}
