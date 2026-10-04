<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# Windows 同步检查与开发交接：macOS 0.1.5

日期：2026-10-04（Asia/Singapore）。目标：在保留 Windows 原生界面、已有功能和用户数据的前提下，对齐本次 macOS 修复，完成 Windows 实机验收后再构建新版安装包。

## 1. 基线与范围

- macOS 修复提交：`2232c8b`（候选审批反馈、幂等性、临时项目过滤）。
- 合并远端 Windows 0.1.3 更新：`679a996`，已保留远端 `d7cd4e3` 及其 Windows EXE 安装、系统集成和文档改动。
- macOS 版本与更新说明：`d675073`，版本 `0.1.5`、build `5`；详见 [更新说明](docs/RELEASE_NOTES_0.1.5.md)。
- 主窗口恢复来自 `edb54e6`；此前本地准备的 0.1.4 未在 GitHub 发布，0.1.5 包含这些改动。
- 本次发布仅提供 macOS DMG。Windows 仍为 0.1.3；不要把旧 EXE 改名成新版本，也不要把 macOS 测试结果写成 Windows 验收结果。
- 从 GitHub 最新 `main` 开始，在 `codex/windows-0.1.5-parity` 分支开发，保留本地未提交工作，不执行强制重置。

## 2. 已验证与待验证

macOS 已验证：74 项 XCTest 通过；隔离窗口批准后待审 1→0、已批准 0→1，成功提示及仓库待审计数同步更新；重复及并发审批仅产生一条规则；临时项目不出现在默认列表和搜索中，源文件与旧数据库记录仍保留。

本轮 Windows 仅检查了下面列出的源码，未在 Windows 11 执行新版测试、构建、安装或 UI 验收。仓库既有 Windows 0.1.3 验收记录属于旧版本证据，不覆盖本次修复。

## 3. 候选审批：检查与修改入口

| 层 | macOS 参考 | Windows 对应 |
|---|---|---|
| 卡片与按钮状态 | [MemoryDrawerView.swift](AIMemory/ViewControllers/MemoryDrawerView.swift)，`CandidateCard` | [MemoryPage.xaml](Windows/src/AIMemory.Windows/Pages/MemoryPage.xaml)、[MemoryPage.xaml.cs](Windows/src/AIMemory.Windows/Pages/MemoryPage.xaml.cs) |
| 提交及局部刷新 | [AppStore.swift](AIMemory/Stores/AppStore.swift)，`reviewingCandidateIDs`、`candidateReviewDidCommit`、`refreshCandidateReview` | `ApproveCandidate_Click`、`ReviewCandidateAsync`、`ReloadAsync`、`ReloadRepositoryOptionsAsync` |
| 事务与重复提交 | [NativeConversationStore.swift](AIMemory/Persistence/NativeConversationStore.swift)，`reviewCandidate` | [MemoryGovernanceService.cs](Windows/src/AIMemory.Core/Services/MemoryGovernanceService.cs)，`ApproveCandidateAsync`、`ReviewCandidateAsync` |

当前 Windows 源码在提交后先等待 `ReloadAsync()`，随后才显示成功提示；未见这条操作链使用按 candidate ID 的忙状态。服务端在事务中只读取 pending/pending_review 候选，重复请求会抛出“找不到待审候选”，与 macOS 的已批准重试语义不同。先写测试复现，不要直接假定 Windows 也会生成重复记录。

要求：

1. 按 candidate ID 管理忙状态，批准、编辑后批准、拒绝、暂缓和批量动作之间避免重复提交；用 `finally` 释放状态，失败后能重试。
2. 写入提交后立即给出明确反馈，并更新卡片；只刷新审批相关集合和仓库计数，不等待图谱、Wiki、同步或全局历史加载。
3. 仓库切换及并行加载使用请求代次或取消令牌，旧请求不得覆盖新仓库状态或让已处理候选重新出现。
4. 服务端在同一事务内检查状态并防止重复插入；使用两个独立连接并发测试。已批准请求的重试应为可识别的幂等结果，不伪报一次新建。
5. 明确区分“写入失败”和“写入成功但刷新失败”。用户取消对话框、空编辑内容、候选不存在、数据库锁定也要有一致处理。
6. 不自动删除或停用既有重复规则；数据整理是单独的用户操作。

## 4. 临时目录：过滤策略与边界

macOS 参考：`NativeConversationStore.isTemporaryProject`、`importConversation`、`listConversations`、`searchConversations`、`searchRepoHistory`；两个历史 importer 只对实际导入记录增加计数。

Windows 检查入口：

- [NativeHistoryImportService.cs](Windows/src/AIMemory.Core/Services/NativeHistoryImportService.cs)：`ImportAllAsync`、`ImportAgentAsync` 与各来源的 `_repository.UpsertAsync` 调用。
- [ConversationRepository.cs](Windows/src/AIMemory.Core/Persistence/ConversationRepository.cs)：列表、搜索、项目历史查询及 `UpsertAsync`。
- [ConversationListProjectionService.cs](Windows/src/AIMemory.Core/Services/ConversationListProjectionService.cs)：项目分组与列表投影。
- MCP 与全局搜索若绕过上述入口，也要核对一致性。

要求：

- 依据会话的工作目录/项目路径判断，不能以历史文件 `storagePath` 含 `tmp` 作为排除条件。Gemini 的 `.gemini/tmp` 是合法历史存储位置。
- macOS 同步过来的 `/tmp`、`/private/tmp`、`/var/tmp`、`/private/var/tmp`、`/var/folders`、`/private/var/folders` 项目采用相同策略。
- Windows 本机临时根以 `Path.GetTempPath()`、环境变量 `TEMP`/`TMP` 及实际系统临时目录为依据，比较规范化路径与目录边界；不要仅凭名称包含 `temp`、`tmp`、`cache` 排除正常项目。
- 处理 Windows 大小写、斜杠、尾部分隔符、盘符和 UNC；对跨平台路径单独解析，避免把 POSIX 路径错误补成当前 Windows 驱动器下的路径。
- 新自动导入跳过临时项目；既有记录从默认列表/搜索中隐藏而非删除。显式读取、恢复、备份与同步写入保留原有能力，不要把过滤塞进通用 Upsert 导致恢复丢数据。
- 增加真实临时路径测试，同时把普通测试的“逻辑项目路径”移至非临时位置；实际测试数据库仍应在隔离临时目录。跨平台语义哈希的共享测试向量保持原值。

## 5. 必须补充的验收用例

| 场景 | 验收结果 |
|---|---|
| 连续点击批准、两窗口/两连接并发 | 仅一条关联规则；提示准确，候选不回弹 |
| 审批时切换仓库、触发刷新 | 新仓库不被旧响应覆盖；写入留在正确仓库 |
| 批准成功但刷新异常 | 提示已写入、刷新失败，不引导重复新建 |
| 编辑后批准、取消、拒绝、暂缓、全部忽略 | 状态、计数、按钮恢复及持久化一致 |
| 新导入临时项目与旧索引临时项目 | 默认列表、搜索、项目分组均隐藏；源文件字节不变 |
| 正常项目内 tmp 子目录、Gemini 历史缓存 | 合法会话保留 |
| Windows TEMP、盘符大小写、UNC、同步的 POSIX 路径 | 根路径及边界匹配正确，普通项目不误伤 |
| 恢复、迁移、WebDAV/文件夹同步、共享哈希向量 | 既有数据保留语义不回归 |
| 关闭、最小化、托盘恢复、重复启动 | Windows 原生单实例和主窗口恢复正常；无需照搬 SwiftUI 实现 |

在 `Windows/tests/AIMemory.Core.Tests` 添加相应测试，参考 macOS 的 `NativeConversationStoreTests`、`NativeHistoryImporterTests`。使用独立数据库，不审批用户真实候选来测试。

## 6. Windows 命令与版本更新

在仓库根目录的 PowerShell 中执行：

```powershell
git fetch origin --tags
git status --short
git switch -c codex/windows-0.1.5-parity origin/main
pwsh ./Windows/scripts/verify.ps1
# verify.ps1 已覆盖源码契约、核心测试、x64/ARM64 构建和 MCP helper 冒烟。
```

随后在真实 Windows 11 桌面执行 [smoke-desktop.ps1](Windows/scripts/smoke-desktop.ps1) 和上述审批/过滤用例；具体参数按实际已注册包确定，沿用 [Windows README](Windows/README.md) 的安装与验收流程。

通过后，统一更新 Core、Mcp、Windows 三个 `.csproj` 的 Version/AssemblyVersion/FileVersion、`Package.appxmanifest`、`Windows/installer/AIMemory.Setup.csproj` 以及 `Windows/scripts/build-installer.ps1` 中硬编码的版本、MSIX 匹配模式和输出目录。目标版本 0.1.5，四段 Windows 版本 0.1.5.0；若远端已占用该版本，先核对并使用下一个一致的补丁版本。

```powershell
pwsh ./Windows/scripts/build-installer.ps1 -Configuration Release -Platform x64
```

安装包必须来自最终提交重建。验证 EXE 安装、实际启动版本、单实例、托盘、登录启动、MCP 与同步后，生成 SHA-256，记录来源提交、命令、字面输出和退出码。补全 `Windows/parity.json`、Windows README 与发布说明；仅把本轮有证据的项目标记完成。发布前取得用户对 Windows 发布的明确授权，不覆盖已有 macOS 资产。

## 7. 交付与回滚边界

- 交付源码提交、测试/构建结果、Windows 实机 UI 证据、版本化 EXE 与 SHA-256、已验证/待验证清单。
- 保留旧安装包与安装前配置/数据库备份；回滚优先恢复应用，不用旧数据库覆盖新产生的用户历史。
- `.artifacts/` 已加入忽略列表，里面含本机测试与历史副本，不推送、不打包；公开交接材料不得包含个人历史、凭据或本机配置。
