using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WeChatJevHud.Overlay;

namespace WeChatJevHud.App;

public sealed class HudControllerWindow : Window
{
    private readonly CancellationTokenSource _stop = new();
    private readonly WpfOverlayPresenter _presenter;
    private readonly Task _runtime;
    private readonly DispatcherTimer _timer;
    private string _status = "Starting (no raw chat diagnostics)…";
    private string _debug = "No typed result yet.";
    private bool _closed;
    public HudControllerWindow(string[] args)
    {
        Title = args.Contains("--demo") ? "Jev HUD · DEMO" : "Jev HUD · Phase 6";
        Width = 440; Height = 170; ShowActivated = false;
        var panel = new StackPanel { Margin = new(15) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var debug = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 300,
            Margin = new(0, 12, 0, 0)
        };
        var stop = new Button { Content = "停止 / Stop", Margin = new(0, 12, 0, 0), Width = 100 };
        stop.Click += (_, _) => Close(); panel.Children.Add(status); panel.Children.Add(stop);
        if (args.Contains("--hud-debug")) { Height = 500; panel.Children.Add(debug); }
        Content = panel;
        _presenter = new();
        var coordinator = new HudRuntimeCoordinator(_presenter, args, s => Interlocked.Exchange(ref _status, s),
            s => Interlocked.Exchange(ref _debug, s));
        _runtime = Task.Run(() => coordinator.RunAsync(_stop.Token));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        { status.Text = Volatile.Read(ref _status); debug.Text = Volatile.Read(ref _debug); }, Dispatcher);
        var secondsIndex = Array.IndexOf(args, "--seconds");
        if (secondsIndex >= 0 && secondsIndex + 1 < args.Length && int.TryParse(args[secondsIndex + 1], out var seconds) && seconds > 0)
        {
            var autoStop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            autoStop.Tick += (_, _) => { autoStop.Stop(); Close(); }; autoStop.Start();
        }
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true; _closed = true; _stop.Cancel(); _presenter.Present(OverlayScene.Hidden);
            await _runtime; _timer.Stop(); _presenter.Dispose(); _stop.Dispose(); Application.Current.Shutdown();
        };
    }
}
