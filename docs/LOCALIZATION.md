# 中英文界面

点击「Aa」，在「界面语言 / Interface language」中选择「简体中文」或「English」。无需重启；下次打开保留选择。默认中文，未知或损坏的语言值回退中文。

切换覆盖主窗口、侧栏、外观设置、书库、帮助、应用确认提示、搜索/保存状态、错误说明和无障碍名称。Windows 原生文件对话框、标准消息框的系统按钮及 OS/runtime 诊断仍使用 Windows 自身语言。

语言为全局设置，保存在 `%LOCALAPPDATA%\QuietRead\state.json` 的 `InterfaceLanguage`，值为 `zh-CN` 或 `en`；沿用 schema 2，旧文件缺少此字段时仍可读取。单书外观和 JSON 书籍备份不携带语言设置。

书名、作者、正文、脚注、标签、系列、书签原标签、摘录及用户笔记保留原文。Markdown 导出中的作者/章节/书签说明使用当前语言，导出不会改变阅读数据。整个功能在本机执行，无需账号或网络。

## 实现与维护

- `QuietRead.Core/Interface.en.json`：以中文文案为键的英文资源，共 332 项，明确嵌入主程序集，避免 `.en` 文件名被 SDK 当作卫星资源。
- `InterfaceText`：显式传入语言格式化；不修改进程/线程文化，不翻译参数中的书籍文字。已知应用错误和带资源路径/字段名的错误可本地化，系统诊断保留原信息。
- `UiText` / `LocalizeExtension`：静态菜单、工具提示及无障碍名称使用单向绑定。动态提示保留文案键和参数，语言变化后重新显示；用户输入框与正文模型不参与翻译。
- 书库行状态单独刷新，保留未保存的元数据编辑；关闭书库后解除语言事件订阅。
- `python tools/check_localization.py`：检查 XAML 和应用 C# 文案覆盖。新增格式模板需同时维护两个语言中的参数。

新增 XAML 文案使用 `Text="{local:Localize Key='中文文案'}"`；动态 TextBlock 使用 `UiText.Text(target, "中文 {0}", value)`。正文及用户数据作为参数传入，不把它们当作资源键。错误使用 `UiText.Error(message)` 参数或 `UiText.GetError(message)`。

## 验证

新增 5 项核心回归：语言保存及旧状态、完整资源参数、并发格式化与原文保护、错误诊断、双语 Markdown 导出。新增 5 项 WPF 回归：即时切换和最小窗口布局、正文/位置/脚注/笔记草稿保留、搜索及错误切换、书库编辑保留、关闭重开与单书外观隔离。

当前合计 100 项核心和 45 项 WPF 回归。Windows Actions 产物 `windows-ui-report` 包含中文 `windows-smoke.png` 和英文 `windows-language-en.png`；通过状态以对应提交运行结果为准。

Linux 可验证核心和交叉编译，实际 WPF 行为由 Windows CI 验证；Windows 11 真机读屏、多 DPI 和系统原生对话框体验仍需人工验收。
