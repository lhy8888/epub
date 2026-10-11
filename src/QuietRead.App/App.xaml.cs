using System.IO;
using System.Windows;
using QuietRead.Core;

namespace QuietRead;

public partial class App : Application
{
    private readonly bool _createMainWindow;
    private IDisposable? _stateSession;
    private OpenRequestChannel? _openRequests;

    public App() : this(true) { }

    internal App(bool createMainWindow) => _createMainWindow = createMainWindow;

    public string? LaunchPath { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (_createMainWindow && e.Args.Length > 0) LaunchPath = e.Args[0];
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(UiText.Get("程序遇到无法继续处理的错误，请重新打开。\n错误类型：{0}", args.Exception.GetType().Name),
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
                UiText.Current.Language = store.Load().InterfaceLanguage;
                string? path = LaunchPath;
                try { if (path != null) path = QuietRead.MainWindow.LocalFile(path); }
                catch (Exception exception) when (exception is EpubException or ArgumentException or IOException or UnauthorizedAccessException)
                { MessageBox.Show(UiText.GetError(exception.Message), "QuietRead", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(1); return; }
                if (await OpenRequestChannel.TryForwardAsync(store.DirectoryPath, path)) { Shutdown(); return; }
                MessageBox.Show(UiText.Get("现有 QuietRead 窗口暂时没有响应，或阅读记录目录无法访问。\n请稍后重试。"),
                    "QuietRead", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1); return;
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(UiText.Get("无法访问阅读记录目录，请检查当前用户文件夹的写入权限。"),
                    "QuietRead", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1); return;
            }
        }
        base.OnStartup(e);
        if (_createMainWindow)
        {
            var window = new MainWindow(store!);
            MainWindow = window;
            MainWindow.Show();
            _openRequests = new OpenRequestChannel(store!.DirectoryPath, path =>
                Dispatcher.BeginInvoke(new Action(async () => await window.ReceiveOpenRequestAsync(path))));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { _openRequests?.Dispose(); _stateSession?.Dispose(); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is QuietRead.MainWindow window && !window.TryFlushReadingData()) e.Cancel = true;
        base.OnSessionEnding(e);
    }
}
