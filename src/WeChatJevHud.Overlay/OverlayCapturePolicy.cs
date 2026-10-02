using WeChatJevHud.Capture;

namespace WeChatJevHud.Overlay;

public static class OverlayCapturePolicy
{
    // Desktop exclusion may be reported by the probe but is never enabled as a perception path in V0.
    public static bool CanProcess(CaptureMethod method, bool renderVerified) => renderVerified && method == CaptureMethod.RenderWindow;
}
