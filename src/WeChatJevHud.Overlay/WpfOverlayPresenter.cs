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
    private OverlayScene _latest = OverlayScene.Hidden;
    private OverlayScene? _rendered;
    private bool _disposed, _probe;
    public nint Handle { get; }
    public bool AffinityConfigured { get; }
    public double LastUpdateMs { get; private set; }
    public event Action<OverlayScene, double, double>? SceneRendered;

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
    public void Present(OverlayScene scene) => Interlocked.Exchange(ref _latest, scene);
    private void Tick()
    {
        if (_disposed || _probe) return;
        var scene = Volatile.Read(ref _latest);
        if (scene.Window is not { } w || scene.Cards.IsEmpty || !OverlayNative.IsCurrentForeground(w))
        {
            _window.Hide(); _rendered = null;
            // A later foreground restore must wait for a fresh observed scene, never reuse a pre-hide scene.
            Interlocked.CompareExchange(ref _latest, OverlayScene.Hidden, scene);
            return;
        }
        if (ReferenceEquals(scene, _rendered)) return;
        if (_rendered is { } prior && prior.Window?.CaptureBounds == w.CaptureBounds && prior.Window.Dpi == w.Dpi &&
            prior.Demo == scene.Demo && prior.Cards.SequenceEqual(scene.Cards)) { _rendered = scene; return; }
        var dispatchMs = (DateTimeOffset.UtcNow - scene.CreatedAt).TotalMilliseconds;
        var timer = Stopwatch.StartNew();
        _window.Show();
        if (!OverlayNative.Position(Handle, w.CaptureBounds) || OverlayNative.GetDpiForWindow(Handle) != w.Dpi.X)
        { _window.Hide(); return; }
        _canvas.Children.Clear();
        foreach (var card in scene.Cards)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = (scene.Demo ? "DEMO · " : "") + card.Presentation.Heading,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 169, 178)),
                FontSize = 12,
                Margin = new(0, 0, 0, 6)
            });
            foreach (var row in card.Presentation.Rows)
            {
                var grid = new Grid { Height = 22 };
                grid.Children.Add(new TextBlock { Text = row.Label, Foreground = Brushes.Gainsboro, FontSize = 13 });
                grid.Children.Add(new TextBlock { Text = row.Value, Foreground = Brushes.Gainsboro, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right });
                content.Children.Add(grid);
            }
            var border = new Border
            {
                Width = card.Bounds.Width,
                Height = card.Bounds.Height,
                Padding = new(11),
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 33, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(66, 72, 80)),
                BorderThickness = new(1),
                CornerRadius = new(9),
                Child = content
            };
            Canvas.SetLeft(border, card.Bounds.X); Canvas.SetTop(border, card.Bounds.Y); _canvas.Children.Add(border);
        }
        _rendered = scene; LastUpdateMs = timer.Elapsed.TotalMilliseconds;
        SceneRendered?.Invoke(scene, LastUpdateMs, dispatchMs);
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
