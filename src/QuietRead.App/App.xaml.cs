using System.Windows;

namespace QuietRead;

public partial class App : Application
{
    public string? LaunchPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0) LaunchPath = e.Args[0];
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("程序遇到无法继续处理的错误，请重新打开。\n错误类型：" + args.Exception.GetType().Name,
                "QuietRead", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        base.OnStartup(e);
    }
}
