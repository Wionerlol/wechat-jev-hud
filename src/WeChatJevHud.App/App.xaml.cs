using System.Windows;

namespace WeChatJevHud.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--capture-debug"))
        {
            var capture = new MainWindow(); capture.Closed += (_, _) => Shutdown(); capture.Show();
        }
        else
        {
            var controller = new HudControllerWindow(e.Args);
            MainWindow = controller;
            controller.Show();
        }
    }
}
