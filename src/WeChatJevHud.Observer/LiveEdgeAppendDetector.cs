using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;

namespace WeChatJevHud.Observer;

public sealed record LiveEdgeBubble(MessageSide Side, CapturePixelRect Bounds, string? CropFingerprint, bool IsFullyVisible,
    string? LogicalId = null);

public sealed record AppendAttempt(int Start, string Reason, int? PreviousIndex = null, int? CurrentIndex = null,
    IReadOnlyList<string>? DifferentFields = null);

public sealed record AppendAttemptTrace(bool AtLiveEdge, bool Stable, int PreviousCount, int CurrentCount,
    int CurrentStart, CapturePixelRect Viewport, int BoundaryMargin,
    IReadOnlyList<LiveEdgeBubble> Previous, IReadOnlyList<LiveEdgeBubble> Current,
    IReadOnlyList<AppendAttempt> Attempts, string Decision);

public sealed record LiveEdgeAppendDecision(string Reason, int PreviousStart, int SuffixStart, double DeltaY, int CurrentStart = 0)
{
    public bool IsAppend => SuffixStart >= 0;
    public static LiveEdgeAppendDecision Suppressed(string reason) => new(reason, 0, -1, 0);
}

/// <summary>
/// Recognizes an ordered live-sequence extension, never arbitrary history overlap.
/// No OCR, LCS, text-specific rules or recent-history buffer participates.
/// </summary>
public sealed class LiveEdgeAppendDetector
{
    public LiveEdgeAppendDecision Detect(
        IReadOnlyList<LiveEdgeBubble> previous,
        IReadOnlyList<LiveEdgeBubble> current,
        CapturePixelRect viewport,
        bool atLiveEdge,
        bool stable,
        int boundaryMargin = 0,
        Action<AppendAttemptTrace>? diagnosticSink = null)
    {
        var currentStart = 0;
        var attempts = diagnosticSink is null ? null : new List<AppendAttempt>();
        void Reject(int start, string reason, int? p = null, int? c = null, IReadOnlyList<string>? fields = null) =>
            attempts?.Add(new(start, reason, p, c, fields));
        LiveEdgeAppendDecision Finish(LiveEdgeAppendDecision decision)
        {
            diagnosticSink?.Invoke(new(atLiveEdge, stable, previous.Count, current.Count, currentStart,
                viewport, boundaryMargin, previous, current, attempts!, decision.Reason));
            return decision;
        }
        if (!stable || !atLiveEdge) return Finish(LiveEdgeAppendDecision.Suppressed("not_stable_live_edge"));
        while (currentStart < current.Count && !current[currentStart].IsFullyVisible && current[currentStart].Bounds.Y <= viewport.Y + boundaryMargin)
            currentStart++;
        if (current.Count == currentStart || current.Skip(currentStart).Any(b => !b.IsFullyVisible))
            return Finish(LiveEdgeAppendDecision.Suppressed("empty_or_partial_view"));
        if (previous.Count == 0)
            return Finish(currentStart == 0 ? new("established_empty_edge", 0, 0, 0) : LiveEdgeAppendDecision.Suppressed("partial_empty_edge"));

        // Pair by chronological occurrence, not a best-fit LCS that can steal a repeat.
        for (var start = 0; start < previous.Count; start++)
        {
            var retained = previous.Count - start;
            if (current.Count <= retained + currentStart) { Reject(start, "current_not_longer_than_retained"); continue; }
            var mismatch = Enumerable.Range(0, retained).FirstOrDefault(i => !Same(previous[start + i], current[currentStart + i]), -1);
            if (mismatch >= 0)
            {
                var p = start + mismatch;
                var c = currentStart + mismatch;
                Reject(start, "same_failed", p, c, DifferentFields(previous[p], current[c]));
                continue;
            }
            var delta = current[currentStart].Bounds.Y - previous[start].Bounds.Y;
            var tolerance = Math.Max(1, previous[start].Bounds.Height * .05);
            var inconsistent = Enumerable.Range(0, retained).FirstOrDefault(i =>
                    Math.Abs(current[currentStart + i].Bounds.Y - previous[start + i].Bounds.Y - delta) > tolerance ||
                    current[currentStart + i].Bounds.X != previous[start + i].Bounds.X, -1);
            if (inconsistent >= 0)
            {
                var p = start + inconsistent;
                var c = currentStart + inconsistent;
                Reject(start, current[c].Bounds.X != previous[p].Bounds.X ? "x_mismatch" : "translation_inconsistent", p, c);
                continue;
            }
            var suffix = currentStart + retained;
            if (current[suffix].Bounds.Y < current[suffix - 1].Bounds.Bottom) { Reject(start, "suffix_overlaps_previous"); continue; }

            if (start == 0 && currentStart == 0 && Math.Abs(delta) <= tolerance)
                return Finish(new("stationary_prefix_suffix", start, retained, delta));

            // A translated append needs a non-ambiguous anchor. Disappearing older
            // prefix occurrences must have actually left the viewport, not be skipped.
            var uniqueAnchor = Enumerable.Range(0, retained).Any(i =>
                previous.Count(b => Same(b, current[currentStart + i])) == 1 &&
                current.Count(b => Same(b, current[currentStart + i])) == 1);
            if (!uniqueAnchor) { Reject(start, "no_unique_anchor"); continue; }
            if (delta >= -tolerance) { Reject(start, "delta_not_upward"); continue; }
            var clippedPrefix = previous.Take(start).Where(b => b.Bounds.Bottom + delta > viewport.Y).ToArray();
            if (clippedPrefix.Length != currentStart) { Reject(start, "clipped_prefix_count_mismatch"); continue; }
            if (!Enumerable.Range(0, currentStart).All(i =>
                    clippedPrefix[i].Bounds.Y + delta <= viewport.Y + boundaryMargin &&
                    clippedPrefix[i].Side == current[i].Side &&
                    clippedPrefix[i].Bounds.X == current[i].Bounds.X &&
                    clippedPrefix[i].Bounds.Width == current[i].Bounds.Width &&
                    Math.Abs(clippedPrefix[i].Bounds.Bottom + delta - current[i].Bounds.Bottom) <= tolerance))
            { Reject(start, "clipped_prefix_geometry_mismatch"); continue; }
            if (current[^1].Bounds.Bottom < previous[^1].Bounds.Bottom - tolerance) { Reject(start, "current_bottom_regressed"); continue; }
            return Finish(new("anchored_translated_suffix", start, suffix, delta, currentStart));
        }
        return Finish(LiveEdgeAppendDecision.Suppressed("no_ordered_live_extension"));
    }

    private static IReadOnlyList<string> DifferentFields(LiveEdgeBubble a, LiveEdgeBubble b)
    {
        var fields = new List<string>();
        if (!a.IsFullyVisible || !b.IsFullyVisible) fields.Add("visibility");
        if (a.Side != b.Side) fields.Add("side");
        if (a.CropFingerprint is null || a.CropFingerprint != b.CropFingerprint) fields.Add("crop_fingerprint");
        if (a.Bounds.Width != b.Bounds.Width) fields.Add("width");
        if (a.Bounds.Height != b.Bounds.Height) fields.Add("height");
        return fields;
    }

    private static bool Same(LiveEdgeBubble a, LiveEdgeBubble b) =>
        a.IsFullyVisible && b.IsFullyVisible && a.Side == b.Side &&
        a.CropFingerprint is not null && a.CropFingerprint == b.CropFingerprint &&
        a.Bounds.Width == b.Bounds.Width && a.Bounds.Height == b.Bounds.Height;
}
