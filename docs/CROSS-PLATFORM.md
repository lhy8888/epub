# 跨平台架构与构建

QuietRead.Core 继续负责 EPUB 安全解析、搜索、文字定位、书库和 JSON 数据。QuietRead.App 保留 Windows WPF 界面。QuietRead.Cross 提供 Avalonia 共享阅读界面和 ReaderSession，QuietRead.Desktop、QuietRead.Android、QuietRead.iOS 分别提供宿主入口。语言资源沿用 Core 中的同一份中英文文案。

主解决方案包含可在 Linux/Windows 还原和编译的项目；移动宿主单独编译，避免普通桌面开发被 iOS/Android SDK 阻塞。核心和共享库始终编译为可移植 net10.0，不继承宿主的 RuntimeIdentifier；宿主声明完整的自身 RID 集合，因此锁文件可跨机器复用。所有平台保持无账号、无同步服务、无遥测，不请求 Android Internet 或全盘存储权限。

## 支持范围

共享界面包括 EPUB 导入、图片及粗体/斜体/代码文字、目录、书内链接、脚注文字预览、续读、书签、可取消的全书/当前章搜索、大小写/整词、单段文字选区高亮与笔记编辑/定位/删除、元数据、全库/单书 JSON 导出与冲突选择导入、Markdown、纸白/暖色/夜间、字号/行距/正文宽度、单书外观和语言持久化。

小于 800 DIP 的窗口采用上下阅读/工具区域，工具面板可关闭；工具栏和底部按钮按可用宽度自动换行。正文分段共同限制为 180 个块、40,000 字符和 2500 个原文 inline；分段、导航、续读与 WPF 使用同一算法。每个分段的图片尝试输入累计限制为 24 MiB，重复引用共享一个 Bitmap，按实际解码尺寸检查唯一图片累计不超过 400 万像素。准备新分段时保留旧页面，短暂同时存在两个有界分段；这不是整个进程的内存上限。安全边界由同一 Core 执行，脚本、远程资源和外部链接不会执行或加载。

共享版按顶部可见段落保存续读位置；单条高亮限定当前段落。高级首行缩进、对齐/字体设置、多段选区、目录折叠、搜索结果文字标记、全屏、文件拖放、第二实例请求转交以及 WPF UI Automation 文本接口目前由 Windows WPF 版提供。共享界面的原生文件选择器按钮跟随系统语言，应用菜单和状态跟随 Aa 中的语言设置。

## 本机数据和导入

桌面默认数据路径为 `Environment.SpecialFolder.LocalApplicationData/QuietRead`：Windows 为 `%LOCALAPPDATA%\QuietRead`，macOS/Linux 路径由 .NET 根据本机环境解析。Android/iOS 使用应用沙箱的本机数据目录。选择 EPUB 后按流复制到 `epubs/<SHA256>.epub`，最多 512 MiB；校验 ZIP 和首章后才原子提交，失败/取消会删除临时文件，重复导入复用同内容文件。

书库移除只删除阅读数据，保留导入的 EPUB。JSON 不包含 EPUB 或原设备路径，跨设备迁移需要另行提供同内容 EPUB，重新导入后依据文件指纹恢复阅读数据。语言属于当前设备的全局设置，不随单书 JSON 迁移。

导出验证实际扩展名，并保护书籍和应用数据目录。桌面本地路径在同目录暂存、刷新后原子替换；手机文档提供器没有通用原子替换能力，因此保留已有非空文件，只写新文件，失败时尝试删除不完整产物。请选择新文件名；清理失败会显示需要手工删除的提示。

修改立即调用原有原子存储，滚动位置延迟 750 ms 保存；桌面关闭、Android OnStop、iOS 后台停用回调刷新最后位置。取消加载时保留旧页面，Android OnResume / iOS 激活回调补绘未完成的页面；旧视图退出时取消后台工作并释放图片。上一页跨分段或章节进入末屏，换书后清空旧搜索结果。保存失败显示具体错误并提供重试；桌面保存未成功时保留窗口。

## 开发命令

使用 global.json 的 SDK 10.0.401，Python 3.11+。

```bash
# 普通开发：不需要移动工作负载
dotnet restore QuietRead.sln --locked-mode
dotnet build QuietRead.sln -c Release --no-restore
dotnet run --project tests/QuietRead.CrossTests -c Release -- artifacts/cross-tests.json

# macOS / Linux 桌面
dotnet run --project src/QuietRead.Desktop -c Release
python3 tools/package_desktop.py --rid linux-x64
python3 tools/package_desktop.py --rid osx-x64
python3 tools/package_desktop.py --rid osx-arm64
```

桌面产物在 `artifacts/desktop-<rid>/`，包括 SHA-256。macOS 包保持可执行权限，解开 tar.gz 得到 `.app`；正式分发需 Developer ID 签名和公证。Linux 自包含包仍需要 X11 图形环境、libfontconfig、libX11 和系统字体。

```bash
# Android：Android SDK API 36 / build-tools 36，JDK 21
dotnet workload install android --skip-manifest-update
dotnet restore src/QuietRead.Android --locked-mode
dotnet publish src/QuietRead.Android -c Release --no-restore -p:JavaSdkDirectory=/path/to/jdk21 -p:AndroidKeyStore=false -o artifacts/android
python3 tools/check_mobile_package.py android artifacts/android

# iOS：macOS、Xcode 26.3，集合 10.0.106 含 iOS SDK 26.2.10233，目标 net10.0-ios26.2
dotnet workload install ios --version 10.0.106
dotnet workload --version # 应输出 10.0.106
dotnet restore src/QuietRead.iOS --locked-mode
dotnet build src/QuietRead.iOS -c Release --no-restore -r iossimulator-arm64 -p:EnableCodeSigning=false
dotnet build src/QuietRead.iOS -c Release --no-restore -r ios-arm64 -p:EnableCodeSigning=false
```

开发 APK 使用 .NET Android 的开发密钥，可以侧载；不同 CI 机器的开发密钥可能不同，不能用作稳定升级签名。正式 Android 发布需要固定 keystore。iOS 设备应用未经签名，只用于证明代码与原生资源编译成功；安装到真机、IPA/TestFlight/App Store 发布需 Apple Developer 证书、团队和 provisioning profile，项目没有内置任何证书或密钥。

## 验证与证据

- 当前 103 项核心和 45 项 WPF 回归继续执行。
- Linux/macOS 27 项、Windows 25 项跨平台测试检查导入、持久化、双语与布局，并使用真实 Skia 解码验证图片共享与预算；新增导出保护、取消/重新激活、视图重建、密集文本分段完整性、反向翻页和搜索归属检查。两项 Unix 特有测试验证目录权限和符号链接保护。细节见 [本轮复审修复](CROSS-PLATFORM-REAUDIT.md)。
- Platform builds 在 macOS Intel/ARM64、Linux x64 执行核心和共享界面测试，打包后启动真实程序、打开示例并保存窗口截图和 JSON 报告。
- Android 验证 APK 的 ZIP CRC、ARM64/x64 原生运行时、全部原生库的 16 KB LOAD 段对齐和离线权限；iOS 验证模拟器/设备 `.app` 的标识和可执行文件。
- 编译或 APK/.app 结构检查不等于手机实机测试；系统文件选择器、触摸选区、后台恢复和真机性能还需安装对应包进行设备验证。测试结论以具体提交的 Actions 结果为准。
