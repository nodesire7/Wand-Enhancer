# GBTGAME CLI 与一键安装

用户交付文件只有 `WandSetup.exe`，已内置同一次构建的增强器与 CLI；完整离线版还内置 Wand 12.68.0 安装包。无需同目录的 WandEnhancer.exe，`WandSetup.exe --cli ...` 可直接使用内置 CLI。增强器版本来自上游 AssemblyVersion；JSON 同时返回源码构建号 `build`，CLI 协议版本为 `1`。

安装助手直接复用 WandEnhancer 的颜色、按钮、勾选框和下拉框资源。与原增强器日志区一样，滚动条隐藏，保留滚轮滚动。状态区压缩为短进度条，主区域展示增强设置。作者 **A-FEI-** 与论坛 [bbs.gbtgame.me](https://bbs.gbtgame.me) 保留。

## 用户使用

完整离线版只需双击 `WandSetup.exe`，点击 **安装**。默认执行：

1. 校验原版安装包 SHA256、版本和内置 CLI 协议。
2. 备份当前用户的 Wand / WeMod Roaming 配置及相关 HKCU 注册表。
3. 关闭默认安装目录内的进程，静默移除默认目录、相关快捷方式及已备份的注册表项。不会启动原版卸载器或客户端卸载事件，不需要点击卸载确认框。
4. 静默安装固定版本 Wand 12.68.0，等待安装进程退出和文件写入稳定。
5. 调用内置 CLI 应用界面选定的配置，并检查修改状态。默认 Static、Pro 和禁止更新；可选择 Supervised、F12 开发工具、远程访问、更新后自动应用和自定义 `.js` 脚本。
6. 启动 Wand。可取消勾选“完成后启动 Wand”。

开始前保存游戏进度。清理会退出原有登录状态。清理范围为当前用户 `%LOCALAPPDATA%\Wand`、`%LOCALAPPDATA%\WeMod` 和相应 Roaming 目录；不扫描清理系统注册表。遇到系统级安装、自定义安装位置或目录链接，会停止并说明原因，需先手动卸载。这是应用专用清理，不依赖 Geek，也不宣称覆盖 Geek 的全部扫描能力。

备份与日志保存在 `%LOCALAPPDATA%\GBTGAME\WandSetup\`。备份保存配置和注册表快照，不保存旧版程序文件；注册表 JSON 含值类型，供人工检查与恢复。修改失败时由原有增强器负责文件回滚，安装助手停止且不启动 Wand；旧版卸载和重装之间不提供整机事务回滚。

CI 发布的轻量版内置 CLI，原版安装包需放在 `WandSetup.exe` 同目录。安装包固定为 `package.json` 指定的文件及哈希；不自动下载第三方镜像，不自动安装其他版本。

## CLI

PowerShell 对 GUI EXE 应使用 `Start-Process -Wait` 或 .NET Process 等方式等待退出。自动化调用请重定向 stdout，以 UTF-8 读取。每次调用输出一个 JSON 对象，日志位于 `logs` 数组；CLI 失败不会打开图形界面。

```powershell
$p = Start-Process .\WandEnhancer.exe -ArgumentList '--cli version' -Wait -PassThru -NoNewWindow -RedirectStandardOutput result.json
Get-Content -Raw -Encoding UTF8 result.json | ConvertFrom-Json
$p.ExitCode
```

```text
WandEnhancer.exe --cli help
WandEnhancer.exe --cli version
WandEnhancer.exe --cli status --path "C:\Users\name\AppData\Local\Wand"
WandEnhancer.exe --cli patch --path "C:\Users\name\AppData\Local\Wand" --expected-version 12.68.0 --patches pro,disable-updates --strategy static
WandEnhancer.exe --cli restore --path "C:\Users\name\AppData\Local\Wand"
WandEnhancer.exe --cli launch --path "C:\Users\name\AppData\Local\Wand" --strategy static
```

`--path` 接受 Squirrel 根目录、`app-*` 版本目录或 EXE；省略时调用已有安装检测。`--expected-version` 在任何修改前检查版本。`patch` 默认 `pro,disable-updates`，策略默认 `static`；可选补丁 `devtools,remote-panel`，可选策略 `supervised`。保持自动更新时用 `--patches pro --auto-apply`；它与 `disable-updates` 互斥。`launch` 对仅修改时未保存的 Supervised 配置，应明确传入 `--strategy supervised`。

上述命令中的 `WandEnhancer.exe` 均可直接替换成 `WandSetup.exe`，无需外置增强器。Static 启动后 CLI 立即退出；直接调用 CLI 的 Supervised 模式会监控到 Wand 退出。安装助手选择 Supervised 时，通过安装目录中的持久启动器运行，安装界面不会等待 Wand 退出。

界面会避免同时启用“禁用更新”与“更新后自动应用”。添加脚本会启用远程访问；脚本在清理旧版前缓存，在修改前复制到安装根目录的 `WandEnhancerScripts`，供更新后重打使用。界面设置先做快照，再通过 `--config` JSON 传给内置 CLI，后续界面变化不会改动当前安装任务。

```text
WandSetup.exe --cli patch --path "C:\Users\name\AppData\Local\Wand" --config options.json
```

`--config` 使用原版 PatchConfig 结构：Strategy（Static=1、Supervised=0）、PatchTypes（Pro=1、禁止更新=2、F12=8、远程访问=16）、AutoApplyAfterUpdate、CustomScriptPaths。它与 `--patches`、`--strategy`、`--auto-apply` 互斥；脚本必须为现有 `.js` 文件，且需要远程访问选项。

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 3 | 未找到安装 |
| 4 | 版本不匹配 |
| 5 | 操作失败 |
| 6 | 同一安装已有 CLI 操作 |

`WandSetup.exe --check` 只校验载荷和当前安装边界，不卸载、不安装、不修改现有 Wand。仍需通过重定向 stdout 的方式等待并读取 JSON。

## 构建

沿用仓库要求的 Node、pnpm、Visual Studio MSBuild 和 .NET Framework 4.8 targeting pack。

```powershell
.\build.ps1 -Configuration Release
.\scripts\build-distribution.ps1 -WandInstallerPath 'F:\Soft\Wand\Wand-12.68.0.exe'
.\scripts\test-fork.ps1
.\scripts\test-installer.ps1
```

输出 `dist\Release\WandSetup.exe` 为完整单文件安装工具，交付目录不再放独立增强器 EXE，另附中文说明与 SHA256SUMS。独立开发者 CLI 仍可在 `WandEnhancer/bin/Release` 获取。省略 `-WandInstallerPath` 构建需外置原版安装包的轻量版，增强器始终内置。修改 `Fork/Installer/package.json` 的版本、文件名与 SHA256 可切换目标安装包；必须先验证兼容性及静默行为。

`sync-upstream.yml` 继续定时合并上游。同步时保护 `Fork/` 和分支专用脚本与工作流，再通过 `reapply-fork.ps1` 补回 MSBuild 入口导入。上游原始 `Program.Main` 无需修改。同步构建后同时发布 CLI 和轻量安装助手。若上游接口出现不兼容，构建或测试会失败，不发布新安装助手。

## 验证范围

`build.ps1` 执行上游完整检查；`test-fork.ps1` 使用临时假安装验证真实子进程 JSON、退出码、中文空格路径、版本拦截、修改状态、清理边界、junction 拒绝与内置 CLI 一致性。不在开发者真实安装上测试卸载或修改。

本次另用提供的 12.68.0 原版安装包解出的临时副本进行了 Static 修改、全部顶层 JavaScript 语法检查与恢复校验：修改成功，恢复后全部 233 个原始文件 SHA256 一致。测试使用独立品牌名，避免停止真实 Wand 进程。

对真实 12.68.0 的首次安装、旧版卸载重装及游戏功能，发布前需在一次性 Windows 虚拟机中验收；`--silent` 的无首启行为依据 Squirrel 的安装流程，实际捆绑版本仍需该验收。自动化测试不能证明 Pro 账号状态或所有游戏效果。

## 隔离验收与完整测试

`test-installer.ps1` 检查 30 组安装编排、失败拦截、参数快照、脚本缓存和持久化、配置传递、更新监控进程输出隔离、静默移除、快捷方式、独立启动及窗口状态；另验证单独 EXE 在空目录调用内置 CLI、轻量版缺失／损坏载荷和进程互斥。窗口测试检查共享调色板、隐藏滚动条、紧凑状态区、820×680 最小尺寸与 125% / 150% / 200% 渲染。按钮测试触发真实窗口事件处理器，用假安装会话验证运行中保护、错误恢复与成功关闭，不运行真实安装。逐项记录见 [UI-TEST-REPORT.md](UI-TEST-REPORT.md)。运行这些检查前需关闭安装助手窗口，避免占用进程互斥锁。

`test-real-package.ps1 -BundleDirectory <原版 app 目录>` 只操作复制到临时目录的副本，覆盖 Static / Supervised 两种策略、四项补丁、JavaScript 和远程桥语法，以及恢复后的全部文件哈希。不要把专有解包文件提交到仓库。

本机没有 Windows Sandbox，用户选择先完成安全测试并提供隔离脚本。可在支持且已启用 Windows Sandbox 的电脑生成验收环境：

```powershell
.\scripts\new-test-sandbox.ps1 -OutputDirectory 'C:\WandAcceptance'
```

使用完整打包版；轻量版还需指定 `-WandInstallerPath <原版安装包>`。双击生成的 `Wand-Acceptance.wsb`，沙盒将自动执行首次安装、旧版卸载重装、配置与注册表备份清理、CLI 恢复重打和启动检查。载荷映射只读、网络关闭，只有新建的 results 目录可回写宿主。结果保存在 `results/result.json` 与 `acceptance.log`；沙盒关闭后内部安装消失。脚本拒绝在宿主用户或非指定沙盒路径运行。

安装助手提供 `--install [--no-launch]` 供无人值守部署与隔离验收使用，**会执行真实卸载和重装**；请勿在宿主机上把它当成只读测试。`--check` 仍为只读校验。测试结果及尚未覆盖的项目见 [TEST-REPORT.md](TEST-REPORT.md)。
