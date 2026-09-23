using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Observer;

public enum BubbleCompletenessReason
{
    Interior, NearTopBoundary, NearBottomBoundary, SuspiciouslyShortAtBoundary, ShapeClipped, Unknown
}

public sealed record BubbleCompletenessEvidence(
    bool IsFullyVisible, int DistanceToTop, int DistanceToBottom, int BubbleHeight,
    double? NominalFullBubbleHeight, double? HeightRatio, bool BoundaryRisk,
    BubbleCompletenessReason Reason, bool TopCapClosed, bool BottomCapClosed,
    double BoundaryBand);

/// <summary>Frame-local completeness evidence only; does not filter detections or perform OCR.</summary>
public sealed class BubbleCompletenessAnalyzer
{
    public IReadOnlyList<BubbleCompletenessEvidence> Analyze(
        CapturedFrame frame, CapturePixelRect roi, IReadOnlyList<DetectedBubble> bubbles)
    {
        // Clearly interior components supply scale without relying on desktop/DPI dimensions.
        var interiorHeights = bubbles.Where(b =>
                b.Bounds.Y - roi.Y > b.Bounds.Height / 2d &&
                roi.Bottom - b.Bounds.Bottom > b.Bounds.Height / 2d)
            .Select(b => b.Bounds.Height).Order().ToArray();
        double? nominal = interiorHeights.Length == 0 ? null : interiorHeights[0];
        return bubbles.Select(b => AnalyzeOne(frame, roi, b.Bounds, nominal)).ToArray();
    }

    private static BubbleCompletenessEvidence AnalyzeOne(
        CapturedFrame frame, CapturePixelRect roi, CapturePixelRect bounds, double? nominal)
    {
        var top = bounds.Y - roi.Y;
        var bottom = roi.Bottom - bounds.Bottom;
        var scale = nominal ?? bounds.Height;
        var band = Math.Max(1, scale * .20);
        var nearTop = top <= band;
        var nearBottom = bottom <= band;
        var risk = nearTop || nearBottom;
        var ratio = nominal is { } h ? bounds.Height / h : (double?)null;
        var (topClosed, bottomClosed) = risk ? ClosedCaps(frame, bounds, scale) : (false, false);
        var reason = BubbleCompletenessReason.Interior;
        var complete = true;
        if (top <= 0 || bottom <= 0)
        {
            complete = false;
            reason = top <= 0 ? BubbleCompletenessReason.NearTopBoundary : BubbleCompletenessReason.NearBottomBoundary;
        }
        else if (risk)
        {
            // A genuinely closed rounded cap can prove a normal near-edge bubble is
            // complete even when the only interior scale sample is multiline.
            complete = topClosed && bottomClosed;
            reason = complete
                ? nearTop ? BubbleCompletenessReason.NearTopBoundary : BubbleCompletenessReason.NearBottomBoundary
                : ratio < .75 ? BubbleCompletenessReason.SuspiciouslyShortAtBoundary
                : nominal is null ? BubbleCompletenessReason.Unknown : BubbleCompletenessReason.ShapeClipped;
        }
        return new(complete, top, bottom, bounds.Height, nominal, ratio, risk, reason, topClosed, bottomClosed, band);
    }

    private static (bool Top, bool Bottom) ClosedCaps(CapturedFrame frame, CapturePixelRect b, double scale)
    {
        // Dominant quantized crop color estimates the bubble background, not its text.
        var histogram = new Dictionary<int, int>();
        for (var y = b.Y; y < b.Bottom; y++)
            for (var x = b.X; x < b.Right; x++)
            {
                var i = y * frame.Stride + x * 4;
                var key = (frame.Bgra32Pixels[i] >> 3) | ((frame.Bgra32Pixels[i + 1] >> 3) << 5) | ((frame.Bgra32Pixels[i + 2] >> 3) << 10);
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        if (histogram.Count == 0) return (false, false);
        var color = histogram.MaxBy(p => p.Value).Key;
        var blue = ((color & 31) << 3) + 4;
        var green = (((color >> 5) & 31) << 3) + 4;
        var red = (((color >> 10) & 31) << 3) + 4;
        int RowCount(int y)
        {
            var count = 0;
            for (var x = b.X; x < b.Right; x++)
            {
                var i = y * frame.Stride + x * 4;
                if (Math.Abs(frame.Bgra32Pixels[i] - blue) <= 8 &&
                    Math.Abs(frame.Bgra32Pixels[i + 1] - green) <= 8 &&
                    Math.Abs(frame.Bgra32Pixels[i + 2] - red) <= 8) count++;
            }
            return count;
        }
        var depth = Math.Clamp((int)Math.Round(scale * .12), 2, Math.Max(2, b.Height / 4));
        bool Closed(bool top)
        {
            var edge = top ? b.Y : b.Bottom - 1;
            var step = top ? 1 : -1;
            var outer = RowCount(edge);
            var inner = Enumerable.Range(1, Math.Min(depth, b.Height - 1)).Max(d => RowCount(edge + step * d));
            return outer > b.Width * .4 && inner - outer >= Math.Max(2, scale * .04);
        }
        return b.Height < 4 ? (false, false) : (Closed(true), Closed(false));
    }
}
