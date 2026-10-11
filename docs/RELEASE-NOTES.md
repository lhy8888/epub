本地、离线、无账号的 EPUB 2/3 阅读器。各平台从 1.0.0 开始独立发布；同一平台的两种 CPU 架构放在同一个发布页。

支持简体中文 / English 菜单即时切换、目录与书内链接、续读与书签、搜索、图片、主题与阅读外观、高亮笔记、本地书库、JSON 备份迁移和 Markdown 摘录。Windows 保留 WPF，其他平台使用共享 Avalonia 界面；具体功能边界见 README。

包含本轮 3 个 P1、4 个 P2 修复与设置文件读取加固：限制重复图片解码和密集文本渲染、保护导出目标及已有备份、恢复取消后的页面、正确反向翻页、校验搜索结果归属。手机导出请使用新文件名。

验证包括 103 项核心检查、45 项 Windows WPF 检查、27 项 Linux/macOS 共享界面检查（Windows 25 项），以及发布包实际启动、手机包结构检查和 CodeQL。手机真机性能、触摸和原生文件选择器尚未验收。

每个附件都有同名 `.sha256` 文件。下载后核对 SHA-256；校验值用于内容完整性。桌面二进制未进行平台签名；Android 开发签名与 iOS 未签名构建的安装和升级边界见本发布页顶部。

暂不支持完整原书 CSS、嵌入字体、复杂 SVG、DRM、固定版式、PDF/MOBI 或云同步。升级前建议在书库导出 JSON；备份不包含 EPUB，请另行保存书籍。

[使用说明](https://github.com/lhy8888/epub#readme) · [完整版本记录](https://github.com/lhy8888/epub/blob/main/CHANGELOG.md) · [复审修复与验证范围](https://github.com/lhy8888/epub/blob/main/docs/CROSS-PLATFORM-REAUDIT.md)
