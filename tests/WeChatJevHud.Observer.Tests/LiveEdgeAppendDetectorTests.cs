using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;

namespace WeChatJevHud.Observer.Tests;

public sealed class LiveEdgeAppendDetectorTests
{
    [Fact]
    public void Logged_smoke3_geometry_rejects_clipped_prefix_even_assuming_retained_hashes_match()
    {
        // Bounds/visibility from the real pre-anchor and first-anchor log frames.
        // Hashes were NOT logged: tokens explicitly assume the strongest possible
        // retained identity. This proves a geometry blocker, not a full pixel replay.
        LiveEdgeBubble B(int x, int y, int w, int h, string assumedHash, bool full = true) =>
            new(MessageSide.Self, new(x, y, w, h), assumedHash, full);
        LiveEdgeBubble[] before = [B(788, 143, 223, 54, "old"), B(548, 227, 463, 82, "multiline"),
            B(803, 401, 208, 54, "anchor2"), B(947, 485, 64, 54, "equal"), B(947, 569, 64, 54, "equal")];
        LiveEdgeBubble[] after = [B(548, 120, 456, 44, "partial", false), B(803, 255, 208, 54, "anchor2"),
            B(947, 339, 64, 54, "equal"), B(947, 423, 64, 54, "equal"), B(803, 569, 208, 54, "anchor3")];
        AppendAttemptTrace? trace = null;
        var result = _detector.Detect(before, after, new(377, 120, 741, 523), true, true, 1, t => trace = t);
        Assert.Equal("no_ordered_live_extension", result.Reason);
        Assert.Equal(1, trace!.CurrentStart);
        Assert.Equal("clipped_prefix_geometry_mismatch", trace.Attempts.Single(a => a.Start == 2).Reason);
        Assert.Equal(5, trace.Attempts.Count);
        Assert.Equal(result, _detector.Detect(before, after, new(377, 120, 741, 523), true, true, 1));
    }

    [Fact]
    public void Trace_reports_exact_same_fields_without_affecting_the_decision()
    {
        var before = new[] { Bubble(60) };
        var after = new[] { Bubble(60, "changed", MessageSide.Remote) with { Bounds = new(30, 60, 61, 25) }, Bubble(100) };
        AppendAttemptTrace? trace = null;
        var result = _detector.Detect(before, after, View, true, true, diagnosticSink: t => trace = t);
        var attempt = Assert.Single(trace!.Attempts);
        Assert.Equal("same_failed", attempt.Reason);
        Assert.Equal(new[] { "side", "crop_fingerprint", "width", "height" }, attempt.DifferentFields);
        Assert.Equal(0, attempt.PreviousIndex);
        Assert.Equal(0, attempt.CurrentIndex);
        Assert.Equal(result, _detector.Detect(before, after, View, true, true));
    }

    private readonly LiveEdgeAppendDetector _detector = new();
    private static readonly CapturePixelRect View = new(0, 20, 500, 800);
    private static LiveEdgeBubble Bubble(int y, string key = "equal", MessageSide side = MessageSide.Self) =>
        new(side, new(30, y, 60, 24), key, true);

    public static IEnumerable<object[]> Repetitions()
    {
        foreach (var side in new[] { MessageSide.Self, MessageSide.Remote })
            for (var count = 1; count <= 8; count++)
                for (var added = 0; added <= 3; added++)
                    yield return [side, count, added];
    }

    [Theory]
    [MemberData(nameof(Repetitions))]
    public void Stationary_occurrences_reserve_exactly_the_new_suffix(MessageSide side, int count, int added)
    {
        var before = Enumerable.Range(0, count).Select(i => Bubble(60 + i * 40, side: side)).ToArray();
        var after = Enumerable.Range(0, count + added).Select(i => Bubble(60 + i * 40, side: side)).ToArray();
        var decision = _detector.Detect(before, after, View, true, true);
        Assert.Equal(added > 0, decision.IsAppend);
        if (added > 0)
        {
            Assert.Equal(0, decision.PreviousStart);
            Assert.Equal(count, decision.SuffixStart);
        }
        Assert.False(_detector.Detect(before, after, View, false, true).IsAppend);
        Assert.False(_detector.Detect(before, after, View, true, false).IsAppend);
    }

    [Theory]
    [InlineData(MessageSide.Self)]
    [InlineData(MessageSide.Remote)]
    public void Mixed_repeats_after_unique_anchor_preserve_order_during_append_translation(MessageSide side)
    {
        for (var repeats = 1; repeats <= 8; repeats++)
        {
            var before = new[] { Bubble(60, "anchor") }.Concat(
                Enumerable.Range(0, repeats).Select(i => Bubble(100 + i * 40, side: side))).ToArray();
            var after = before.Select(b => b with { Bounds = b.Bounds with { Y = b.Bounds.Y - 30 } })
                .Append(Bubble(before[^1].Bounds.Y + 10, side: side)).ToArray();
            var result = _detector.Detect(before, after, View, true, true);
            Assert.True(result.IsAppend);
            Assert.Equal(before.Length, result.SuffixStart);
            Assert.Equal(-30, result.DeltaY);
        }
    }

    [Fact]
    public void All_binary_sequences_through_length_six_keep_chronological_prefix_on_append()
    {
        for (var count = 1; count <= 6; count++)
            for (var mask = 0; mask < (1 << count); mask++)
                foreach (var side in new[] { MessageSide.Self, MessageSide.Remote })
                {
                    var before = Enumerable.Range(0, count).Select(i => Bubble(60 + 40 * i,
                        (mask & (1 << i)) == 0 ? "same" : "other", side)).ToArray();
                    foreach (var key in new[] { "same", "other" })
                    {
                        var after = before.Append(Bubble(60 + 40 * count, key, side)).ToArray();
                        var result = _detector.Detect(before, after, View, true, true);
                        Assert.True(result.IsAppend);
                        Assert.Equal(0, result.PreviousStart);
                        Assert.Equal(count, result.SuffixStart);
                    }
                }
    }

    [Fact]
    public void Top_partial_history_does_not_veto_an_anchored_complete_append()
    {
        LiveEdgeBubble[] before = [Bubble(30, "old"), Bubble(70, "anchor"), Bubble(110)];
        LiveEdgeBubble[] after = [Bubble(20, "clipped") with { Bounds = new(30, 20, 60, 14), IsFullyVisible = false },
            Bubble(50, "anchor"), Bubble(90), Bubble(130)];
        var result = _detector.Detect(before, after, View, true, true);
        Assert.True(result.IsAppend);
        Assert.Equal(1, result.PreviousStart);
        Assert.Equal(1, result.CurrentStart);
        Assert.Equal(3, result.SuffixStart);
    }

    [Fact]
    public void Append_can_push_old_prefix_out_of_view_when_surviving_unique_anchor_proves_order()
    {
        LiveEdgeBubble[] before = [Bubble(30, "old"), Bubble(70, "anchor"), Bubble(110), Bubble(150)];
        LiveEdgeBubble[] after = [Bubble(30, "anchor"), Bubble(70), Bubble(110), Bubble(150)];
        var result = _detector.Detect(before, after, View, true, true);
        Assert.True(result.IsAppend);
        Assert.Equal(1, result.PreviousStart);
        Assert.Equal(3, result.SuffixStart);
    }

    [Fact]
    public void Scroll_equal_sequence_into_history_is_not_an_append()
    {
        LiveEdgeBubble[] before = [Bubble(60), Bubble(100), Bubble(140)];
        foreach (var delta in new[] { -40, -20, 20, 40 })
        {
            LiveEdgeBubble[] after = [Bubble(100 + delta), Bubble(140 + delta), Bubble(180 + delta, "old-history")];
            Assert.False(_detector.Detect(before, after, View, true, true).IsAppend);
        }
    }

    [Fact]
    public void Scrolling_reveals_prefix_not_suffix_even_when_visual_neighbors_match()
    {
        LiveEdgeBubble[] before = [Bubble(60, "A"), Bubble(100), Bubble(140)];
        LiveEdgeBubble[] after = [Bubble(30, "history"), Bubble(70, "A"), Bubble(110), Bubble(150)];
        Assert.False(_detector.Detect(before, after, View, true, true).IsAppend);
        // Nonuniform motion is not a translated live sequence, even with an anchor.
        after = [Bubble(30, "A"), Bubble(90), Bubble(110), Bubble(150)];
        Assert.False(_detector.Detect(before, after, View, true, true).IsAppend);
    }

    [Fact]
    public void Missing_visible_prefix_cannot_be_skipped_to_manufacture_an_append()
    {
        LiveEdgeBubble[] before = [Bubble(60, "old"), Bubble(100, "anchor"), Bubble(140)];
        LiveEdgeBubble[] after = [Bubble(80, "anchor"), Bubble(120), Bubble(160)];
        Assert.False(_detector.Detect(before, after, View, true, true).IsAppend);
    }

    [Fact]
    public void Partial_suffix_and_empty_transition_do_not_authorize_new_messages()
    {
        LiveEdgeBubble[] before = [Bubble(60)];
        LiveEdgeBubble[] after = [Bubble(60), Bubble(100) with { IsFullyVisible = false }];
        Assert.False(_detector.Detect(before, after, View, true, true).IsAppend);
        Assert.False(_detector.Detect(before, [], View, true, true).IsAppend);
        Assert.False(_detector.Detect([], after, View, true, true).IsAppend);
        Assert.True(_detector.Detect([], before, View, true, true).IsAppend);
    }
}
