using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

public sealed record OverlayLayoutOptions(int MaxVisibleCards = 3, double CardWidth = 210, double Gap = 10,
    double MaxVerticalShift = 96);

public sealed class OverlayLayoutEngine
{
    private readonly OverlayLayoutOptions _options;
    public OverlayLayoutEngine(OverlayLayoutOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaxVisibleCards < 1 || _options.CardWidth < 100 || _options.Gap < 0 || _options.MaxVerticalShift < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    public ImmutableArray<PositionedHudCard> Layout(IEnumerable<HudCard> cards, CapturePixelRect usable,
        DpiSnapshot dpi, IReadOnlyList<CapturePixelRect> allBubbles)
    {
        var area = OverlayCoordinateMapper.ToLocal(usable, dpi);
        var obstacles = allBubbles.Select(b => OverlayCoordinateMapper.ToLocal(b, dpi)).ToArray();
        var result = ImmutableArray.CreateBuilder<PositionedHudCard>();
        foreach (var card in cards.OrderByDescending(c => c.Sequence).ThenBy(c => c.Key.MessageId, StringComparer.Ordinal)
                     .Take(_options.MaxVisibleCards))
        {
            var bubble = OverlayCoordinateMapper.ToLocal(card.Bubble, dpi);
            var height = 48 + card.Presentation.Rows.Length * 22;
            var xs = new[] { bubble.Right + _options.Gap, bubble.X - _options.Gap - _options.CardWidth };
            LocalDipRect? found = null;
            foreach (var x in xs)
            {
                // Small, deterministic vertical shifts. Never cross unrelated bubbles.
                for (var step = 0; step <= _options.MaxVerticalShift / 8 && found is null; step++)
                    foreach (var sign in step == 0 ? new[] { 1 } : new[] { 1, -1 })
                    {
                        var box = new LocalDipRect(x, bubble.Y + step * 8 * sign, _options.CardWidth, height);
                        if (area.Contains(box) && !obstacles.Any(box.Intersects) && !result.Any(c => box.Intersects(c.Bounds)))
                        { found = box; break; }
                    }
                if (found is not null) break;
            }
            if (found is { } placement) result.Add(new(card.Key, placement, card.Presentation));
        }
        return result.ToImmutable();
    }
}
