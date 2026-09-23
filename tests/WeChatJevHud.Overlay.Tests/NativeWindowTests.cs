using System.Windows.Threading;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public class NativeWindowTests
{
    [Fact]
    public async Task RealHwndStylesPositionShowHideDoNotActivate()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Testhost has no App manifest. Opt this dedicated native test thread into PMv2.
            var priorDpi = OverlayNative.SetThreadDpiAwarenessContext(new nint(-4));
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    Assert.NotEqual(nint.Zero, priorDpi);
                    using var presenter = new WpfOverlayPresenter();
                    var foreground = OverlayNative.GetForegroundWindow();
                    var bounds = new DesktopPixelRect(40, 40, 300, 200);
                    var snapshot = new WeChatWindowSnapshot(foreground, 0, "test", "", "", bounds, bounds, null, null,
                        new MonitorSnapshot("test", bounds, bounds, true), new(96, 96), true, false, true, DateTimeOffset.UtcNow);
                    Assert.Equal(OverlayNative.RequiredStyles, (long)OverlayNative.GetWindowLongPtr(presenter.Handle, -20) & OverlayNative.RequiredStyles);
                    await presenter.ProbeAsync(snapshot, true, true);
                    Assert.Equal(foreground, OverlayNative.GetForegroundWindow());
                    Assert.Equal(bounds, OverlayNative.Bounds(presenter.Handle));
                    Assert.Equal(new nint(-1), OverlayNative.SendMessageW(presenter.Handle, 0x84, 0, 0));
                    Assert.Equal(new nint(3), OverlayNative.SendMessageW(presenter.Handle, 0x21, 0, 0));
                    Assert.True(OverlayNative.GetDpiForWindow(presenter.Handle) >= 96);
                    Assert.True(OverlayNative.GetWindowDisplayAffinity(presenter.Handle, out var affinity));
                    Assert.Equal(0x11u, affinity); // Configuration only; pixel exclusion requires separate audit.
                    await presenter.ProbeAsync(snapshot, false, true);
                    Assert.False(OverlayNative.IsWindowVisible(presenter.Handle));
                    Assert.Equal(foreground, OverlayNative.GetForegroundWindow());
                    completion.SetResult();
                }
                catch (Exception e) { completion.SetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            OverlayNative.SetThreadDpiAwarenessContext(priorDpi);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
