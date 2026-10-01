using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

public sealed record OverlayLayoutOptions(int MaxVisibleCards = 3, double CardWidth = 300, double Gap = 10,
    double MaxVerticalShift = 96, double MaxHorizontalShift = 20);

public sealed class OverlayLayoutEngine
{
    private readonly OverlayLayoutOptions _options;
    public OverlayLayoutEngine(OverlayLayoutOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaxVisibleCards < 1 || _options.CardWidth < 100 || _options.Gap < 0 || _options.MaxVerticalShift < 0 || _options.MaxHorizontalShift < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    public ImmutableArray<PositionedHudCard> Layout(IEnumerable<HudCard> cards, CapturePixelRect usable,
        DpiSnapshot dpi, IReadOnlyList<CapturePixelRect> allBubbles, Action<HudTrace>? trace = null)
    {
        var area = OverlayCoordinateMapper.ToLocal(usable, dpi);
        var obstacles = allBubbles.Select(b => OverlayCoordinateMapper.ToLocal(b, dpi)).ToArray();
        var result = ImmutableArray.CreateBuilder<PositionedHudCard>();
        var ordered = cards.OrderByDescending(c => c.Sequence).ThenBy(c => c.Key.MessageId, StringComparer.Ordinal).ToArray();
        foreach (var card in ordered.Skip(_options.MaxVisibleCards))
            trace?.Invoke(new("hud_layout_attempt", new
            {
                key = card.Key,
                bubble_rect = card.Bubble,
                sequence = card.Sequence,
                presentation_state = card.Presentation.Rows.IsEmpty ? "Pending" : "Ready",
                candidate_placement_count = 0,
                reason = "card_limit",
                usable,
                dpi
            }));
        foreach (var card in ordered.Take(_options.MaxVisibleCards))
        {
            var bubble = OverlayCoordinateMapper.ToLocal(card.Bubble, dpi);
            var height = card.Presentation.Rows.IsEmpty ? 48 : 20 + Math.Ceiling(card.Presentation.Rows.Length / 2d) * 22;
            var preferredRight = bubble.Right + _options.Gap;
            // A shorter new bubble's card may need to clear a slightly wider preceding bubble
            // when shifted upward. Keep the alternate nearby and in DIPs; collision checks still apply.
            var xs = new[] { preferredRight }.Concat(obstacles.Select(o => o.Right + _options.Gap)
                    .Where(x => x > preferredRight && x - preferredRight <= _options.MaxHorizontalShift)
                    .Distinct().Order())
                .Append(bubble.X - _options.Gap - _options.CardWidth);
            LocalDipRect? found = null;
            var attempts = 0;
            var rejected = new Dictionary<string, int>();
            foreach (var x in xs)
            {
                // Small, deterministic vertical shifts. Never cross unrelated bubbles.
                for (var step = 0; step <= _options.MaxVerticalShift / 8 && found is null; step++)
                    foreach (var sign in step == 0 ? new[] { 1 } : new[] { 1, -1 })
                    {
                        var box = new LocalDipRect(x, bubble.Y + step * 8 * sign, _options.CardWidth, height);
                        attempts++;
                        var rejection = !area.Contains(box) ? x < area.X ? "left_overflow" : box.Right > area.Right ? "right_overflow" : "outside_usable_area"
                            : obstacles.Any(box.Intersects) ? "bubble_collision" : result.Any(c => box.Intersects(c.Bounds)) ? "card_collision" : null;
                        if (rejection is null)
                        { found = box; break; }
                        rejected[rejection] = rejected.GetValueOrDefault(rejection) + 1;
                    }
                if (found is not null) break;
            }
            trace?.Invoke(new("hud_layout_attempt", new
            {
                key = card.Key,
                bubble_rect = card.Bubble,
                sequence = card.Sequence,
                presentation_state = card.Presentation.Rows.IsEmpty ? "Pending" : "Ready",
                candidate_placement_count = attempts,
                reason = found is not null ? "success" : rejected.ContainsKey("card_collision") ? "card_collision"
                    : rejected.ContainsKey("bubble_collision") ? "bubble_collision" : "outside_usable_area",
                rejections = rejected,
                usable,
                dpi
            }));
            if (found is { } placement)
            {
                result.Add(new(card.Key, placement, card.Presentation));
                trace?.Invoke(new("hud_layout_output", new { key = card.Key, bounds = placement }));
            }
        }
        return result.ToImmutable();
    }
}
