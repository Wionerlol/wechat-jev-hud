using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

public sealed record OverlayLayoutOptions(double RailWidth = 280, double MinimumRailWidth = 220,
    double CompactWidth = 200, double MinimumCompactWidth = 160, double Margin = 16,
    double Gap = 8, double AnchorSize = 20, double AnchorGap = 4, double Clearance = 4);

/// <summary>One right-side semantic rail plus small keyed anchors; no per-card rectangle search.</summary>
public sealed class OverlayLayoutEngine
{
    private readonly OverlayLayoutOptions _options;
    private readonly HudPresentationPolicy _policy;
    public OverlayLayoutEngine(OverlayLayoutOptions? options = null, HudPresentationPolicy? policy = null)
    {
        _options = options ?? new();
        _policy = policy ?? new();
        double[] values = [_options.RailWidth, _options.MinimumRailWidth, _options.CompactWidth,
            _options.MinimumCompactWidth, _options.Margin, _options.Gap, _options.AnchorSize, _options.AnchorGap, _options.Clearance];
        if (values.Any(v => !double.IsFinite(v) || v < 0) || _options.MinimumRailWidth < 180 ||
            _options.RailWidth < _options.MinimumRailWidth || _options.MinimumCompactWidth < 120 ||
            _options.CompactWidth < _options.MinimumCompactWidth || _options.AnchorSize is < 16 or > 24)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public HudRailLayout Layout(IEnumerable<HudCard> cards, CapturePixelRect usable, DpiSnapshot dpi,
        IReadOnlyList<CapturePixelRect> allBubbles, Action<HudTrace>? trace = null)
    {
        var plan = _policy.Select(cards);
        if (plan.OrderedItems.IsEmpty) return HudRailLayout.Empty;
        var area = OverlayCoordinateMapper.ToLocal(usable, dpi);
        var bubbles = allBubbles.Select(b => OverlayCoordinateMapper.ToLocal(b, dpi)).ToArray();
        var anchors = ImmutableArray.CreateBuilder<BubbleAnchor>();
        for (var i = 0; i < plan.OrderedItems.Length; i++)
        {
            var card = plan.OrderedItems[i];
            var bubble = OverlayCoordinateMapper.ToLocal(card.Bubble, dpi);
            var y = bubble.Y + Math.Max(0, (bubble.Height - _options.AnchorSize) / 2);
            var choices = new[] { bubble.Right + _options.AnchorGap, bubble.X - _options.AnchorGap - _options.AnchorSize };
            var bounds = choices.Select(x => new LocalDipRect(x, y, _options.AnchorSize, _options.AnchorSize))
                .Where(r => area.Contains(r) && !bubbles.Any(r.Intersects) && !anchors.Any(a => r.Intersects(a.Bounds)))
                .Cast<LocalDipRect?>().FirstOrDefault();
            if (bounds is { } safe) anchors.Add(new(card.Key, safe, i + 1));
        }

        var inner = new LocalDipRect(area.X + _options.Margin, area.Y + _options.Margin,
            Math.Max(0, area.Width - 2 * _options.Margin), Math.Max(0, area.Height - 2 * _options.Margin));
        var obstacles = bubbles.Concat(anchors.Select(a => a.Bounds)).ToArray();
        var latest = plan.OrderedItems[0];
        var latestHeight = latest.Presentation.Rows.IsEmpty ? 48 : ExpandedHeight(latest.Presentation);
        foreach (var width in new[] { _options.RailWidth, _options.MinimumRailWidth }.Distinct())
            foreach (var region in Regions(inner, width, obstacles))
                if (Fit(region, latestHeight, plan) is { } fit)
                    return Finish(Build(region, HudPresentationLevel.Expanded, latestHeight, fit, plan, anchors.ToImmutable()));

        foreach (var width in new[] { _options.CompactWidth, _options.MinimumCompactWidth }.Distinct())
            foreach (var region in Regions(inner, width, obstacles))
                if (Fit(region, CompactHeight, plan) is { } fit)
                    return Finish(Build(region, HudPresentationLevel.Compact, CompactHeight, fit, plan, anchors.ToImmutable()));

        var indicatorRegion = Regions(inner, 64, obstacles).FirstOrDefault(r => r.Height >= IndicatorHeight);
        if (indicatorRegion.Height >= IndicatorHeight)
        {
            var bounds = indicatorRegion with { Height = IndicatorHeight };
            return Finish(new(HudRailDensity.Indicator, bounds, [], anchors.ToImmutable(),
                new(bounds, plan.OrderedItems.Length, latest.Key, true), latest.Key, plan.OrderedItems.Length));
        }
        // No safe space exists even for a tiny counter. Preserve latest-key evidence in diagnostics,
        // never present an older result as the latest or cover a source bubble.
        return Finish(new(HudRailDensity.AnchorsOnly, null, [], anchors.ToImmutable(), null, latest.Key, plan.OrderedItems.Length));

        HudRailLayout Finish(HudRailLayout result)
        {
            trace?.Invoke(new("hud_rail_layout", new
            {
                latest_key = result.LatestKey,
                density = result.Density.ToString(),
                rail = result.RailBounds,
                active_count = result.ActiveCount,
                overflow_count = result.Overflow?.Count ?? result.ActiveCount - result.Items.Length,
                items = result.Items.Select(i => new
                {
                    key = i.Key,
                    level = i.Level.ToString(),
                    bounds = i.Bounds,
                    state = i.Presentation.Rows.IsEmpty ? "Pending" : "Ready"
                }).ToArray(),
                anchors = result.Anchors,
                reason = result.Density == HudRailDensity.AnchorsOnly ? "no_safe_rail_area" : "success",
                usable,
                dpi
            }));
            return result;
        }
    }

    public const double CompactHeight = 56;
    public const double IndicatorHeight = 28;
    public static double ExpandedHeight(HudPresentationModel presentation) => presentation.Details.IsDefaultOrEmpty
        ? 44 + presentation.Rows.Length * 22
        : 44 + presentation.Details.Sum(g => 20 + g.Rows.Length * 22);

    private IEnumerable<LocalDipRect> Regions(LocalDipRect inner, double width, LocalDipRect[] obstacles)
    {
        if (inner.Width < width || inner.Height <= 0) return [];
        var x = inner.Right - width;
        var blocked = obstacles.Where(o => o.X < inner.Right && o.Right > x)
            .Select(o => (Top: Math.Max(inner.Y, o.Y - _options.Clearance), Bottom: Math.Min(inner.Bottom, o.Bottom + _options.Clearance)))
            .Where(o => o.Bottom > o.Top).OrderBy(o => o.Top).ToArray();
        var gaps = new List<LocalDipRect>();
        var y = inner.Y;
        foreach (var block in blocked)
        {
            if (block.Top > y) gaps.Add(new(x, y, width, block.Top - y));
            y = Math.Max(y, block.Bottom);
        }
        if (y < inner.Bottom) gaps.Add(new(x, y, width, inner.Bottom - y));
        return gaps.OrderByDescending(r => r.Height).ThenBy(r => r.Y);
    }

    private (int Compact, int Overflow, double Height)? Fit(LocalDipRect region, double latestHeight, HudPresentationPlan plan)
    {
        for (var compact = Math.Min(plan.MaxCompactItems, plan.OrderedItems.Length - 1); compact >= 0; compact--)
        {
            var overflow = plan.OrderedItems.Length - 1 - compact;
            var height = latestHeight + compact * (CompactHeight + _options.Gap) + (overflow > 0 ? _options.Gap + IndicatorHeight : 0);
            if (height <= region.Height) return (compact, overflow, height);
        }
        return null;
    }

    private HudRailLayout Build(LocalDipRect region, HudPresentationLevel latestLevel, double latestHeight,
        (int Compact, int Overflow, double Height) fit, HudPresentationPlan plan, ImmutableArray<BubbleAnchor> anchors)
    {
        var items = ImmutableArray.CreateBuilder<PositionedHudCard>();
        var y = region.Y;
        for (var i = 0; i <= fit.Compact; i++)
        {
            var height = i == 0 ? latestHeight : CompactHeight;
            var item = plan.OrderedItems[i];
            items.Add(new(item.Key, new(region.X, y, region.Width, height), item.Presentation,
                i == 0 ? latestLevel : HudPresentationLevel.Compact, i + 1));
            y += height + _options.Gap;
        }
        HudOverflowIndicator? overflow = fit.Overflow == 0 ? null : new(new(region.X, y, region.Width, IndicatorHeight), fit.Overflow, plan.OrderedItems[0].Key, false);
        return new(latestLevel == HudPresentationLevel.Expanded ? HudRailDensity.Expanded : HudRailDensity.Compact,
            region with { Height = fit.Height }, items.ToImmutable(), anchors, overflow, plan.OrderedItems[0].Key, plan.OrderedItems.Length);
    }
}
