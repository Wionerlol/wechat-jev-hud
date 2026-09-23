using System.Collections.Immutable;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Overlay;

public readonly record struct HudKey(long Epoch, string MessageId);
/// <summary>Local WPF DIPs, never desktop coordinates.</summary>
public readonly record struct LocalDipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Intersects(LocalDipRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    public bool Contains(LocalDipRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}
public sealed record HudRow(string Label, string Value);
public sealed record HudPresentationModel(string Heading, ImmutableArray<HudRow> Rows, JevAnalysisResult? Analysis = null)
{
    public static HudPresentationModel Pending { get; } = new("Jev · 分析中…", []);
}
public sealed record HudCard(HudKey Key, CapturePixelRect Bubble, HudPresentationModel Presentation, long Sequence);
public sealed record PositionedHudCard(HudKey Key, LocalDipRect Bounds, HudPresentationModel Presentation);
public sealed record OverlayScene(WeChatWindowSnapshot? Window, ImmutableArray<PositionedHudCard> Cards, bool Demo = false)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public static OverlayScene Hidden { get; } = new(null, []);
}
public interface IOverlayPresenter
{
    // Nonblocking, latest-wins. Implementations marshal UI mutations themselves.
    void Present(OverlayScene scene);
}
public static class OverlayCoordinateMapper
{
    public static LocalDipRect ToLocal(CapturePixelRect rect, DpiSnapshot dpi)
    {
        if (dpi.X == 0 || dpi.Y == 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        return new(rect.X / dpi.ScaleX, rect.Y / dpi.ScaleY, rect.Width / dpi.ScaleX, rect.Height / dpi.ScaleY);
    }
    public static DesktopPixelRect ToDesktop(DesktopPixelRect capture, CapturePixelRect bubble) =>
        new(checked(capture.X + bubble.X), checked(capture.Y + bubble.Y), bubble.Width, bubble.Height);
}
