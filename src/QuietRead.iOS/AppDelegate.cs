using Avalonia.iOS;
using Foundation;
using QuietRead.Cross;
namespace QuietRead.iOS;
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<ReaderApp>
{
    public AppDelegate()
    {
        // AvaloniaAppDelegate implements IUIApplicationDelegate on UIResponder;
        // its deactivation event is raised by the UIKit background notification.
        ((IAvaloniaAppDelegate)this).Deactivated += (_, _) => ReaderApp.ActiveView?.PersistForBackground();
    }
}
