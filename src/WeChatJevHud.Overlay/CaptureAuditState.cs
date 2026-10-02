using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Capture;

namespace WeChatJevHud.Overlay;

public sealed record CaptureConfiguration(nint Handle, nint? RenderHandle, DesktopPixelRect Bounds,
    string Monitor, uint DpiX, uint DpiY)
{
    public static CaptureConfiguration From(WeChatWindowSnapshot w) =>
        new(w.Handle, w.RenderHandle, w.CaptureBounds, w.Monitor.DeviceName, w.Dpi.X, w.Dpi.Y);
}

public enum CaptureAuditOutcome { Passed, TransientConfigurationChanged, NotForeground, HardSafetyFailure }
public enum CaptureAuditPhase { WaitingForStable, Auditing, Verified, HardFailed }

/// <summary>Only stable, freshly audited physical configurations authorize render capture.</summary>
public sealed class CaptureAuditState
{
    public const int RequiredStableObservations = 2;
    private CaptureConfiguration? _candidate, _verified, _failed;
    private int _stable;
    public long Generation { get; private set; }
    public CaptureAuditPhase Phase { get; private set; } = CaptureAuditPhase.WaitingForStable;
    public CaptureAuditOutcome? LastOutcome { get; private set; }
    public bool Observe(CaptureConfiguration? configuration, bool foreground)
    {
        if (configuration != _candidate)
        {
            _candidate = configuration; _stable = 0; _verified = null; _failed = null; Generation++;
        }
        if (!foreground || configuration is null)
        { _stable = 0; _verified = null; Phase = CaptureAuditPhase.WaitingForStable; return false; }
        if (_failed == configuration) { Phase = CaptureAuditPhase.HardFailed; return false; }
        if (_verified == configuration) { Phase = CaptureAuditPhase.Verified; return false; }
        Phase = CaptureAuditPhase.WaitingForStable;
        if (++_stable < RequiredStableObservations) return false;
        Phase = CaptureAuditPhase.Auditing; return true;
    }
    public CaptureAuditOutcome Complete(CaptureConfiguration audited, CaptureConfiguration? current,
        bool foreground, CaptureExclusionEvidence evidence)
    {
        var outcome = current != audited || !evidence.ConfigurationStable
            ? CaptureAuditOutcome.TransientConfigurationChanged
            : !foreground || !evidence.ForegroundPreserved ? CaptureAuditOutcome.NotForeground
            : evidence.SafeRender ? CaptureAuditOutcome.Passed : CaptureAuditOutcome.HardSafetyFailure;
        LastOutcome = outcome; _verified = null; _stable = 0;
        if (outcome == CaptureAuditOutcome.Passed) { _verified = audited; Phase = CaptureAuditPhase.Verified; }
        else if (outcome == CaptureAuditOutcome.HardSafetyFailure) { _failed = audited; Phase = CaptureAuditPhase.HardFailed; }
        else Phase = CaptureAuditPhase.WaitingForStable;
        return outcome;
    }
    public bool CanProcess(CaptureConfiguration current, CaptureMethod method) =>
        Phase == CaptureAuditPhase.Verified && current == _verified && OverlayCapturePolicy.CanProcess(method, true);
}
