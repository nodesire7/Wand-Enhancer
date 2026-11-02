# GBTGAME.ME 汉化优化分支说明

本仓库是 [k1tbyte/Wand-Enhancer](https://github.com/k1tbyte/Wand-Enhancer) 的分支。它做两件事：

1. **自动跟随上游**：GitHub Actions 定时把上游 `master` 合并进来，合并后重新应用本分支的品牌信息，全程不需要手动点 “Sync fork”。
2. **固定本分支构建信息**：作者 `A-FEI-`、优化汉化、论坛 `bbs.GBTGAME.ME`、TG 群组 `t.me/GBTGame_Team` 写入界面、日志和 exe 属性；上游更新不会覆盖这些内容。

---

## 一、自动同步上游

工作流：`.github/workflows/sync-upstream.yml`

| 项目 | 说明 |
| --- | --- |
| 触发 | GitHub 定时（每小时 `:17`、`:47` UTC）+ Actions 页面手动 `Run workflow` + 本机计划任务兜底 |
| 上游 | `https://github.com/k1tbyte/Wand-Enhancer` 的 `master` |
| 方式 | `git merge --no-ff upstream/master`，保留可追溯的合并历史 |
| 冲突 | 一律取上游版本，然后由 `scripts/apply-branding.ps1` 重新写回本分支的改动 |
| 结果 | 有更新才 push 并重新构建；已是最新则直接结束 |

> 2026-11-02 实测：该 fork 的 GitHub `schedule` 事件没有投递（最小化 `*/5` 测试工作流运行 1 小时也没有任何 scheduled run）。这是 GitHub 侧的投递问题，社区也有同类报告。因此本机额外装了一个 Windows 计划任务，每 30 分钟调用一次 `workflow_dispatch`，两条路互不影响。

### 第一次启用

Fork 仓库的定时工作流默认可能处于关闭状态：

1. 打开仓库 **Actions** 标签页，如果顶部提示工作流被禁用，点 **I understand my workflows, go ahead and enable them**。
2. 打开 **Sync upstream and build**，点 **Run workflow**（可勾选 `force_build` 立刻构建一版）。
3. 之后由 GitHub 定时或本机计划任务触发。长时间（60 天）没有任何提交时 GitHub 会暂停定时任务，手动运行一次即可恢复。

> 定时任务在 GitHub 繁忙时可能延迟，但每次运行都会合并全部新提交，不会漏更新。

### 本机兜底：Windows 计划任务

| 项目 | 说明 |
| --- | --- |
| 任务名 | `WandEnhancer Upstream Sync`，当前用户登录时每 30 分钟运行 |
| 动作 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "F:\Code\Wemod\Wand-Enhancer\scripts\trigger-sync.ps1"` |
| 作用 | 只调用 `gh workflow run sync-upstream.yml`，合并、构建、发布仍在 GitHub 上完成 |
| 日志 | `%LOCALAPPDATA%\WandEnhancer-upstream-sync.log` |
| 前提 | `gh auth status` 已登录 nodesire7；脚本会临时清掉失效的机器级 `GITHUB_TOKEN`，改用 gh 的登录凭据 |
| 删除 | `schtasks /Delete /TN "WandEnhancer Upstream Sync" /F` |

电脑关机时这个兜底不会运行，开机后会补触发。GitHub 定时恢复正常后两者会重复触发，工作流会自行判断“已是最新”并直接结束，不影响结果。

换电脑或仓库路径变了以后，重新注册一次：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-sync-task.ps1
```


### 同步失败怎么办

同步任务在以下情况会停下来，**不会**把半成品 push 到 `master`：

- 合并出现 Git 无法自动处理的冲突（已自动取上游，理论上只剩脚本无法识别的情况）。
- `scripts/apply-branding.ps1` 找不到它要修改的位置（例如上游重写了 `App.xaml.cs` 的启动代码）。

到 **Actions → Sync upstream and build** 查看红色步骤的日志，按提示修好 `scripts/apply-branding.ps1` 里的定位字符串后，再手动运行一次即可。日常上游改动（功能、UI、版本号）不会触发这类失败。

---

## 二、品牌信息

### 唯一数据源

编辑 `branding/branding.json`：

```json
{
  "brand": "GBTGAME.ME",
  "author": "A-FEI-",
  "note": "优化汉化",
  "product": "WandEnhancer GBTGAME.ME 优化汉化版",
  "forum": "bbs.GBTGAME.ME",
  "forumUrl": "https://bbs.GBTGAME.ME",
  "telegram": "t.me/GBTGame_Team",
  "telegramUrl": "https://t.me/GBTGame_Team"
}
```

改完后运行：

```powershell
pwsh ./scripts/apply-branding.ps1
```

Windows 自带的 PowerShell 5.1 也可以：`powershell -ExecutionPolicy Bypass -File .\scripts\apply-branding.ps1`。

脚本会重新生成 `WandEnhancer/Branding/BrandingInfo.g.cs`，并修正两个上游文件里的少量固定行。**不要手动改 `BrandingInfo.g.cs`**，它下次会被覆盖。

### 生效位置

| 位置 | 内容 |
| --- | --- |
| 窗口标题栏徽章 | `GBTGAME.ME · A-FEI- · 优化汉化`（点击打开论坛）和 `TG`（点击打开 TG 群），悬停显示论坛与 TG |
| 窗口标题 / 任务栏 | `WandEnhancer GBTGAME.ME 优化汉化版 <版本号>` |
| 主界面底部 | `Made with ❤️ by k1tbyte · 优化汉化 A-FEI-` 与 `bbs.GBTGAME.ME · t.me/GBTGame_Team` |
| 运行日志首行 | 产品名、作者、论坛、TG、构建提交号（复制/导出日志时会一起带上，方便反馈问题） |
| exe 文件属性 | 产品名、公司、文件说明、版权均带 GBTGAME.ME / A-FEI- |

上游原有的 “Source code / Made with ❤️ by k1tbyte” 署名保留，本分支信息叠加显示。

### 实现方式（为什么不会被上游覆盖）

- `branding/branding.json`、`WandEnhancer/Branding/**`、`Directory.Build.targets`、`scripts/apply-branding.ps1` 都是**上游没有的文件**，合并时不会冲突。
- 界面是运行时注入的（`App.xaml.cs` 里只有一行 `BrandingUi.Apply(...)`），不修改上游的 XAML，所以上游调整布局不会冲掉徽章。
- 只有两处上游文件需要小改动：`App.xaml.cs` 的一行调用、`Properties/AssemblyInfo.cs` 的 5 个程序集属性。它们由脚本按“找到就替换、找不到就补一行”的方式维护，合并冲突取上游后会立刻被重新写回。
- `Directory.Build.targets` 自动把 `WandEnhancer/Branding/*.cs` 加入编译，不必修改旧式 `.csproj`，避免与上游新增文件冲突。

### 校验

```powershell
pwsh ./scripts/apply-branding.ps1 -Check
```

输出 `Branding is up to date.` 表示一切正常；否则会列出过期的文件并以退出码 1 结束。CI 在构建前也会跑这一步。

---

## 三、自动构建与下载

工作流：`.github/workflows/sync-upstream.yml` 的 `build` 任务（上游有新提交时自动运行，或在手动运行时勾选 `force_build`）。

它会执行完整的 `build.ps1`（web-panel lint/类型检查/测试、WPF 编译、补丁回归检查），然后把结果更新到**滚动发布**：

- 发布页：`https://github.com/nodesire7/Wand-Enhancer/releases/tag/gbtg-latest`
- 直链：`https://github.com/nodesire7/Wand-Enhancer/releases/download/gbtg-latest/WandEnhancer.exe`
- 校验：同目录 `WandEnhancer.exe.sha256`

`gbtg-latest` 是固定标签，每次构建覆盖其中的 exe，下载地址永远不变。构建未签名，SmartScreen 提示属正常现象。

> 上游 `release.yml` 只在上游仓库运行（`if: github.repository == 'k1tbyte/Wand-Enhancer'`），不会在本分支重复发布。

---

## 四、本地构建

需要 Visual Studio（含 MSBuild）、.NET Framework 4.8 目标包、Node.js 22 和 pnpm（仓库用 corepack 锁定 10.17.0）：

```powershell
.\build.ps1 -Configuration Release
```

产物：`WandEnhancer\bin\Release\WandEnhancer.exe`。构建前如需确认品牌信息：

```powershell
pwsh ./scripts/apply-branding.ps1 -Check
```

---

## 五、维护守则

1. **不要直接改上游文件来做汉化/品牌**：所有固定信息放 `branding/branding.json`，需要新界面元素时加到 `WandEnhancer/Branding/BrandingUi.cs`，并在 `App.xaml.cs` 的 hook 里调用。
2. 上游更新后如果 `apply-branding.ps1` 报“找不到 …”，说明上游改了对应代码的位置：在脚本里调整定位字符串（`this.MainWindow.Show();` / `[assembly: Assembly*]`），不要改成直接提交合并结果。
3. 合并冲突一律以上游为准，本分支的改动靠脚本重建——这也是定时任务能无人值守运行的前提。
4. 修改 `branding/branding.json` 后记得运行脚本并提交生成的文件，否则 `Verify fork branding` 工作流会失败。
