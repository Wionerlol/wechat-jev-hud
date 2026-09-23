using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Observer;

public sealed record SemanticEdgeEvidence(int GuardPixels, int DistanceToTop, int DistanceToBottom, bool Excluded);

/// <summary>V0 conservative semantic exclusion, not a completeness classifier.</summary>
public static class SemanticEdgeGuard
{
    public static SemanticEdgeEvidence Evaluate(CapturePixelRect bubble, CapturePixelRect roi, uint dpiY)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpiY);
        var pixels = Math.Max(1, checked((int)Math.Round(6d * dpiY / 96, MidpointRounding.AwayFromZero)));
        var top = bubble.Y - roi.Y;
        var bottom = roi.Bottom - bubble.Bottom;
        return new(pixels, top, bottom, top < pixels || bottom < pixels);
    }
}
