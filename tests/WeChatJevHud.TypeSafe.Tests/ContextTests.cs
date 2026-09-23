using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Ocr;
using Xunit;

namespace WeChatJevHud.TypeSafe.Tests;

public class ContextTests
{
    internal static ObservedMessage Message(string id, string text = "好", long epoch = 1,
        MessageSide side = MessageSide.Remote, MessageObservationKind origin = MessageObservationKind.LiveNew) =>
        new(id, epoch, side, text, text, OcrTextStatus.Recognized, null, new(0, 0, 50, 40),
            DateTimeOffset.UnixEpoch, origin, "visual", true,
            SemanticRegionVerified: true, OutsideSemanticEdgeGuard: true);

    [Fact]
    public void Context_is_same_epoch_prior_only_and_omits_untrusted_text_with_gap()
    {
        var prior = Message("prior", "你到了吗", side: MessageSide.Self);
        var unknown = Message("uncertain", "must-not-upload") with { OcrStatus = OcrTextStatus.LowConfidence };
        var target = Message("target", "到了");
        var context = new JevContextBuilder().Build(1,
            [Message("other", "other epoch", 2), prior, unknown, target, Message("later", "future")], target);
        Assert.NotNull(context);
        Assert.Equal("你到了吗", Assert.Single(context.State.RecentMessages).Text);
        Assert.Equal("self", context.State.RecentMessages[0].Side);
        Assert.True(context.State.ContextHasGaps);
        Assert.Equal("到了", context.State.CurrentMessage.Text);
    }

    [Fact]
    public void Budget_keeps_recent_whole_messages_and_unicode_and_does_not_borrow_quote_trust()
    {
        var target = Message("target", "😀") with { QuotedText = "unverified quote" };
        var timeline = new[] { Message("old", "old"), Message("recent", "😀你"), target };
        var context = new JevContextBuilder(new(8, 3)).Build(1, timeline, target)!;
        Assert.Equal("😀你", Assert.Single(context.State.RecentMessages).Text);
        Assert.Equal(3, context.TextCharacters);
        Assert.True(context.State.ContextHasGaps);
        Assert.Null(context.State.CurrentMessage.QuotedText);
        Assert.Null(new JevContextBuilder(new(8, 1)).Build(1, [Message("long", "不要")], Message("long", "不要")));
    }

    [Fact]
    public void Most_recent_eight_are_chronological_and_fingerprint_tracks_exact_semantic_state()
    {
        var timeline = Enumerable.Range(0, 12).Select(i => Message(i.ToString(), $"text{i}")).ToArray();
        var builder = new JevContextBuilder();
        var context = builder.Build(1, timeline, timeline[^1])!;
        Assert.Equal(Enumerable.Range(3, 8).Select(i => $"text{i}"), context.State.RecentMessages.Select(m => m.Text));
        Assert.Equal(context.Fingerprint, builder.Build(1, timeline, timeline[^1])!.Fingerprint);
        timeline[10] = timeline[10] with { NormalizedText = "changed" };
        Assert.NotEqual(context.Fingerprint, builder.Build(1, timeline, timeline[^1])!.Fingerprint);
    }
}
