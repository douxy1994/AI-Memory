# AI Memory Windows 原生版 0.1.3 功能对齐交接（给 Codex / Kimi Code）

> 交接日期：2026-08-09
> 交接来源：macOS 端 Codex 现场实现、测试与发布结果
> 目标环境：有 Explorer 与通知区域的 Windows 11 实机
> 项目根目录：Windows 端实际克隆路径
> macOS 对齐基线：`v0.1.3` / `dadcbe662aa596fd54367e84d0281bd0ae5be647`
> 已发布参考：[AI Memory 0.1.3](https://github.com/douxy1994/AI-Memory/releases/tag/v0.1.3)

## 1. 接手方式

先运行：

```powershell
git fetch origin --tags
git switch main
git pull --ff-only
git status --short --branch
git rev-parse HEAD
git merge-base --is-ancestor dadcbe662aa596fd54367e84d0281bd0ae5be647 HEAD
```

最后一条命令必须返回 `0`。当前 `main` 还应包含本交接文档的更新提交；不要回到旧交接中的 `033bde5` 开发，也不要移动已经发布的 `v0.1.3` 标签。

依次阅读：

1. 本文件；
2. [`KIMI_FINITE_GOAL.md`](./KIMI_FINITE_GOAL.md)；
3. [`parity.json`](./parity.json)；
4. [`README.md`](./README.md)；
5. [`../docs/RELEASE_NOTES_0.1.3.md`](../docs/RELEASE_NOTES_0.1.3.md)；
6. [`scripts/verify.ps1`](./scripts/verify.ps1) 与 [`scripts/smoke-desktop.ps1`](./scripts/smoke-desktop.ps1)。

## 2. 已确认的 macOS 0.1.3 行为

这些是 Windows 端要对齐的产品行为，不是要求照搬 SwiftUI 实现：

1. 窗口先用 AI Memory 独立索引显示缓存内容，再在后台扫描本机全部**已安装且具有受支持历史格式**的 Agent/CLI；正常启动不再依赖“刷新全部来源”。
2. 启动导入是本机只读、幂等操作；不会顺便上传 WebDAV 或本地同步目录。
3. 导入完成后刷新全部可用来源、跨 Agent 计数、最近任务和当前来源，并显示持久完成状态。
4. “迁移对话”目标来自本机实时检测结果：排除源 Agent，显示所有已安装目标；能安全写入的可选，尚无稳定写入器的标记“当前格式只读”并禁用。
5. Kimi Code 已成为安全可写目标，与 Claude、Codex、Gemini、OpenCode 一样要在写入后回读验证；失败时撤销目标写入且不删除源。
6. 更新、自动更新和诊断集中在“关于”页；设置不再保留重复的“更新与诊断”分类。
7. 常规提示使用无品牌色的系统半透明材质；错误和警告仍保留必要的语义与无障碍信息。
8. 二级页面操作按钮使用平台原生材质；返回工作台操作允许使用有色突出样式保证对比度。
9. 历史页的资料库、运行、产物、经历、Wiki、实体图等标签页使用一致的可用内容宽度。
10. 登录时自动启动开关正确处理系统需批准或用户禁用状态，并提供系统设置入口。

macOS 发布前实测：启动自动同步识别 `7` 个本机来源、扫描 `434` 条对话，跨 Agent 计数为 `685`；Xcode 测试 `72/72` 通过。数字只用于证明行为已运行，Windows 验收必须使用 Windows 实机自己的结果，不能硬编码这些值。

## 3. Windows 当前基线与已定位差异

不要重写已通过模块。当前代码已经具备大量可复用基础：

- `NativeHistoryImportService.ImportAllAsync()` 已有 Claude、Codex、Gemini、Kimi、Hermes、Antigravity、OpenCode、ZCode 八类只读导入器；
- `AgentCatalog.Detect()` 每次读取当前环境，能把已安装 Agent 排到前面；
- `ConversationMigrationService` 已有写入、回读验证、失败撤销、移动归档与回收站恢复流程；
- `StartupService` 已使用 MSIX `StartupTask`，并能打开 `ms-settings:startupapps`；
- 关于窗口已有更新检查，Windows 页面已有 `InfoBar`、`ContentDialog` 和公共 `CardStyle`；
- `parity.json` 现有 37 项中 34 项标记为 `implemented`，另有 3 项 Windows 实机运行门禁。

本次静态审查确认的差异：

| 对齐项 | Windows 当前代码 | 结论 |
| --- | --- | --- |
| 启动自动导入 | `App.LaunchAsync()` 初始化数据库和页面后只启动 ChatMem WebDAV 兼容迁移及更新检查；`NativeHistoryImportService.ImportAllAsync()` 只在 `MainWindow.RefreshAllSourcesAsync()` 手动路径调用 | 需要实现 |
| 动态迁移目标 | `ConversationPage.Migrate_Click()` 已按 `AgentCatalog.Detect()` 过滤已安装项，但又用 `WritableTargets` 隐藏只读目标 | 部分实现，需显示只读项 |
| Kimi 写入 | `NativeAgentConversationWriter.WritableTargets` 只有 `claude/codex/gemini/opencode`，`WriteAsync` 与 `DiscardAsync` 无 Kimi 分支 | 需要实现 |
| 更新与诊断合并 | `AboutWindow` 有更新检查；`SettingsPage.xaml` 仍有完整 `updates` 分类、更新、就绪检查和诊断面板 | 需要合并并删除重复入口 |
| Windows 原生材质 | `MainWindow` 未设置 Mica/SystemBackdrop；常规成功反馈使用 `InfoBarSeverity.Success`，页面按钮未形成统一的二级/突出层级 | 需要按 WinUI 3 适配，不照搬 Liquid Glass API |
| 历史页宽度 | 各标签使用不同控件树，尚无覆盖所有标签内容边界的专项测试或截图证据 | 需要统一并实机验证 |
| 登录启动 | `StartupService` 已区分 `StartupTaskState` 并提供系统设置入口 | 保留实现，只做打包实测 |

## 4. 必做实现 A：每次启动自动导入所有已安装且可读的本机历史

### 行为

1. `LaunchAsync()` 必须先显示窗口、初始化数据库、读取设置并展示缓存工作台。
2. 缓存 UI 可操作后，仅在主实例启动一次后台导入；第二实例重定向激活不得重复扫描。
3. 从一个可扩展的“已安装 + 有读取能力”注册表派生来源，不在 UI 或启动代码硬编码固定四项。
4. 当前八种读取器全部参与动态判断；未来增加读取器时只扩展能力注册表，启动流程自动纳入。
5. 单个来源损坏只进入 warning 并继续其他来源；全局状态最终清除 busy。
6. 导入后重新加载当前来源、跨 Agent 聚合计数和工作台最近任务；不要通过重新创建窗口刷新。
7. 手动“刷新全部来源”继续作为故障恢复入口，并复用同一服务路径。
8. 不在启动时调用 `SyncNowAsync()`；WebDAV/本地目录上传下载仍只由已配置的同步动作触发。

### 建议代码入口

- `Windows/src/AIMemory.Windows/App.xaml.cs`
- `Windows/src/AIMemory.Windows/MainWindow.xaml.cs`
- `Windows/src/AIMemory.Windows/Pages/WorkbenchPage.xaml.cs`
- `Windows/src/AIMemory.Core/Services/NativeHistoryImportService.cs`
- `Windows/src/AIMemory.Core/Services/AgentCatalog.cs`
- 中英文 `Resources.resw`

建议让 `MainWindow` 暴露一个可等待的 `SynchronizeInstalledAgentHistoryAfterLaunchAsync()`，由 `App.LaunchAsync()` 在 `CompleteStartup(settings)` 后 fire-and-observe。该方法与手动刷新共用一个互斥门和结果模型，避免启动扫描与用户点击刷新并发写 SQLite。

### 验收

- 冷启动前准备至少 Claude/Codex/Kimi 三种夹具，不点击刷新即可在数据库和工作台看到全部来源；
- 连续启动两次只保留一个进程和一次扫描；
- 同一批历史第二次启动不产生重复对话；
- 一个损坏夹具不会阻止另外两个来源导入；
- 未配置同步时无网络同步；已配置同步时也不能因本机导入自动上传；
- 状态显示实际来源数、扫描总数与 warning 数，不使用固定数字。

## 5. 必做实现 B：动态迁移目标与 Kimi Code 写入

### 目标列表

- 数据源是实时 `AgentCatalog.Detect()`，排除当前来源；
- 显示所有已安装目标；
- `NativeAgentConversationWriter` 有稳定写入能力的目标可选；
- 其他已安装目标显示“当前格式只读”并禁用；
- 若没有可写目标，完整迁移主按钮禁用，总结式迁移仍可复制继续卡片；
- 不把“有残留配置”当作已安装。

### Kimi Code 文件契约

Windows 写入器应与已经能读取的 Kimi 格式保持同构：

```text
%USERPROFILE%\.kimi-code\sessions\wd_<safe-leaf>_<sha256-prefix>\session_<uuid>\
├── state.json
└── agents\main\wire.jsonl

%USERPROFILE%\.kimi-code\session_index.jsonl
```

- `state.json`：至少包含 `createdAt`、`updatedAt`、`title`、`lastPrompt`、`workDir` 和 `agents.main`；
- `wire.jsonl`：用户消息写为 `turn.prompt`，助手文本写为 `context.append_loop_event/content.part`，工具调用与结果保留稳定 ID；
- `session_index.jsonl`：追加 `sessionId`、`sessionDir`、`workDir`；
- 恢复命令使用 `kimi --session <session_id>`；
- 所有路径用 Windows 原生路径和规范化项目目录计算，不复制 macOS 绝对路径。

### 原子性与回滚

1. 写 `state.json` 和 `wire.jsonl` 后再登记索引；
2. 任一步失败时删除新会话目录并移除相同 `sessionId` 的索引行；
3. 迁移服务随后运行本机历史导入，核对目标 Agent、消息数、首条用户消息；
4. 回读失败调用 `DiscardAsync`，源对话保持原状；
5. `cut` 仅在目标回读通过后归档源；从回收站恢复和永久清理都要覆盖 Kimi 目录与索引；
6. 不改写真实用户已有索引行；测试使用临时 home。

### 必测

- `WritableTargets` 包含 `kimi`；
- Kimi copy 生成完整目录、事件和恰好一条动态索引；
- 导入器能回读并核对消息数/首条用户消息；
- 写入中断、索引失败、回读不符三种情况均清理目标且保留源；
- cut、回收站恢复和永久删除往返；
- 菜单显示已安装只读目标但禁止选择，并实时反映安装/卸载变化。

## 6. 必做实现 C：关于、登录启动和 Windows 11 原生材质

### 关于与设置

把以下内容集中到 `AboutWindow`：

- 自动检查更新开关与 feed；
- 手动检查与安装；
- 升级就绪检查；
- 运行、复制诊断和打开数据目录。

从 `SettingsPage` 删除 `updates` 分类和整块 `UpdatesPanel`，并同步清理 code-behind、`.resw` 资源、帮助文案、快捷入口和测试。当前设置最终保留常规、Agent 集成、数据同步与备份三类；以后只有存在真实高级设置时才新增“高级”，不为凑数增加空页。

### 登录启动

不要重写已经使用 `StartupTask` 的实现。必须在注册的 MSIX 身份下验证：

- `Disabled` 可请求启用；
- `DisabledByUser` 显示系统设置按钮；
- `DisabledByPolicy` 不伪装为成功；
- 开启、退出、重新登录后应用按预期启动；
- 关闭开关后不再启动。

### 平台原生材质

macOS 的 Liquid Glass 在 Windows 端应转换为 Windows 11 Fluent 材质，而不是模拟 SwiftUI：

- 主窗口使用 WinUI 3 `MicaBackdrop`/`SystemBackdrop`，不支持时回退主题背景；
- 短暂浮层或消息宿主可使用系统 Acrylic/ThemeResource；常规同步成功条保持无绿色品牌底色；
- warning/error 可以保留语义图标、文字和系统严重度，但不能只靠颜色表达；
- 二级页面按钮统一默认/轻量层级；返回工作台按钮使用 `AccentButtonStyle` 或同等突出样式以保持对比度；
- 不把每张内容卡都做成重度 Acrylic，避免性能和可读性退化；
- 高对比度、减少透明度和不支持材质时必须回退为系统主题资源。

实现与评审以 Microsoft 官方文档为准：

- [Mica material](https://learn.microsoft.com/windows/apps/design/style/mica)
- [Acrylic material](https://learn.microsoft.com/windows/apps/design/style/acrylic)
- [InfoBar](https://learn.microsoft.com/windows/apps/design/controls/infobar)

## 7. 必做实现 D：历史页宽度一致

在同一个窗口尺寸、NavigationView 展开状态和缩放比例下，逐个切换历史页全部标签：

- 资料库/对话；
- 记忆；
- 检查点；
- 交接；
- 运行；
- 产物；
- 经历；
- Wiki；
- 实体图。

统一外层 `Grid`/`ScrollViewer`/`ListView` 的 `HorizontalAlignment="Stretch"`、列宽、外边距和 `MaxWidth` 规则。空状态和有数据状态都必须占同一内容宽度。不要用某个标签的固定像素宽度去掩盖布局问题。

保存至少三张同尺寸截图：资料库、运行、实体图；标注窗口尺寸与显示缩放，并核对内容卡左右边界一致。

## 8. 版本、状态与文档

功能对齐和 Windows 实机验收通过后：

1. 将 Windows 程序集、MCP serverInfo、诊断回退值和 MSIX 版本统一到 `0.1.3` / `0.1.3.0`；
2. 更新 `Windows/README.md`；
3. 更新 `Windows/parity.json` 的现有功能证据，不凭静态代码把运行门禁标为完成；
4. 更新校验脚本，使其覆盖启动自动导入、Kimi 写入、动态只读目标、设置分类去重和版本一致性；
5. 不移动 macOS `v0.1.3` 标签；Windows 安装包发布由用户单独确认。

## 9. 固定验证顺序

### 9.1 基线

```powershell
pwsh .\Windows\scripts\verify.ps1 *> .\artifacts\windows-0.1.3-baseline.log
$LASTEXITCODE
```

先保存原始输出，再改代码。

### 9.2 局部测试

```powershell
dotnet test .\Windows\tests\AIMemory.Core.Tests\AIMemory.Core.Tests.csproj `
  --configuration Release
node .\Windows\scripts\verify-parity.mjs
node .\Windows\scripts\verify-localization.mjs
node .\Windows\scripts\verify-codebehind.mjs
```

### 9.3 完整验证与桌面 smoke

```powershell
pwsh .\Windows\scripts\verify.ps1 *> .\artifacts\windows-0.1.3-final.log
$LASTEXITCODE

pwsh .\Windows\scripts\smoke-desktop.ps1 `
  -AppUserModelId '<PackageFamilyName>!App' `
  -ProcessName 'AIMemory.Windows'
```

还要验证：

- x64 与 ARM64 Release；
- x64 unsigned MSIX；
- packaged `aimemory-mcp.exe` 初始化并返回 22 个工具；
- `AIMemorySourceRevision.txt` 与 HEAD 一致；
- 单实例、窗口可见、关闭到通知区域、二次启动恢复；
- 启动自动导入、登录启动开关、关于页和全部历史标签的实机 UI。

## 10. 交付物与完成定义

Windows Codex 最终必须提交：

1. 修改文件清单与分支/提交；
2. 基线及修改后命令、完整输出和退出码；
3. 启动自动导入统计与去重证据；
4. Kimi 写入、回读、撤销、cut/恢复测试；
5. 迁移目标菜单截图；
6. 关于页、登录启动状态、无色消息条与返回按钮截图；
7. 历史页三张同尺寸对比截图；
8. x64/ARM64、MSIX、packaged MCP 与桌面生命周期证据；
9. `Windows/parity.json` 的真实更新；
10. 可运行回滚命令。

只有本文件第 4–9 节全部通过，且 `parity.json` 三个既有 Windows 运行门禁也有实机证据，才能报告“Windows 0.1.3 功能对齐完成”。不要用 macOS 测试、静态检查或模拟数据替代 Windows 桌面证据。
