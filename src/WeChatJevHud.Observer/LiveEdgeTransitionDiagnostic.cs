namespace WeChatJevHud.Observer;

/// <summary>Redacted eligibility recovery evidence; never contains conversation text.</summary>
public sealed record LiveEdgeTransitionDiagnostic(
    string State, long? Epoch, string? SavedTailId, string? CurrentTailId,
    int LayoutStableCount, string Reason);
