using System.Diagnostics;
using System.Threading.Channels;
using WeChatJevHud.Observer;

namespace WeChatJevHud.TypeSafe;

public sealed record JevCoordinatorOptions(int QueueCapacity = 8, int ResultCapacity = 32,
    int DedupCapacity = 2048);

/// <summary>
/// Call SetEpoch and TrySchedule with snapshots after reconciliation, never inside OCR.
/// No network or subscriber callbacks run on the perception thread. DrainResults is
/// epoch-filtered atomically; future consumers must retain each result's epoch tag.
/// </summary>
public sealed class JevAnalysisCoordinator : IAsyncDisposable
{
    private sealed record Work(JevContext Context, JevQuestionSet Questions, DateTimeOffset StartedAt,
        long StartedTicks, long QueuedTicks, double ContextBuildMs, CancellationToken EpochToken);
    private readonly object _gate = new();
    private readonly Channel<Work> _queue;
    private readonly Queue<JevAnalysisResult> _results = new();
    private readonly HashSet<(long Epoch, string Id, string Version, string Fingerprint)> _seen = [];
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _epochCancellation = new();
    private readonly IConversationJudgmentService _service;
    private readonly JevContextBuilder _contextBuilder;
    private readonly JevCoordinatorOptions _options;
    private readonly Task _worker;
    private long _epoch;
    private bool _completed;
    private long _requests, _successes, _failures, _duplicates, _skipped, _queueFull, _stale, _resultsDropped;

    public JevAnalysisCoordinator(IConversationJudgmentService service, JevContextBuilder? contextBuilder = null,
        JevCoordinatorOptions? options = null)
    {
        _service = service;
        _contextBuilder = contextBuilder ?? new();
        _options = options ?? new();
        if (_options.QueueCapacity < 1 || _options.ResultCapacity < 1 || _options.DedupCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(_options.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(RunAsync);
    }

    public JevCounters Counters
    {
        get { lock (_gate) return new(_requests, _successes, _failures, _duplicates, _skipped, _queueFull, _stale, _resultsDropped); }
    }

    // Composition-root bridge: called AFTER ObserveAsync, with its frozen chronological state.
    public IReadOnlyList<JevStatus> AcceptObservation(RecentConversationSnapshot state,
        IReadOnlyList<ObservedMessage> newMessages)
    {
        if (state.Epoch is null) return [];
        SetEpoch(state.Epoch.Id);
        return newMessages.Select(message => TrySchedule(state.Messages, message)).ToArray();
    }

    public void SetEpoch(long epoch)
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (_completed || epoch <= _epoch) return; // Observer epochs are monotonic within this lifetime.
            _epoch = epoch;
            _seen.Clear();
            _stale += _results.Count;
            _results.Clear();
            previous = _epochCancellation;
            _epochCancellation = new();
        }
        _ = CancelAndDisposeAsync(previous);
    }

    public JevStatus TrySchedule(IReadOnlyList<ObservedMessage> timeline, ObservedMessage target,
        JevQuestionSet? questions = null)
    {
        var started = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        questions ??= new();
        long epoch;
        lock (_gate)
        {
            epoch = _epoch;
            if (_completed) return JevStatus.Cancelled;
            if (!JevContextBuilder.IsEligible(target, epoch)) { _skipped++; return JevStatus.SkippedNotEligible; }
        }
        var context = _contextBuilder.Build(epoch, timeline, target);
        if (context is null) { lock (_gate) _skipped++; return JevStatus.SkippedNotEligible; }
        var buildMs = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
        lock (_gate)
        {
            if (_completed) return JevStatus.Cancelled;
            if (_epoch != epoch) { _stale++; return JevStatus.Stale; }
            var key = (epoch, target.Id, questions.Version, context.Fingerprint);
            if (_seen.Contains(key)) { _duplicates++; return JevStatus.Duplicate; }
            // Never evict successful keys and accidentally replay them in a long epoch.
            if (_seen.Count >= _options.DedupCapacity) { _skipped++; return JevStatus.DedupCapacityExceeded; }
            _seen.Add(key); // Includes explicit failures/skips: no implicit retry on another frame.
            if (_queue.Writer.TryWrite(new(context, questions, started, ticks, Stopwatch.GetTimestamp(), buildMs, _epochCancellation.Token)))
                return JevStatus.Queued;
            _queueFull++;
            return JevStatus.QueueFull;
        }
    }

    public IReadOnlyList<JevAnalysisResult> DrainResults()
    {
        lock (_gate)
        {
            var current = _results.Where(r => r.ConversationEpochId == _epoch).ToArray();
            _results.Clear();
            return current;
        }
    }

    public Task CompleteAsync()
    {
        lock (_gate) { _completed = true; _queue.Writer.TryComplete(); }
        return _worker;
    }

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (work.Context.ConversationEpochId != _epoch) { _stale++; continue; }
            }
            var waitMs = Stopwatch.GetElapsedTime(work.QueuedTicks).TotalMilliseconds;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, work.EpochToken);
            TypeSafeEvaluation evaluation;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                evaluation = await _service.AnalyzeAsync(work.Context, work.Questions, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { evaluation = new(JevStatus.Cancelled); }
            catch (Exception) { evaluation = new(JevStatus.ServiceUnavailable); } // Never log external exception messages.
            lock (_gate)
            {
                _requests += evaluation.RequestCount;
                if (work.Context.ConversationEpochId != _epoch) { _stale++; continue; }
                if (evaluation.Status == JevStatus.Success) _successes++; else _failures++;
                if (_shutdown.IsCancellationRequested) continue;
                if (_results.Count == _options.ResultCapacity) { _results.Dequeue(); _resultsDropped++; }
                _results.Enqueue(new(work.Context.ConversationEpochId, work.Context.MessageId,
                    work.Questions.Version, work.Context.Fingerprint, evaluation.Status, work.StartedAt,
                    DateTimeOffset.UtcNow, evaluation, work.Context.State.RecentMessages.Length,
                    work.Context.StateCharacters, work.Context.State.ContextHasGaps, work.ContextBuildMs,
                    waitMs, Stopwatch.GetElapsedTime(work.StartedTicks).TotalMilliseconds));
            }
        }
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        finally { source.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await CompleteAsync().ConfigureAwait(false);
        await CancelAndDisposeAsync(_epochCancellation).ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
