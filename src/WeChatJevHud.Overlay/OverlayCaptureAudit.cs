using WeChatJevHud.Capture;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

public sealed record CaptureExclusionEvidence(bool VisiblePositiveControl, bool RenderExcluded, bool DesktopExcluded,
    bool ForegroundPreserved, bool StylesCorrect, bool PhysicalBoundsCorrect, bool AffinityConfigured)
{
    public bool SafeRender => VisiblePositiveControl && RenderExcluded && ForegroundPreserved && StylesCorrect && PhysicalBoundsCorrect;
}

public static class OverlayCaptureAudit
{
    public static async Task<CaptureExclusionEvidence> RunAsync(WpfOverlayPresenter presenter, WeChatWindowSnapshot w, IWindowCapture capture)
    {
        var before = OverlayNative.GetForegroundWindow();
        if (!OverlayNative.IsCurrentForeground(w)) return new(false, false, false, false, false, false, false);
        try
        {
            // Prove the marker really renders, rather than claiming exclusion from an invisible host.
            await presenter.ProbeAsync(w, true, false);
            var positive = HasMarker(capture.Capture(w with { RenderHandle = null }));
            await presenter.ProbeAsync(w, true, true);
            var render = capture.Capture(w);
            var desktop = capture.Capture(w with { RenderHandle = null });
            return new(positive, render.Method == CaptureMethod.RenderWindow && !HasMarker(render), !HasMarker(desktop),
                OverlayNative.GetForegroundWindow() == before && OverlayNative.IsCurrentForeground(w),
                ((long)OverlayNative.GetWindowLongPtr(presenter.Handle, -20) & OverlayNative.RequiredStyles) == OverlayNative.RequiredStyles,
                OverlayNative.Bounds(presenter.Handle) == w.CaptureBounds,
                OverlayNative.GetWindowDisplayAffinity(presenter.Handle, out var affinity) && affinity == 0x11);
        }
        finally { await presenter.ProbeAsync(w, false, true); }
    }
    private static bool HasMarker(CapturedFrame frame)
    {
        var count = 0;
        for (var y = 0; y < Math.Min(100, frame.Height); y++)
            for (var x = 0; x < Math.Min(100, frame.Width); x++)
            {
                var p = y * frame.Stride + x * 4;
                if (frame.Bgra32Pixels[p] > 200 && frame.Bgra32Pixels[p + 1] < 30 && frame.Bgra32Pixels[p + 2] > 220) count++;
            }
        return count > 1000;
    }
}
