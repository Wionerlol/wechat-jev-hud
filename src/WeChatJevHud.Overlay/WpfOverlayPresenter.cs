using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Overlay;

/// <summary>One HWND, one Canvas. Latest-wins mailbox never waits for Dispatcher rendering.</summary>
public sealed class WpfOverlayPresenter : IOverlayPresenter, IDisposable
{
    private readonly Window _window;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _timer;
    private sealed record Submission(long Generation, OverlayScene Scene);
    private Submission _latest = new(0, OverlayScene.Hidden);
    private long _generation;
    private (long Generation, string Action)? _lastTickTrace;
    private OverlayScene? _rendered;
    private bool _disposed, _probe;
    public nint Handle { get; }
    public bool AffinityConfigured { get; }
    public double LastUpdateMs { get; private set; }
    public event Action<OverlayScene, double, double>? SceneRendered;
    public Action<HudTrace>? Trace { get; set; }

    public WpfOverlayPresenter()
    {
        _window = new Window
        {
            Title = "WeChat Jev HUD",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            Content = _canvas,
            Width = 1,
            Height = 1,
        };
        Handle = new WindowInteropHelper(_window).EnsureHandle();
        OverlayNative.Configure(Handle);
        HwndSource.FromHwnd(Handle).AddHook(Hook);
        AffinityConfigured = OverlayNative.SetWindowDisplayAffinity(Handle, 0x11);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick(), _window.Dispatcher);
    }
    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (msg == 0x84) { handled = true; return -1; } // HTTRANSPARENT (also EX_TRANSPARENT across processes)
        return 0;
    }
    public void Present(OverlayScene scene)
    {
        var submission = new Submission(Interlocked.Increment(ref _generation), scene);
        Trace?.Invoke(new("hud_scene_submitted", new
        {
            scene_generation = submission.Generation,
            density = scene.Layout.Density.ToString(),
            latest_key = scene.Layout.LatestKey,
            anchors = scene.Layout.Anchors,
            overflow = scene.Layout.Overflow,
            cards = scene.Cards.Select(c => new { key = c.Key, level = c.Level.ToString(), presentation_state = c.Presentation.Rows.IsEmpty ? "Pending" : "Ready", bounds = c.Bounds }).ToArray()
        }));
        Interlocked.Exchange(ref _latest, submission);
    }
    private void TraceTick(Submission submission, string action)
    {
        if (Trace is null || _lastTickTrace == (submission.Generation, action)) return;
        _lastTickTrace = (submission.Generation, action);
        Trace(new("hud_scene_tick", new
        {
            scene_generation = submission.Generation,
            card_keys = submission.Scene.Cards.Select(c => c.Key).ToArray(),
            action
        }));
    }
    private void Tick()
    {
        if (_disposed || _probe) return;
        var submission = Volatile.Read(ref _latest);
        var scene = submission.Scene;
        var hideReason = scene.Window is null || scene.Layout.IsEmpty ? "hidden_empty"
            : !OverlayNative.IsWeChatForeground(scene.Window) ? "hidden_not_foreground"
            : !OverlayNative.SnapshotMatchesCurrentWindow(scene.Window) ? "hidden_snapshot_mismatch" : null;
        if (hideReason is not null)
        {
            TraceTick(submission, hideReason);
            _window.Hide(); _rendered = null;
            // A later foreground restore must wait for a fresh observed scene, never reuse a pre-hide scene.
            Interlocked.CompareExchange(ref _latest, new(submission.Generation, OverlayScene.Hidden), submission);
            return;
        }
        var w = scene.Window!;
        if (ReferenceEquals(scene, _rendered)) { TraceTick(submission, "deduped_same_scene"); return; }
        if (_rendered is { } prior && prior.Window?.CaptureBounds == w.CaptureBounds && prior.Window.Dpi == w.Dpi &&
            prior.Demo == scene.Demo && prior.Cards.SequenceEqual(scene.Cards) &&
            prior.Layout.Anchors.SequenceEqual(scene.Layout.Anchors) && prior.Layout.Overflow == scene.Layout.Overflow &&
            prior.Layout.LatestKey == scene.Layout.LatestKey && prior.Layout.Density == scene.Layout.Density)
        { _rendered = scene; TraceTick(submission, "deduped_same_scene"); return; }
        var dispatchMs = (DateTimeOffset.UtcNow - scene.CreatedAt).TotalMilliseconds;
        var timer = Stopwatch.StartNew();
        _window.Show();
        if (!OverlayNative.Position(Handle, w.CaptureBounds))
        { _window.Hide(); TraceTick(submission, "position_failed"); return; }
        if (OverlayNative.GetDpiForWindow(Handle) != w.Dpi.X)
        { _window.Hide(); TraceTick(submission, "dpi_mismatch"); return; }
        _canvas.Children.Clear();
        foreach (var card in scene.Cards)
        {
            var content = new StackPanel();
            var prefix = (scene.Demo ? "DEMO · " : "") + card.Ordinal + " · ";
            var offscreen = card.Visibility == HudVisibilityState.OffscreenRetained ? " · 屏外" : "";
            if (card.Presentation.Rows.IsEmpty)
            {
                content.Children.Add(Text(prefix + card.Presentation.Heading + offscreen, 20));
            }
            else if (card.Level == HudPresentationLevel.Compact)
            {
                var rows = card.Presentation.Rows;
                content.Children.Add(Text(prefix + rows[0].Label + " " + rows[0].Value + offscreen, 18));
                if (rows.Length >= 3) content.Children.Add(Text("回应" + rows[1].Value + " · 前文" + rows[2].Value, 18));
            }
            else
            {
                content.Children.Add(Text(prefix + "Jev · 最新" + offscreen, 22));
                if (card.Presentation.Details.IsDefaultOrEmpty)
                    foreach (var row in card.Presentation.Rows) content.Children.Add(Row(row));
                else foreach (var group in card.Presentation.Details)
                    {
                        content.Children.Add(Text(group.Label, 20, true));
                        foreach (var row in group.Rows) content.Children.Add(Row(row));
                    }
            }
            var border = new Border
            {
                Width = card.Bounds.Width,
                Height = card.Bounds.Height,
                Padding = new(9),
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 33, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(66, 72, 80)),
                BorderThickness = new(1),
                CornerRadius = new(9),
                Child = content
            };
            Canvas.SetLeft(border, card.Bounds.X); Canvas.SetTop(border, card.Bounds.Y); _canvas.Children.Add(border);
        }
        foreach (var anchor in scene.Layout.Anchors)
            AddSmall(anchor.Bounds, anchor.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (scene.Layout.Overflow is { } overflow)
            AddSmall(overflow.Bounds, overflow.IncludesLatest ? "Jev · " + overflow.Count : "+" + overflow.Count + " 条");
        _rendered = scene; LastUpdateMs = timer.Elapsed.TotalMilliseconds;
        TraceTick(submission, "rendered");
        Trace?.Invoke(new("hud_scene_rendered", new
        {
            scene_generation = submission.Generation,
            density = scene.Layout.Density.ToString(),
            latest_key = scene.Layout.LatestKey,
            anchors = scene.Layout.Anchors,
            overflow = scene.Layout.Overflow,
            cards = scene.Cards.Select(c => new
            {
                key = c.Key,
                level = c.Level.ToString(),
                ordinal = c.Ordinal,
                visibility = c.Visibility.ToString(),
                bounds = c.Bounds,
                rows = c.Presentation.Rows,
                details = c.Presentation.Details.IsDefault ? null : (object)c.Presentation.Details
            }).ToArray()
        }));
        SceneRendered?.Invoke(scene, LastUpdateMs, dispatchMs);
    }

    private static TextBlock Text(string value, double height, bool muted = false) => new()
    {
        Text = value,
        Height = height,
        FontSize = 12,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = muted ? new SolidColorBrush(Color.FromRgb(160, 169, 178)) : Brushes.Gainsboro
    };
    private static Grid Row(HudRow row)
    {
        var grid = new Grid { Height = 22 };
        grid.Children.Add(Text(row.Label, 22));
        grid.Children.Add(new TextBlock { Text = row.Value, Foreground = Brushes.Gainsboro, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right });
        return grid;
    }
    private void AddSmall(LocalDipRect bounds, string value)
    {
        var border = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            CornerRadius = new(5),
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 33, 37)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(66, 72, 80)),
            BorderThickness = new(1),
            Child = new TextBlock
            {
                Text = value,
                FontSize = 11,
                Foreground = Brushes.Gainsboro,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(border, bounds.X); Canvas.SetTop(border, bounds.Y); _canvas.Children.Add(border);
    }

    // Used only by explicit native capture audit; no semantic result or screenshot persistence.
    public async Task ProbeAsync(WeChatWindowSnapshot w, bool show, bool exclude)
    {
        await _window.Dispatcher.InvokeAsync(() =>
        {
            _probe = show;
            _window.Hide(); _canvas.Children.Clear(); _rendered = null;
            if (!show) return;
            OverlayNative.SetWindowDisplayAffinity(Handle, exclude ? 0x11u : 0u);
            _canvas.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(251, 3, 241)), Width = 64, Height = 64 });
            _window.Show(); OverlayNative.Position(Handle, w.CaptureBounds);
        });
        await Task.Delay(120);
        OverlayNative.DwmFlush();
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _window.Close();
    }
}
