# Cross-platform dependencies

The Windows WPF reader and QuietRead.Core use the .NET platform. The shared reader uses the following direct packages, pinned to 11.3.22 and covered by committed NuGet lock files, NuGet vulnerability auditing and CodeQL:

- Avalonia, Avalonia.Themes.Fluent, Avalonia.Desktop, Avalonia.Android, Avalonia.iOS: MIT; https://github.com/AvaloniaUI/Avalonia
- Avalonia.Headless: MIT; test runner only.

Transitive dependencies include Avalonia.BuildServices, Avalonia.Remote.Protocol, MicroCom.Runtime, SkiaSharp, HarfBuzzSharp, native Skia/HarfBuzz assets, and AndroidX support libraries on Android. Exact versions and integrity hashes are in each project's `packages.lock.json`. NuGet restore must use these locks; adding another direct dependency requires updating `tools/check_repository.py`, this document and the relevant lock files.

Avalonia is Copyright (c) AvaloniaUI OÜ. All Rights Reserved. MicroCom is Copyright (c) MicroCom contributors. SkiaSharp is Copyright (c) 2015-2016 Xamarin, Inc., Copyright (c) 2017-2018 Microsoft Corporation. HarfBuzzSharp is Copyright (c) Xamarin, Inc. and Microsoft Corporation.

MIT license:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Native components retain their upstream licensing: Skia and HarfBuzz have BSD/MIT-compatible notices; AndroidX uses Apache-2.0. .NET redistribution notices are included with self-contained runtime output. Complete native notices and source are maintained at https://github.com/mono/SkiaSharp, https://github.com/google/skia, https://github.com/harfbuzz/harfbuzz and https://android.googlesource.com/platform/frameworks/support/. Platform packages include this notice; mobile release packaging must retain the dependency notices.
