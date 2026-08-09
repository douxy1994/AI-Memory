# AI Memory for Windows 11

AI Memory 的 Windows 11 原生版本，使用 C#、WinUI 3 与 Windows App SDK 构建。它与 macOS 版本共享产品行为和数据语义，但不使用跨平台 UI，也不会覆盖 macOS 应用或 ChatMem。

> 当前版本：**0.1.3**。Windows 11 x64 EXE 安装程序、x64/ARM64 Release 构建、真实桌面生命周期、启动导入、Agent 集成与 WebDAV 增量同步均已在 Windows 11 实机验收。

> macOS `v0.1.3` 的功能对齐基线和固定验收要求见 [`KIMI_HANDOFF.md`](./KIMI_HANDOFF.md)；本版本已经按该清单完成同步。

## 原生技术栈

- C# / .NET 10；
- WinUI 3 / Windows App SDK 2.2；
- Windows 11 SDK；
- MSIX；
- Microsoft.Data.Sqlite；
- Windows Credential Locker；
- StartupTask；
- Windows App Lifecycle `AppInstance`；
- 原生 stdio MCP helper。

## 当前实现

- 单实例：重复启动恢复并聚焦现有窗口；
- 原生通知区域图标：关闭主窗口后继续运行，可重新打开、立即同步或彻底退出；
- 可按来源切换的工作台、最近任务、四项核心指标，以及按 Windows、Mac、Linux、Internal、Other 自动识别的电脑分组；
- 可重命名电脑、合并电脑、移动单个项目并恢复自动分组，所有调整只影响展示，不改写原始项目路径；
- 字体设置全局应用于 WinUI，并兼容 macOS 的系统、思源黑体、思源宋体和霞鹜文楷标识；未安装字体由 Windows 自动回退；
- 使用 Windows App SDK MRT Core、`.resw` 与 `x:Uid` 提供完整简体中文和英文界面；
  可跟随 Windows 显示语言，也可在设置中单独选择并在重启后生效；
- 统一历史页中的对话、记忆、检查点、交接、运行、产物、经历与 Wiki，
  对话支持来源筛选、项目多选筛选、按项目/时间线排列、最近更新/创建/标题排序，
  所有来源型条目均可打开原始对话；实体图谱也会列出真实关联并打开对应对话、
  记忆规则或 Wiki；
- 完整对话详情、收藏备注/标签/置顶/继续卡片，以及带确认、失败反馈和保留期说明的
  可恢复批量回收站；
- SQLite 数据库、可双向读取 macOS/Windows 键名的版本化设置，以及可自动探测或
  用 Windows App SDK 原生文件选择器手动选库的 ChatMem 安全导入；导入使用
  只读 SQLite 在线快照保留已提交 WAL 数据，先备份现有数据库，再迁移、校验
  临时副本并替换；
- 首次启动会以只读、幂等方式迁移 ChatMem 的 WebDAV 地址与凭据：不会覆盖
  AI Memory 中不同的现有端点，优先读取 Windows Credential Manager 中
  ChatMem 的原凭据，并兼容旧设置文件中的密码回退，密码只写入 AI Memory
  自己的 Credential Locker；
- WebDAV 与本地文件夹增量同步；WebDAV 兼容旧字节哈希并使用与 macOS 共享的
  语义摘要，JSON 序列化差异不会反复上传；本地目录使用 Windows App SDK 原生
  `FolderPicker` 选择，采用跨端一致的 `conversations/<agent>/<base64url(id)>.json`
  布局，并可在同步前检查云盘锁文件及短时变动；
- 无变化跳过、未变化文件使用 NTFS 硬链接复用的增量恢复点，支持定时备份、
  恢复前安全备份与失败自动回滚；
- Windows Credential Locker 保存 WebDAV 密码；
- 登录启动开关；若曾被用户从 Windows“启动应用”中禁用，会直接提供系统
  设置入口恢复，不伪装成可由应用绕过的开关；
- 升级就绪检查会逐项验证设置文件、WebDAV 配置、Credential Locker 密码、
  SQLite 结构版本与 `quick_check`，并明确区分通过、提醒和阻断问题；
- 首批常用目录项包含 Factory Droid、Mistral Vibe、Amazon Q Developer、GitHub Copilot CLI、
  Qwen Code、Goose、Cline、Roo Code、OpenHands、Aider、Crush 与 Kilo Code；
- 165 种主流 Agent、通用 AI CLI 与本地模型 CLI 安装检测（包括 Claw Code、Coro、Nori、CodeMachine、Open Codex、Groq Code CLI、Devon、g3、Mini-Kode、zot、VibePod、Every Code、Claw Code Agent、Gitagent、OpenDev、QodeX、ClawCodex、Tutti、acpx、cmux、muxd、muxel、Flowmux、MCPJam、Zenflow、Void、Ruflo / Claude Flow、Claurst、agentty、Herdr、Smol Developer、Claude Engineer、Free Code、ForgeCode、AutoCodeRover、Agentless、Codel、openHarness、Octomind、Codex Infinity、San、Waveloom、picocode、QQCode、Keen Code、Smelt、Grinta、Zap、Binharic、Darce、CLAII、xAI Grok Build / Grok CLI、NanoClaw、Clawith、claw0、GitClaw、LionClaw、Crab Code、OpenAgent、DvalinCode、LettaBot 与 oh-my-openagent），兼容无扩展、
  `.exe` 与 `.cmd` 启动器；
  同时检查用户目录和 Windows 的 Program Files/Common Program Files，系统级安装的桌面 Agent
  也会进入“已检测”排序；
  已安装项优先，未安装项即使存在旧配置也保持关闭；
- 16 种具有稳定配置格式的 Agent 可安全启用或关闭 MCP；其中 11 种同时
  安装 AI Memory skill 与受管启动规则。已有配置先备份，部分安装可自动修复，
  并支持对当前已安装且可安全配置的 Agent 批量安装、修复或确认后批量卸载；
- Claude、Codex、Gemini、Kimi、Hermes、Antigravity、OpenCode 与 ZCode
  本地历史只读导入；启动时只扫描本机已安装且可读的来源，先显示缓存，再后台
  幂等刷新，不会自动触发 WebDAV 或本地文件夹同步；
- 迁移目标根据当前安装状态动态生成；完整对话可原生复制到 Claude、Codex、Gemini、
  OpenCode 或 Kimi Code，并在写入后
  重新导入核对消息数与首条用户消息；“移动”模式会把来源原始历史和完整
  数据快照放入可恢复回收站，恢复及永久删除均覆盖文件型、SQLite 型与 Kimi
  索引；已安装但格式只读的目标会显示为不可选择，不会报告虚假迁移成功；
- 总结式迁移可生成带来源证据的继续卡片，不伪装成目标 Agent 的原生历史；
- 可按仓库筛选的记忆复核、批量忽略、冲突查看、检查点与交接流程；
  候选记忆在批准前会显示置信度、来源证据、待处理的合并建议和冲突提示，
  避免只凭摘要进行不可追溯的审核；
  交接包可查看完整目标、已完成、下一步、关键文件和命令，也可复制内容、
  打开来源对话或标记为已消费；
- 打开对话后按来源刷新本地历史，并默认每两分钟更新该对话唯一的自动记忆恢复点；
- 文件、工作台、窗口与帮助菜单，以及覆盖 ChatMem 导入、关闭窗口、工作台、
  待复核、历史、记忆、同步、来源刷新、设置、关于和检查更新的快捷键；
- 设置仅保留通用、Agent 集成、数据同步与备份三个分类；更新、升级就绪和运行
  诊断集中到带软件图标的独立“关于 AI Memory”窗口；主窗口和关于窗口使用
  Windows 11 Mica，常规成功提示保持中性；
- 与 macOS 对齐的 22 项原生 MCP 工具，覆盖历史检索、记忆治理、
  检查点、交接、运行产物、Wiki、索引、冲突与实体图。

八类本地历史及 Kimi 写入/回滚/恢复均通过共享核心层测试；完整状态见
[功能矩阵](../docs/FEATURE_MATRIX.md)。本机最终回归为 95/95 核心测试、37/37
功能契约、22 项 MCP 工具；x64 与 ARM64 WinUI Release 构建均为 0 警告、0 错误。
通过 EXE 安装程序注册 WinUI 包后已验证单实例、可见窗口、关闭到通知区域、二次启动恢复、设置三分类、
独立关于窗口、登录启动开关、6 个实际已安装来源的 47 条会话启动导入，以及真实
WebDAV 同步（上传 0、下载 0、跳过 733；64/64 响应采样）。

## 下载与安装

从 [v0.1.3 Release](https://github.com/douxy1994/AI-Memory/releases/tag/v0.1.3)
下载 `AI-Memory-0.1.3-Windows-x64-Setup.exe` 和同名 `.sha256`，双击 EXE
即可安装并启动 AI Memory。安装程序会把 WinUI 应用注册到当前用户，安装目录为
`%LOCALAPPDATA%\Programs\AI Memory\0.1.3`；安装前对照 `.sha256` 校验文件：

   ```powershell
   Get-FileHash .\AI-Memory-0.1.3-Windows-x64-Setup.exe -Algorithm SHA256
   ```

应用数据保存在 `%LOCALAPPDATA%\AIMemory`，安装或升级不会删除数据库、设置或凭据。

## 构建

安装 Visual Studio 的 **Windows application development** 工作负载、Windows 11 SDK 和 .NET 10，然后在仓库根目录运行：

```powershell
pwsh ./Windows/scripts/verify.ps1
```

单独运行核心测试：

```powershell
dotnet test .\Windows\tests\AIMemory.Core.Tests\AIMemory.Core.Tests.csproj `
  --configuration Release
```

构建 x64 WinUI 应用：

```powershell
dotnet build .\Windows\src\AIMemory.Windows\AIMemory.Windows.csproj `
  --configuration Release `
  -p:Platform=x64
```

## Windows 11 桌面生命周期验收

该测试必须在已登录的 Windows 11 桌面会话运行。它会验证首次启动只有一个
进程、关闭主窗口后进程继续驻留、再次启动不会创建第二个持久进程，并能恢复
原窗口。测试只清理自己启动的进程；检测到已有 AI Memory 时会直接停止。

验证构建输出（展开内部 MSIX 负载得到完整包布局后注册其根清单；构建输出目录里的
AppxManifest.xml 仍含构建命名空间且缺少 Assets，而 `-DisableDevelopmentMode` 会让
部署拒绝未签名的松散布局）：

```powershell
$msix = Get-ChildItem `
  .\Windows\src\AIMemory.Windows\bin `
  -Recurse -Filter *.msix |
  Where-Object FullName -Match '\\x64\\' |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
$layout = Join-Path $env:TEMP "AIMemory-msix-layout"
if (Test-Path -LiteralPath $layout) { Remove-Item $layout -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)
Add-AppxPackage -Path (Join-Path $layout "AppxManifest.xml") -Register
$package = Get-AppxPackage -Name "com.aimemory.windows"
try {
  pwsh .\Windows\scripts\smoke-desktop.ps1 `
    -AppUserModelId "$($package.PackageFamilyName)!App" `
    -ProcessName "AIMemory.Windows"
}
finally {
  Get-AppxPackage -Name "com.aimemory.windows" |
    Remove-AppxPackage -ErrorAction SilentlyContinue
  Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
}
```

验证 EXE 安装后的 Windows 包：

```powershell
$appId = (Get-StartApps | Where-Object Name -eq "AI Memory").AppID
pwsh .\Windows\scripts\smoke-desktop.ps1 `
  -AppUserModelId $appId `
  -ProcessName "AIMemory.Windows"
```

构建可发布的 x64 EXE 安装程序：

```powershell
pwsh .\Windows\scripts\build-installer.ps1
```

输出位于 `release/0.1.3/AI-Memory-0.1.3-Windows-x64-Setup.exe`，旁边同时生成
同名 `.sha256` 校验文件。

## 项目

| 项目 | 职责 |
| --- | --- |
| `AIMemory.Core` | 数据模型、SQLite、同步、备份、导入、诊断和记忆治理 |
| `AIMemory.Windows` | WinUI 3 页面、窗口、菜单、系统集成与 Windows 包负载 |
| `AIMemory.Mcp` | Windows stdio MCP helper |
| `AIMemory.Core.Tests` | 核心数据与业务测试 |

默认数据目录为 `%LOCALAPPDATA%\AIMemory`。登录启动在包清单中默认关闭，只有用户在设置中主动启用后才请求。

Copyright © 2026 douxy1994

AI Memory is licensed under the [GNU Affero General Public License v3.0
(AGPL-3.0-only)](../LICENSE). See [../NOTICE.md](../NOTICE.md) for the
complete project copyright and third-party attribution notice.
