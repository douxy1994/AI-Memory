# AI Memory Windows 原生版开发交接（给 Kimi Code）

> 交接日期：2026-08-02
> 交接来源：Codex 侧本机工作区
> 目标环境：Windows 11 实机上的 Kimi Code
> 项目根目录：`/Volumes/DouXY/download/AIMemory`（Windows 端请替换为实际克隆路径）

## 1. 接手前先确认代码版本

当前已确认的基线：

```text
branch: main
HEAD: 033bde56f3e39571050738aba15e64380eb3cfc4
short HEAD: 033bde5
origin/main: 3c69e4d582783ff7e39f134578410dff28330b46
local main: ahead of origin/main by 60 commits
tracked working tree at handoff inspection: clean
```

Windows 端第一步运行：

```powershell
git status --short --branch
git rev-parse HEAD
git log --oneline -8
```

接手的代码必须是 `033bde5` 或其后继，并且包含以下提交：

```text
033bde5 Show local sync progress in Windows UI
1e4b276 Record Windows startup window diagnostics
0654424 Report favorite pin result accurately
258a92d Report agent integration toggle result accurately
38ff48a Verify Windows desktop window identity
66c528f Publish Windows build artifacts from CI
```

如果 Windows 端只有旧的 `origin/main`，先取得这 60 个本地提交和本交接文档；不要在旧代码上重复重写 Windows 版，也不要用 `reset --hard` 覆盖现有成果。

## 2. 产品目标与硬约束

Windows 版必须与当前 macOS AI Memory 的功能、数据语义和操作结果对等，同时采用 Windows 11 原生技术：

- C#；
- WinUI 3；
- Windows App SDK；
- .NET 10；
- MSIX；
- Windows Credential Locker、StartupTask、AppInstance、Shell_NotifyIcon 等官方系统能力。

禁止转成 Electron、网页壳、Flutter、Qt、Python GUI 或其他跨平台 UI。不要修改或破坏 macOS 工程。

必须持续保持以下产品不变量：

1. AI Memory 使用独立数据目录 `%LOCALAPPDATA%\AIMemory`，不得覆盖 ChatMem 数据。
2. 桌面应用只保留一个活跃实例；关闭主窗口进入通知区域，二次启动恢复原窗口。
3. Agent/CLI 未安装时默认关闭；检测到安装的项目排序在前。
4. 不得仅因为存在旧配置文件就把已经卸载的 Agent 显示为已启用。
5. WebDAV 和本地目录同步只传输发生变化的对话。
6. 所有可见按钮、菜单、列表项和设置都必须连接真实逻辑。
7. 删除进入可恢复的回收站；永久删除必须明确确认。
8. 数据迁移、恢复和导入先保护原数据，失败时保留稳定状态。
9. 不用模拟数据、占位成功提示或永远不会执行的分支伪装功能完成。
10. 源文件与应用内统一显示 `Copyright © 2026 douxy1994`，许可证为 `AGPL-3.0-only`。

## 3. 当前实现状态

权威状态文件：[`Windows/parity.json`](./parity.json)。当前共 37 项：

- `implemented`：34 项；
- `verification-required`：3 项；
- 没有标记为待开发的已知核心功能项。

已实现的主要模块：

- Windows 11 原生主窗口、导航、菜单、快捷键、设置、帮助和独立关于窗口；
- 单实例、关闭到通知区域、开机启动；
- 工作台、历史、对话详情、收藏、回收站；
- 候选记忆复核、批准规则、冲突、Wiki、实体关系；
- 检查点、交接包、运行、产物和经历的可操作入口；
- ChatMem 数据库与 WebDAV 配置兼容导入；
- WebDAV 与本地目录增量双向同步及可见进度；
- 备份、恢复点、恢复前安全备份和自动备份；
- 22 个 MCP 工具；
- 中英文 606 个定位资源；
- x64/ARM64 构建与 MSIX 自动化工作流；
- 启动诊断日志：`%LOCALAPPDATA%\AIMemory\startup.log`。

## 4. Agent / CLI 目录现状

macOS 与 Windows 当前都包含 165 个同步 ID，校验脚本会阻止两端清单漂移：

```powershell
node .\Windows\scripts\verify-agent-catalog.mjs
```

关键实现：

- `Windows/src/AIMemory.Core/Services/AgentCatalog.cs`
- `Windows/src/AIMemory.Core/Services/AgentIntegrationManager.cs`
- `AIMemory/Services/NativeAgentIntegrationStore.swift`
- `Windows/scripts/verify-agent-catalog.mjs`

现有规则：

- PATH 检测支持无扩展名、`.exe`、`.cmd`；
- 同时检测用户目录、Program Files 和 Common Program Files；
- 每次检测都重新读取环境，安装新 CLI 后无需重启应用；
- 已检测项目排序在前，未安装项目保持关闭；
- 16 种具有稳定配置格式的 Agent 支持安全 MCP 开关；
- 其中 11 种还安装 AI Memory skill 和受控启动规则；
- 启用、关闭、部分安装修复和批量操作均保留用户原配置备份。

新增 Agent/CLI 时必须同时完成：

1. 查证真实的 Windows 可执行文件名和安装/配置目录；
2. 增加 Windows 与 macOS 两端同一个稳定 ID；
3. 未检测到安装时保持 `Missing`、`IsIntegrated=false`；
4. 增加检测测试；
5. 更新 README 中的数量；
6. 运行 `verify-agent-catalog.mjs`；
7. 只有配置格式稳定且能安全回滚时才标记自动集成可用。

不要为了增加数量而加入没有真实产品来源、没有可验证启动器或会与其他软件共用通用命令名的条目。

## 5. 当前真正剩余的三项验证

以下三项在 `Windows/parity.json` 中仍是 `verification-required`。Windows 实机是提供强证据的正确环境：

### 5.1 x64 与 ARM64 原生构建

需要验证：

- WinUI 3 x64 Release 成功；
- WinUI 3 ARM64 Release 成功；
- 生成 x64 unsigned MSIX；
- 构建产物包含匹配架构的 `Helpers\aimemory-mcp.exe`；
- 产物包含 `AIMemorySourceRevision.txt`，且提交号正确。

### 5.2 打包后的 MCP helper 运行

需要从 WinUI 构建输出中直接运行 helper，验证：

- 进程退出码为 0；
- `initialize` 返回 server `aimemory`；
- protocol 为 `2025-03-26`；
- `tools/list` 返回全部 22 个工具。

### 5.3 Windows 11 桌面生命周期

需要在有 Explorer 和通知区域的交互式 Windows 11 会话中验证：

- 通过注册后的 AppsFolder 启动，而不是直接启动裸 exe；
- 只存在一个 `AIMemory.Windows` 进程；
- 主窗口可见，标题包含 `AI Memory`；
- 日志出现 `launch.complete`；
- 关闭窗口后进程仍存在且窗口隐藏；
- 再次启动不会新增持久进程，并恢复原窗口；
- 通知区域的打开、立即同步、退出均有效。

自动脚本：

```powershell
pwsh .\Windows\scripts\smoke-desktop.ps1 `
  -AppUserModelId '<PackageFamilyName>!App' `
  -ProcessName 'AIMemory.Windows'
```

## 6. Windows 端建议执行顺序

### 阶段 A：一次基线验证

```powershell
pwsh .\Windows\scripts\verify.ps1
```

该脚本会执行目录、定位、版权、MCP、XAML/code-behind、核心测试、x64/ARM64 构建和 packaged helper smoke。先保存完整输出，不要先重构。

### 阶段 B：MSIX 与桌面 smoke

按 [`.github/workflows/build.yml`](../.github/workflows/build.yml) 中以下步骤逐条在 Windows 实机复现：

1. `Build unsigned MSIX package`；
2. `Register WinUI package for desktop smoke`；
3. `smoke-desktop.ps1`；
4. 检查 `%LOCALAPPDATA%\AIMemory\startup.log`。

### 阶段 C：仅修复真实失败

- 每次只处理一个明确失败；
- 保留完整错误输出和退出码；
- 先修根因，再复跑该项最小测试；
- 通过后再执行一次完整 `verify.ps1` 和桌面 smoke；
- 不因代码风格、警告数量或个人架构偏好重写已通过模块。

### 阶段 D：有限人工冒烟

自动化全部通过后，仅检查以下核心流程：

1. 首次启动与二次启动；
2. 工作台刷新来源、立即同步、打开历史；
3. 历史列表可点击并进入对话；
4. 收藏、置顶、备注、取消收藏；
5. 删除到回收站、恢复、永久删除确认；
6. Agent 页面已安装项目靠前，未安装项目关闭；
7. WebDAV 密码粘贴、验证结果、同步动画与完成结果；
8. 本地目录增量同步；
9. 创建恢复点与恢复；
10. 设置、关于、检查更新、开机启动；
11. 关闭到通知区域并再次唤醒；
12. 重启后设置和数据仍存在。

## 7. 关键文件索引

| 范围 | 文件 |
| --- | --- |
| 交接目标 | `Windows/KIMI_FINITE_GOAL.md` |
| 功能状态 | `Windows/parity.json` |
| Windows 总说明 | `Windows/README.md` |
| 构建入口 | `Windows/AIMemory.Windows.slnx` |
| 应用工程 | `Windows/src/AIMemory.Windows/AIMemory.Windows.csproj` |
| 生命周期 | `Windows/src/AIMemory.Windows/Program.cs`, `App.xaml.cs`, `MainWindow.xaml.cs` |
| Agent 目录 | `Windows/src/AIMemory.Core/Services/AgentCatalog.cs` |
| Agent 集成 | `Windows/src/AIMemory.Core/Services/AgentIntegrationManager.cs` |
| 同步 | `WebDavService.cs`, `LocalFolderSyncService.cs` |
| 数据库 | `AIMemoryDatabase.cs`, `ConversationRepository.cs`, `SchemaV1.sql` |
| 核心测试 | `Windows/tests/AIMemory.Core.Tests/` |
| 完整验证 | `Windows/scripts/verify.ps1` |
| 桌面 smoke | `Windows/scripts/smoke-desktop.ps1` |
| CI | `.github/workflows/build.yml` |

## 8. 已知环境与诊断提示

- macOS 上已验证 90 项 Windows Core 测试和 22 个 MCP 工具，但 WinUI XAML 编译器是 Windows PE 程序，Windows 桌面证据应在 Windows 11 产生。
- 旧的桌面 smoke 曾出现进程存在但窗口不可见；当前代码已改为注册生成的 `AppxManifest.xml`，通过 AppsFolder 启动，并记录 `window.activate.called`、`window.bring-to-front.completed`、`notification-area.ready/failed`。
- 发生启动失败时先读取 `%LOCALAPPDATA%\AIMemory\startup.log`，不要先改窗口架构。
- 当前本地提交尚领先远端；推送、发布 Release 或删除远端内容应由用户明确确认后执行。

## 9. 完成定义

只有当 `Windows/KIMI_FINITE_GOAL.md` 中的全部验收项都有 Windows 实机证据时，才把对应三项 `verification-required` 改为 `implemented`。完成后输出一次最终记录并停止，不再进行额外 UI 微调、架构升级、代码美化或功能扩张。
