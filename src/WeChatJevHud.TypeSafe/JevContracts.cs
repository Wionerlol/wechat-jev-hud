using System.Collections.Immutable;

namespace WeChatJevHud.TypeSafe;

public enum JevStatus
{
    Success, Queued, Duplicate, SkippedNotEligible, NotConfigured, Timeout,
    RateLimited, Unauthorized, ServiceUnavailable, MalformedResponse,
    Cancelled, Stale, QueueFull, DedupCapacityExceeded, InputTooLarge,
}

public sealed record SemanticMessage(string Side, string Text, string? QuotedText = null);

// Code-owned state, not a TypeSafe protocol DTO. No pixels, OCR scores or identities.
public sealed record JevSemanticState(SemanticMessage CurrentMessage,
    ImmutableArray<SemanticMessage> RecentMessages, string Locale, bool ContextHasGaps);

public sealed record JevContext(long ConversationEpochId, string MessageId,
    JevSemanticState State, string Fingerprint, int TextCharacters, int StateCharacters);

public sealed record NoulJudgment(double YesProbability);
public sealed record ChoiceJudgment(string Choice, ImmutableDictionary<string, double> Probabilities,
    double DistributionConfidence);
public sealed record ScoreJudgment(double Score, ImmutableDictionary<int, double> Probabilities,
    ImmutableDictionary<int, string> Legend, double DistributionConfidence);
public sealed record JevJudgments(
    NoulJudgment ExpectsResponse,
    NoulJudgment ReferencesPriorContext,
    NoulJudgment ContainsDirectRequest,
    NoulJudgment ExpressesDisagreementOrCorrection,
    NoulJudgment ContainsTimeOrPlanCommitment,
    ChoiceJudgment SpeechAct,
    ScoreJudgment Urgency,
    ScoreJudgment EmotionalIntensity);

public sealed record TypeSafeEvaluation(JevStatus Status, JevJudgments? Judgments = null,
    string? Model = null, int RequestBytes = 0, int ResponseBytes = 0,
    int? InputTokens = null, int? OutputTokens = null,
    double RoundtripMs = 0, double MappingMs = 0, int RequestCount = 0);

public interface ITypeSafeClient
{
    Task<TypeSafeEvaluation> EvaluateAsync(JevSemanticState state, JevQuestionSet questions,
        CancellationToken cancellationToken);
}

public interface IConversationJudgmentService
{
    Task<TypeSafeEvaluation> AnalyzeAsync(JevContext context, JevQuestionSet questions,
        CancellationToken cancellationToken);
}

public sealed class ConversationJudgmentService(ITypeSafeClient client) : IConversationJudgmentService
{
    public Task<TypeSafeEvaluation> AnalyzeAsync(JevContext context, JevQuestionSet questions,
        CancellationToken cancellationToken) => client.EvaluateAsync(context.State, questions, cancellationToken);
}

public sealed record JevAnalysisResult(long ConversationEpochId, string MessageId,
    string JudgmentSetVersion, string ContextFingerprint, JevStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, TypeSafeEvaluation Evaluation,
    int ContextMessages, int StateCharacters, bool ContextHasGaps,
    double ContextBuildMs, double QueueWaitMs, double TotalSemanticMs);

public sealed record JevCounters(long Requests, long Successes, long Failures,
    long Duplicates, long Skipped, long QueueFull, long Stale, long ResultsDropped);
