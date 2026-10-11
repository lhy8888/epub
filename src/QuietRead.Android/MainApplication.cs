using Android.App;
using Android.Runtime;
using Avalonia.Android;
using QuietRead.Cross;

namespace QuietRead.Android;

[Application]
public sealed class MainApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<ReaderApp>(javaReference, transfer);
