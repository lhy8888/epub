# P0 阅读功能与验收

本轮依据 Readest/calibre 的功能设计独立实现六项 P0，未复制其 AGPL/GPL 应用代码，未引入新 NuGet 依赖。

| 功能 | 实现 | 自动验收 |
| --- | --- | --- |
| 精确续读 | 逻辑段/字符偏移＋有界文本校验，原生 TextPointer 映射，按文字位置去重书签，旧比例回退 | 核心改变人工拆块、移动/重复文本、Unicode；Windows 字号/宽度/窗口变化、近距离书签及重开后原摘录可见 |
| 前后返回 | 64 项主动跳转历史，普通滚动/翻页不入栈，新分支清空前进 | Windows 目录/章节跳转、前后退与分支替换 |
| 脚注 | EPUB type/ARIA role 识别、跨章受限文字预览、原文跳转 | 核心范围/限额/无外部和活动链接；Windows 正文位置不变、正文自动化、关闭焦点及旧任务取消 |
| 搜索 | 物理资源进度、停止、前后结果、返回查询前位置、字符偏移 | 核心重复 spine 进度和取消；Windows 完成进度、结果按钮、返回原摘录、新查询不被旧搜索覆盖 |
| 文件转交 | 状态独占＋当前用户命名管道；8 KiB/期限/路径与版本校验 | 核心独立进程转交、超大消息拒绝/恢复；Windows 窗口接收；便携 EXE 两次真实启动 |
| 无障碍 | 原生 TextPattern、LiveRegionChanged、焦点返回、可见焦点、系统高对比 | Windows 读取正文及脚注 DocumentRange/VisibleRanges；控件命名、键盘焦点；真机读屏仍需人工验收 |

代码入口：Core 的 `ReadingLocator.cs`、`OpenRequestChannel.cs`、脚注/搜索接口；App 的 `DocumentPosition.cs`、`MainWindow.Reading.cs` 和现有导航入口。

阅读位置仍依赖同一本书的文件身份。没有在 P0 中加入改名后的跨路径关联、批注或长期书库。图片只能按块定位，无法定位图中某个像素；位置校验失配且文本有歧义时使用原记录回退，不猜测句子。

状态 schema 1 可读，写出 schema 2；字段均有界，满额 Unicode 记录上限提升至 4 MiB。旧版程序不认识新版状态，因此升级后不应使用旧版覆盖同一状态目录。

Windows CI 检查原生 UI 和已打包 EXE；Linux 只能执行 Core 和交叉编译。Windows 11 人工验收应使用 Narrator/NVDA 从正文连续阅读，检查中英混排、链接、注释关闭焦点、搜索完成播报、系统高对比及多 DPI。自动化 TextPattern 检查不代表已完成所有读屏产品的实际体验验收。
