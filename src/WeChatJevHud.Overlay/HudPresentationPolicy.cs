using System.Collections.Immutable;

namespace WeChatJevHud.Overlay;

public sealed record HudPresentationPolicyOptions(int MaxCompactItems = 2);
public sealed record HudPresentationPlan(ImmutableArray<HudCard> OrderedItems, int MaxCompactItems);

/// <summary>Chronological priority is independent of screen Y and result-completion order.</summary>
public sealed class HudPresentationPolicy
{
    private readonly HudPresentationPolicyOptions _options;
    public HudPresentationPolicy(HudPresentationPolicyOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaxCompactItems < 0) throw new ArgumentOutOfRangeException(nameof(options));
    }
    public HudPresentationPlan Select(IEnumerable<HudCard> items) => new(items.OrderByDescending(i => i.Sequence)
        .ThenBy(i => i.Key.MessageId, StringComparer.Ordinal).ToImmutableArray(), _options.MaxCompactItems);
}
