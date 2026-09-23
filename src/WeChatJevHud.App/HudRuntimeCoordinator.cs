using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Observer;
using WeChatJevHud.Overlay;
using WeChatJevHud.Runtime;
using WeChatJevHud.TypeSafe;
using WeChatJevHud.Vision;
using WeChatJevHud.Windows;

namespace WeChatJevHud.App;

/// <summary>App composition/lifecycle only. Runs off Dispatcher; perception and TypeSafe algorithms are frozen.</summary>
public sealed class HudRuntimeCoordinator(WpfOverlayPresenter presenter, string[] args, Action<string> status, Action<string>? debugInfo = null)
{
    private readonly HudLifecycle _hud = new();
    private readonly OverlayLayoutEngine _layout = new();
    private readonly ConcurrentDictionary<HudKey, DateTimeOffset> _appearance = new();
    private readonly ConcurrentDictionary<HudKey, byte> _readyLogged = new();
    private readonly ConcurrentQueue<string> _renderDiagnostics = new();

    public async Task RunAsync(CancellationToken ct)
    {
        var demo = args.Contains("--demo");
        var debug = args.Contains("--hud-debug");
        var auditOnly = args.Contains("--capture-audit");
        var upload = args.Contains("--jev") && !demo && !auditOnly;
        var tracker = new Win32WeChatWindowTracker();
        var capture = new Win32ScreenRegionCapture();
        var locator = new DarkThemeChatRegionLocator();
        using var http = TypeSafeHttpClient.CreateHttpClient();
        await using var jev = upload ? new JevAnalysisCoordinator(new ConversationJudgmentService(
            new TypeSafeHttpClient(http, Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")))) : null;
        PerceptionRuntime? perception = null;
        var audit = new CaptureAuditState();
        string? auditDiagnostic = null;
        void AuditStatus(CaptureConfiguration? configuration, string reason)
        {
            var line = $"hud_audit_state={audit.Phase} monitor={configuration?.Monitor} dpi={configuration?.DpiX} generation={audit.Generation} reason={reason}";
            if (line != auditDiagnostic) { Console.WriteLine(line); auditDiagnostic = line; }
        }
        long epoch = 0;
        string? lastFingerprint = null;
        var change = new ChatRoiChangeDetector();
        void Rendered(OverlayScene scene, double updateMs, double dispatchMs)
        {
            if (!debug || scene.Demo) return;
            foreach (var card in scene.Cards.Where(c => c.Presentation.Heading == "Jev"))
                if (_appearance.TryGetValue(card.Key, out var start) && _readyLogged.TryAdd(card.Key, 0))
                    _renderDiagnostics.Enqueue(JsonSerializer.Serialize(new
                    {
                        hud_ready = card.Key,
                        hud_update_ms = updateMs,
                        hud_dispatch_ms = dispatchMs,
                        total_remote_to_ready_ms = (DateTimeOffset.UtcNow - start).TotalMilliseconds
                    }));
        }
        presenter.SceneRendered += Rendered;
        try
        {
            if (!demo && !auditOnly)
            {
                status("Starting native Paddle worker…");
                perception = new(Environment.CurrentDirectory, device: Value("--paddle-device") ?? "gpu:0");
                try { await perception.Worker.InitializeAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { status("Paddle unavailable; untrusted runtime fallback only."); }
            }
            while (!ct.IsCancellationRequested)
            {
                while (_renderDiagnostics.TryDequeue(out var rendered)) Console.WriteLine(rendered);
                var w = tracker.Locate();
                if (w is null || !w.IsVisible || w.IsMinimized || !w.IsForeground)
                {
                    audit.Observe(w is null ? null : CaptureConfiguration.From(w), false);
                    AuditStatus(w is null ? null : CaptureConfiguration.From(w), "not_foreground_or_unavailable");
                    presenter.Present(OverlayScene.Hidden);
                    status("HUD hidden: WeChat unavailable / minimized / not foreground.");
                    await Task.Delay(200, ct); continue;
                }
                var configuration = CaptureConfiguration.From(w);
                var shouldAudit = audit.Observe(configuration, true);
                if (!audit.CanProcess(configuration, CaptureMethod.RenderWindow))
                {
                    presenter.Present(OverlayScene.Hidden);
                    AuditStatus(configuration, "configuration_gate");
                    if (!shouldAudit)
                    {
                        status($"Capture safety: {audit.Phase} · DPI={w.Dpi.X}");
                        await Task.Delay(200, ct); continue;
                    }
                    status("Checking capture exclusion (temporary colored test patch; no images saved)…");
                    CaptureExclusionEvidence evidence;
                    try { evidence = await OverlayCaptureAudit.RunAsync(presenter, w, capture); }
                    catch (Exception) { evidence = new(false, false, false, true, false, false, false); }
                    var fresh = tracker.Locate();
                    var outcome = audit.Complete(configuration, fresh is null ? null : CaptureConfiguration.From(fresh),
                        fresh is not null && OverlayNative.IsWeChatForeground(fresh), evidence);
                    Console.WriteLine("hud_capture_audit=" + JsonSerializer.Serialize(evidence));
                    AuditStatus(configuration, outcome.ToString());
                    if (outcome != CaptureAuditOutcome.Passed) { await Task.Delay(200, ct); continue; }
                    w = fresh!;
                    if (auditOnly) { status("Capture audit passed for current monitor. Close to stop, or move across DPI to audit again."); }
                }
                if (auditOnly) { await Task.Delay(200, ct); continue; }
                try
                {
                    var frame = capture.Capture(w);
                    // Unverified BitBlt is NEVER processed, even when affinity was accepted.
                    if (!audit.CanProcess(CaptureConfiguration.From(w), frame.Method) || !OverlayNative.SnapshotMatchesCurrentWindow(w))
                    { presenter.Present(OverlayScene.Hidden); status("Desktop fallback: frame discarded, HUD hidden (fail closed)."); await Task.Delay(200, ct); continue; }
                    var roi = locator.Locate(frame).Bounds;
                    if (demo)
                    {
                        var b = new CapturePixelRect(roi.X + 20, roi.Y + 30, Math.Min(160, roi.Width / 4), 40);
                        var card = new HudCard(new(0, "DEMO"), b, new("Jev · DEMO", [new("询问", "88%"), new("期待回应", "91%"), new("依赖前文", "79%"), new("紧迫度", "0.4/3")]), 1);
                        presenter.Present(new(w, _layout.Layout([card], roi, w.Dpi, [b]), true));
                        status($"DEMO · no OCR/API · DPI={w.Dpi.X} · capture={frame.Method}");
                    }
                    else if (perception is not null)
                    {
                        // Include header changes, before pending-candidate OCR can delay identity confirmation.
                        var fingerprint = change.ComputeFingerprint(frame, new CapturePixelRect(roi.X, 0, roi.Width, roi.Bottom));
                        if (fingerprint != lastFingerprint) presenter.Present(OverlayScene.Hidden); // Hide while a changed view's identity is unresolved, including OCR wait.
                        lastFingerprint = fingerprint;
                        var result = await perception.Observer.ObserveAsync(frame, ct);
                        var state = perception.Observer.State;
                        var hidden = result.Identity.Decision is ConversationIdentityDecision.PendingSwitch or ConversationIdentityDecision.LayoutTransition
                            || result.Identity.LayoutChanged || result.Baseline.State != ConversationBaselineState.Established;
                        if (epoch != result.Epoch.Id) { _appearance.Clear(); _readyLogged.Clear(); epoch = result.Epoch.Id; }
                        _hud.Observe(epoch, state.VisibleMessages, result.FrameChanged, hidden);
                        jev?.SetEpoch(epoch);
                        foreach (var target in result.NewMessages)
                        {
                            var scheduled = jev?.TrySchedule(state.Messages, target) ?? JevStatus.NotConfigured;
                            if (_hud.Schedule(target, scheduled)) _appearance[new(epoch, target.Id)] = frame.CapturedAt - frame.Duration;
                            if (debug) Console.WriteLine($"hud_target epoch={epoch} id={target.Id} side={target.Side} semantic_ready={target.IsTrustedForSemantics} schedule={scheduled}");
                        }
                        double composeMs = 0;
                        foreach (var analysis in jev?.DrainResults() ?? [])
                        {
                            var compose = Stopwatch.StartNew();
                            _hud.Apply(analysis);
                            composeMs += compose.Elapsed.TotalMilliseconds;
                            if (debug)
                            {
                                var formatted = JevDiagnosticFormatter.Format(analysis);
                                Console.WriteLine(formatted); debugInfo?.Invoke(formatted);
                            }
                        }
                        var layout = Stopwatch.StartNew();
                        var eligibleIds = state.Messages.Where(m => m.IsTrustedForSemantics).Select(m => m.Id).ToHashSet();
                        var cards = _layout.Layout(_hud.Cards.Where(c => eligibleIds.Contains(c.Key.MessageId)), roi, w.Dpi, state.VisibleMessages.Select(v => v.BubbleRect).ToArray());
                        var layoutMs = layout.Elapsed.TotalMilliseconds;
                        presenter.Present(hidden ? OverlayScene.Hidden : new(w, cards));
                        status($"WeChat / capture active · Paddle={perception.Worker.RuntimeInfo is not null} · JevConfigured={upload && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"))} · epoch={epoch} · cards={cards.Length} · DPI={w.Dpi.X}");
                        if (debug && result.FrameChanged) Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            epoch,
                            identity = result.Identity.Decision.ToString(),
                            capture_ms = frame.Duration.TotalMilliseconds,
                            change_detect_ms = result.Timings.ChangeDetect.TotalMilliseconds,
                            bubble_detect_ms = result.Timings.BubbleDetect.TotalMilliseconds,
                            ocr_ms = result.Timings.Ocr.TotalMilliseconds,
                            hud_compose_ms = composeMs,
                            hud_layout_ms = layoutMs,
                            new_messages = result.NewMessages.Count,
                            fallback = perception.OcrCounters.Snapshot
                        }));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception) { presenter.Present(OverlayScene.Hidden); status("Runtime unavailable; HUD hidden. No exception text logged."); }
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { status("Startup failed; HUD hidden. Check native runtime setup."); }
        finally
        {
            presenter.SceneRendered -= Rendered;
            presenter.Present(OverlayScene.Hidden); _hud.Clear();
            if (perception is not null) await perception.DisposeAsync();
        }
    }
    private string? Value(string option) { var i = Array.IndexOf(args, option); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
