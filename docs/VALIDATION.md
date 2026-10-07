# 验证记录 · QuietRead 0.1.0

验证环境：Ubuntu 24.04.3 LTS，.NET 10.0.12。时间：2026-10-07T10:51:00.5602379Z UTC。

此文件记录首次本地验证，不随每次 CI 运行自动改写。最新提交的 Windows/Linux 核心报告、Windows WPF 冒烟报告及真实窗口图在 [GitHub Actions](https://github.com/lhy8888/epub/actions) 的对应运行产物中；请以实际运行状态判断是否通过。

## 已完成

- 38 项核心、兼容性及安全回归测试通过，0 项失败。
- Windows x64 自包含 Release 发布成功，编译为 Windows PE x64 程序，0 个编译警告/错误。
- 下表 EPUB 的所有正文资源均成功转换为受控正文块；光栅图片通过签名和尺寸检查。没有执行 Windows 图片解码器或 WPF 绘制。

| 文件 | 读取章节资源数 | 目录条目数 | 正文字符数 | 检查通过的位图数 |
| --- | ---: | ---: | ---: | ---: |
| alice.epub | 15 | 16 | 164,532 | 1 |
| moby-dick.epub | 13 | 146 | 1,242,097 | 1 |
| chinese.epub | 18 | 1 | 1,205,140 | 1 |
| QuietRead-Guide.epub | 4 | 4 | 21,630 | 1 |

章节资源数指 EPUB spine 中的文件数，可能与书中实际章数不同；目录锚点可指向同一文件中的多个章节。

真实 EPUB 仅用于兼容性验证，不包含在交付包中。验证来源：[Alice](https://www.gutenberg.org/ebooks/11)、[Moby Dick](https://www.gutenberg.org/ebooks/2701)、[中文测试书](https://www.gutenberg.org/ebooks/23825)。交付包中的阅读指南是本项目原创测试书。

## 核心性能测量

合成书含 1,000 个章节资源，每章 80 段文字；系统文件缓存和 .NET 运行时已预热。此测量不包括解码图片、启动 WPF、创建窗口或排版绘制，不能当作 Windows 11 真机体验数据。

| 项目 | 测量结果 |
| --- | ---: |
| 1000-chapter full-text scan (no matches) | 382.649 ms |
| Open 1000-chapter EPUB, warmed OS/runtime | 中位 13.358 ms；最大 21.059 ms；7 次 |
| Parse one 80-paragraph chapter, cache miss | 中位 0.473 ms；最大 0.617 ms；7 次 |

## 尚未执行

当前环境不是 Windows，无法启动和交互验证 WPF 窗口。本次没有测量 Windows 11 上的冷启动耗时、工作集内存、GPU/CPU 占用、滚动帧率、Windows 解码器行为或多显示器 DPI 切换。没有做代码签名或独立安全审计。

Windows 11 验收应覆盖：打开示例书和自己的 EPUB、拖放、目录锚点、书内链接、搜索、书签删除、关闭后续读、长章节继续下一段、三种主题、外观设置、复制文字、全屏、键盘与鼠标、缩放/DPI 及读取中取消/换书。

## 自动化复现

```powershell
dotnet run --project tests/QuietRead.Tests -c Release -- --benchmark --report docs/core-tests.local.json
```

可额外指定一个或多个本地书籍：

```powershell
dotnet run --project tests/QuietRead.Tests -c Release -- --validate "C:\Books\one.epub" --validate "C:\Books\two.epub" --report docs/core-tests.local.json
```

完整逐项结果和原始测量数据见 `core-tests.json`。测试包含脚本与外链隔离、XXE/实体扩展、路径穿越、重复 ZIP 名称、加密与压缩方式、ZIP64、目录/资源/XML/图片限制、懒加载、缓存、取消、全文搜索和设置损坏恢复及写入顺序。
