# QuietRead · 静读

一个注重安全、性能与低内存的 **Windows 11 EPUB 阅读器**。

本地离线 · 原生界面 · 解压即用 · 无账号 · 无广告

[![Build](https://github.com/lhy8888/epub/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/lhy8888/epub/actions/workflows/ci.yml)
[![Security](https://github.com/lhy8888/epub/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/lhy8888/epub/actions/workflows/security.yml)

![QuietRead 阅读界面](docs/assets/reader.png)

## 舒服地读一本书

- **清爽界面**：正文居中，目录可以收起，常用功能直接点击。
- **阅读外观**：纸白、暖色、夜间三种背景，自由调整字体、字号、行距与宽度。
- **随时继续**：自动保存位置，最近阅读、书签、全书搜索与全屏阅读。
- **简单操作**：打开或拖入 EPUB，用滚轮和底部翻页按钮阅读，无需记忆快捷键。

## 安全地打开书籍

不执行书内脚本，不加载外部图片、字体或网页，不修改原 EPUB。读取前检查 ZIP、XML 和图片，并限制复杂度与资源大小。

程序以普通用户权限运行，阅读记录只保存在本机，没有遥测或书籍上传。

## 为轻量阅读设计

按需读取章节，长章节分段显示。最多缓存两章，并限制缓存、正文与图片预算；搜索逐章扫描，复用文字缓冲区。解析、解码和搜索在后台执行，外观调整复用正文。

## 下载与使用

1. 打开 [下载页面](https://github.com/lhy8888/epub/actions/workflows/ci.yml)，选择 `main` 最近一次成功构建，在 **Artifacts** 下载 `QuietRead-Windows-x64`。
2. 解开下载包，再把其中的便携 ZIP **完整解压**，双击 `QuietRead.exe`。无需安装额外运行时。
3. 点击「打开」或拖入本地 `.epub`。包内附有阅读指南，可直接体验。

开发版下载需要登录 GitHub。当前提供 Windows x64 便携包，尚未数字签名；[正式版本](https://github.com/lhy8888/epub/releases)发布后会出现在 Releases。

## 支持哪些书

支持可重排 EPUB 2/3、中英文、基础文字排版、目录内链及 JPEG / PNG / GIF 首帧 / BMP 图片。适合小说和一般文字书。

采用简化排版；暂不支持原书 CSS、复杂 SVG、音视频、DRM 与固定版式。安全设计和内存预算的具体边界见下方文档。

[开发指南](CONTRIBUTING.md) · [设计与检查](docs/ARCHITECTURE.md) · [代码审查](docs/SECURITY-REVIEW.md) · [安全政策](SECURITY.md) · [版本记录](CHANGELOG.md) · [反馈问题](https://github.com/lhy8888/epub/issues/new/choose)

源码采用 [MIT](LICENSE) 许可证。
