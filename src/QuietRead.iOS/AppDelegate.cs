using Avalonia.iOS;
using Foundation;
using QuietRead.Cross;
using UIKit;
namespace QuietRead.iOS;
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<ReaderApp>
{
    public override void DidEnterBackground(UIApplication application)
    {
        ReaderApp.ActiveView?.PersistForBackground();
        base.DidEnterBackground(application);
    }
}
