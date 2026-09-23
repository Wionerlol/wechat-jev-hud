using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;

namespace WeChatJevHud.TypeSafe;

public sealed record JevContextOptions(int MaxPriorMessages = 8, int MaxTextCharacters = 2000);

public sealed class JevContextBuilder
{
    private readonly JevContextOptions _options;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JevContextBuilder(JevContextOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaxPriorMessages is < 0 or > 25 || _options.MaxTextCharacters is < 1 or > 4000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public static bool IsEligible(ObservedMessage message, long epoch) =>
        message.ConversationEpochId == epoch && message.Side == MessageSide.Remote &&
        message.Origin == MessageObservationKind.LiveNew && message.IsTrustedForSemantics;

    public JevContext? Build(long epoch, IReadOnlyList<ObservedMessage> timeline, ObservedMessage target)
    {
        if (!IsEligible(target, epoch)) return null;
        var index = -1;
        for (var i = 0; i < timeline.Count; i++)
            if (timeline[i].Id == target.Id && timeline[i].ConversationEpochId == epoch) { index = i; break; }
        if (index < 0 || timeline[index] != target) return null;

        var current = Convert(target);
        var used = Characters(current);
        if (used > _options.MaxTextCharacters) return null; // Never truncate the target's meaning.
        var recent = new List<SemanticMessage>();
        var gaps = false;
        for (var i = index - 1; i >= 0; i--)
        {
            var message = timeline[i];
            if (message.ConversationEpochId != epoch) continue;
            if (!message.IsTrustedForSemantics || message.Side == MessageSide.Unknown)
            {
                gaps = true;
                continue;
            }
            var item = Convert(message);
            var size = Characters(item);
            if (recent.Count == _options.MaxPriorMessages || used + size > _options.MaxTextCharacters)
            {
                gaps = true;
                break; // Drop older whole messages; no mid-codepoint or misleading partial text.
            }
            recent.Add(item);
            used += size;
        }
        recent.Reverse();
        var state = new JevSemanticState(current, recent.ToImmutableArray(), "zh-CN", gaps);
        var json = SerializeState(state);
        if (Encoding.UTF8.GetByteCount(json) > 16000) return null;
        return new(epoch, target.Id, state, ConvertHash(json), used, json.EnumerateRunes().Count());
    }

    internal static string SerializeState(JevSemanticState state) => JsonSerializer.Serialize(state, JsonOptions);
    private static string ConvertHash(string json) => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private static int Characters(SemanticMessage message) => message.Text.EnumerateRunes().Count() +
        (message.QuotedText?.EnumerateRunes().Count() ?? 0);

    // Observer has no independent quote-trust provenance. Never upload QuotedText until
    // a future contract can attest to that region separately; do not borrow main trust.
    private static SemanticMessage Convert(ObservedMessage message) =>
        new(message.Side == MessageSide.Self ? "self" : "remote", message.NormalizedText);
}
