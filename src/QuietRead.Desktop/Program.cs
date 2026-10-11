using Avalonia;
using QuietRead.Cross;

namespace QuietRead.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--smoke") { ReaderApp.StartupFile = args[1]; ReaderApp.SmokeReport = args[2]; }
        else if (args.Length == 1) ReaderApp.StartupFile = args[0];
        return AppBuilder.Configure<ReaderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}
