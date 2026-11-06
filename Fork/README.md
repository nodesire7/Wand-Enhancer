# GBTGAME CLI 与一键安装

本分支保留原有图形界面，并在同一个 `WandEnhancer.exe` 中提供 CLI。安装助手 `WandSetup.exe` 内置同一次构建的 CLI，避免调用错版本。增强器版本继续来自上游 AssemblyVersion；JSON 同时返回源码构建号 `build`，CLI 协议版本另为 `1`。

安装助手采用现代 WPF 深色界面，固定显示作者 **A-FEI-** 与论坛 [bbs.gbtgame.me](https://bbs.gbtgame.me)。侧栏展示六个步骤，失败时展开日志并允许重试。

## 用户使用

完整离线版只需双击 `WandSetup.exe`，点击 **安装**。默认执行：

1. 校验原版安装包 SHA256、版本和内置 CLI 协议。
2. 备份当前用户的 Wand / WeMod Roaming 配置及相关 HKCU 注册表。
3. 关闭默认安装目录内的进程，静默移除默认目录、相关快捷方式及已备份的注册表项。不会启动原版卸载器或客户端卸载事件，不需要点击卸载确认框。
4. 静默安装固定版本 Wand 12.68.0，等待安装进程退出和文件写入稳定。
5. 调用 CLI，以 Static 策略应用 Pro 和禁止更新，检查修改状态。
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

Static 启动后 CLI 立即退出，应用不会继承 JSON 输出管道；Supervised 模式需要持续监控，CLI 会一直运行到 Wand 退出。安装助手使用 Static 模式。

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

输出 `dist\Release\WandSetup.exe` 为完整单文件安装工具，另提供 `WandEnhancer.exe`、中文说明与 SHA256SUMS。省略 `-WandInstallerPath` 构建需外置原版安装包的轻量版。修改 `Fork/Installer/package.json` 的版本、文件名与 SHA256 可切换目标安装包；必须先验证其兼容性及静默行为，不能只改文件名。

`sync-upstream.yml` 继续定时合并上游。同步时保护 `Fork/` 和分支专用脚本与工作流，再通过 `reapply-fork.ps1` 补回 MSBuild 入口导入。上游原始 `Program.Main` 无需修改。同步构建后同时发布 CLI 和轻量安装助手。若上游接口出现不兼容，构建或测试会失败，不发布新安装助手。

## 验证范围

`build.ps1` 执行上游完整检查；`test-fork.ps1` 使用临时假安装验证真实子进程 JSON、退出码、中文空格路径、版本拦截、修改状态、清理边界、junction 拒绝与内置 CLI 一致性。不在开发者真实安装上测试卸载或修改。

本次另用提供的 12.68.0 原版安装包解出的临时副本进行了 Static 修改、全部顶层 JavaScript 语法检查与恢复校验：修改成功，恢复后全部 233 个原始文件 SHA256 一致。测试使用独立品牌名，避免停止真实 Wand 进程。

对真实 12.68.0 的首次安装、旧版卸载重装及游戏功能，发布前需在一次性 Windows 虚拟机中验收；`--silent` 的无首启行为依据 Squirrel 的安装流程，实际捆绑版本仍需该验收。自动化测试不能证明 Pro 账号状态或所有游戏效果。

## 隔离验收与完整测试

`test-installer.ps1` 检查 21 组安装编排、失败拦截、注册表快照、静默移除、快捷方式备份和目标识别、参数引号往返、独立 GUI 启动及窗口状态，另检查轻量版缺失／损坏载荷和进程互斥。静默移除回归用假 Update.exe 验证原卸载器不会被启动，并验证只读应用文件可清理、无关目录及同名无关快捷方式保留。窗口测试覆盖最小尺寸与 150% / 200% 渲染。

`test-real-package.ps1 -BundleDirectory <原版 app 目录>` 只操作复制到临时目录的副本，覆盖 Static / Supervised 两种策略、四项补丁、JavaScript 和远程桥语法，以及恢复后的全部文件哈希。不要把专有解包文件提交到仓库。

本机没有 Windows Sandbox，用户选择先完成安全测试并提供隔离脚本。可在支持且已启用 Windows Sandbox 的电脑生成验收环境：

```powershell
.\scripts\new-test-sandbox.ps1 -OutputDirectory 'C:\WandAcceptance'
```

使用完整打包版；轻量版还需指定 `-WandInstallerPath <原版安装包>`。双击生成的 `Wand-Acceptance.wsb`，沙盒将自动执行首次安装、旧版卸载重装、配置与注册表备份清理、CLI 恢复重打和启动检查。载荷映射只读、网络关闭，只有新建的 results 目录可回写宿主。结果保存在 `results/result.json` 与 `acceptance.log`；沙盒关闭后内部安装消失。脚本拒绝在宿主用户或非指定沙盒路径运行。

安装助手提供 `--install [--no-launch]` 供无人值守部署与隔离验收使用，**会执行真实卸载和重装**；请勿在宿主机上把它当成只读测试。`--check` 仍为只读校验。测试结果及尚未覆盖的项目见 [TEST-REPORT.md](TEST-REPORT.md)。
