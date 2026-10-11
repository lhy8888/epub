using Android.App;
using Android.Content.PM;
using Avalonia.Android;
using QuietRead.Cross;

namespace QuietRead.Android;

[Activity(Label = "QuietRead", Theme = "@style/QuietReadTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnResume()
    {
        base.OnResume();
        _ = ReaderApp.ActiveView?.ResumeFromBackgroundAsync();
    }
    protected override void OnStop()
    {
        // UI mutations are saved immediately; flush the last debounced scroll before suspension.
        ReaderApp.ActiveView?.PersistForBackground();
        base.OnStop();
    }
}
