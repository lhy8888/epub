using System.IO;
using System.Windows;
using QuietRead.Core;

namespace QuietRead;

public partial class App : Application
{
    private readonly bool _createMainWindow;
    private IDisposable? _stateSession;

    public App() : this(true) { }

    internal App(bool createMainWindow) => _createMainWindow = createMainWindow;

    public string? LaunchPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (_createMainWindow && e.Args.Length > 0) LaunchPath = e.Args[0];
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("程序遇到无法继续处理的错误，请重新打开。\n错误类型：" + args.Exception.GetType().Name,
                "QuietRead", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        StateStore? store = null;
        if (_createMainWindow)
        {
            store = new StateStore();
            try { _stateSession = store.AcquireSession(); }
            catch (IOException)
            {
                MessageBox.Show("QuietRead 已在运行，或阅读记录目录暂时无法访问。\n请在现有窗口中打开书籍，或关闭它后再试。",
                    "QuietRead", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1); return;
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("无法访问阅读记录目录，请检查当前用户文件夹的写入权限。",
                    "QuietRead", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1); return;
            }
        }
        base.OnStartup(e);
        if (_createMainWindow)
        {
            MainWindow = new MainWindow(store!);
            MainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { _stateSession?.Dispose(); }
    }
}
