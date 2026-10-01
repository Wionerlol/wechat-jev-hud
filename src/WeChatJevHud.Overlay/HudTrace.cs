namespace WeChatJevHud.Overlay;

/// <summary>Opt-in spatial/result diagnostics. Data must never contain source chat text.</summary>
public sealed record HudTrace(string Stage, object Data);
