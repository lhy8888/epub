using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace QuietRead.Cross;

public sealed class ReaderApp : Application
{
    public static ReaderView? ActiveView { get; private set; }
    public static string? StartupFile { get; set; }
    public static string? SmokeReport { get; set; }
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        Control content;
        try { ActiveView = new ReaderView(new ReaderSession()); content = ActiveView; }
        catch (Exception error)
        {
            content = new TextBlock { Text = error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) };
            if (SmokeReport != null) throw;
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "QuietRead", Width = 1120, Height = 800, MinWidth = 360, MinHeight = 480, Content = content };
            window.Closing += (_, e) =>
            {
                if (ActiveView == null) return;
                if (!ActiveView.TryClose()) e.Cancel = true;
            };
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activities && ActiveView != null)
        {
            ReaderSession session = ActiveView.Session;
            activities.MainViewFactory = () => ActiveView = new ReaderView(session);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime mobile) mobile.MainView = content;
        base.OnFrameworkInitializationCompleted();
    }
}
