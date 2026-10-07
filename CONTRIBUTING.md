# 参与开发

欢迎通过 Issue 报告兼容性问题，通过 Pull Request 提交修改。请先描述具体触发条件和期望行为；安全漏洞按 [SECURITY.md](SECURITY.md) 报告。

## 环境

- Windows 11 x64 适合开发和人工验收 WPF 界面。Linux 可开发/运行核心、编译 Windows 目标。
- 使用 `global.json` 中的 .NET SDK；当前是 10.0.401，允许同一功能带的补丁更新。
- Python 3.11 或更新版本用于示例书、仓库检查和 ZIP 打包；脚本只用标准库。
- 不需要安装第三方 NuGet 包或 Node.js。

## 开发流程

```powershell
git clone https://github.com/lhy8888/epub.git
cd epub
dotnet restore QuietRead.sln --locked-mode
dotnet build QuietRead.sln -c Release --no-restore
dotnet run --project tests/QuietRead.Tests -c Release -- --validate samples/QuietRead-Guide.epub --report docs/core-tests.local.json
dotnet format whitespace QuietRead.sln --no-restore --verify-no-changes
python tools/check_repository.py
python tools/check_dependencies.py
```

Windows 界面测试与便携包：

```powershell
dotnet run --project tests/QuietRead.UiTests -c Release -- samples/QuietRead-Guide.epub docs/ui-tests.local.json artifacts/windows-smoke.png
.\tools\Build.ps1
```

这两个测试项目使用独立控制台测试程序，必须通过 `dotnet run` 执行。测试有失败时返回非零退出码；请勿将 `dotnet test` 成功当作已经执行回归测试。

修改 C# 后可运行 `dotnet format whitespace QuietRead.sln --no-restore` 修复格式。项目启用警告即错误、安全类别分析器、直接与传递 NuGet 漏洞审计。请为实际行为变化补充有意义的测试。

## 安全与性能约束

- EPUB 属于不可信输入；保留 ZIP/XML/文字/图片预算与路径校验。
- 界面只接收受控的正文模型，不增加脚本执行、动态 XAML、任意 URL 打开或自动联网。
- 解析、解码和搜索放在工作线程；任何新异步操作都要处理取消、窗口关闭和换书。
- 测试与示例书只使用原创或允许公开分发的内容；不要上传自己的付费 EPUB、阅读记录或私钥。
- 当前无显式 NuGet 包引用。新增依赖需要说明用途、许可证和供应链影响，并同步调整源码检查政策与锁文件。

## 更新运行时与发布

1. 更新 `global.json` 到受支持的稳定 SDK，检查 .NET 官方安全公告。
2. 在项目根目录执行 `dotnet restore QuietRead.sln --force-evaluate`，提交更新后的 `packages.lock.json`。若只有 Windows 目标包变化，应在 Windows 和 Linux 均验证锁定还原。
3. 重新发布 Windows x64；将对应 `Microsoft.NETCore.App` 与 `Microsoft.WindowsDesktop.App` 版本的官方许可证/第三方声明更新到 `licenses/`。打包器会拒绝缺少对应版本声明的包。
4. 同步 `Directory.Build.props`、`app.manifest`、界面版本信息、CHANGELOG 和 `docs/RELEASE-NOTES.md`。
5. 等待主分支 Build and checks、CodeQL 检查通过，处理 Security 中的扫描发现，再完成 Windows 11 真机验收。
6. 推送与项目版本一致的标签，例如 `git tag v0.1.0` 与 `git push origin v0.1.0`。标签触发再次编译和测试；只有全部构建检查成功，才创建 GitHub Release 和上传 ZIP/哈希。

Actions 默认只授予读取仓库权限，只有版本标签的发布任务可写 Releases，只有 CodeQL 任务可写扫描结果。所有 Action 固定完整 SHA，并交由 Dependabot 提交更新 PR。不要用 `pull_request_target` 执行 PR 代码。

## 合并检查

建议将 `All build checks` 与 `All security scans` 设置为主分支保护的必需检查。仓库保护规则由维护者在 GitHub Settings 中管理；工作流文件本身不会自动启用分支保护。格式、依赖审计和测试必须通过，CodeQL 的发现需单独评估和处理。

PR 中说明：问题如何触发、修改后的行为、已运行的验证以及仍存在的限制。提交生成文件、运行时或大体积电子书会被仓库检查拒绝。
