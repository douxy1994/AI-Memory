# AI Memory for Windows 11

AI Memory 的 Windows 11 原生版本，使用 C#、WinUI 3 与 Windows App SDK 构建。它与 macOS 版本共享产品行为和数据语义，但不使用跨平台 UI，也不会覆盖 macOS 应用或 ChatMem。

> 当前状态：**Preview**。核心层、主要页面及 x64/ARM64 构建均已通过 CI，仍需在真实 Windows 11 桌面会话完成安装与交互验收。

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
- 可按来源切换的工作台、最近任务、项目分组和四项核心指标；
- 统一历史页中的对话、记忆、检查点、交接、运行、产物、经历与 Wiki，
  所有来源型条目均可打开原始对话；
- 完整对话详情、收藏备注/标签/置顶/继续卡片以及可恢复回收站；
- SQLite 数据库、版本化设置和 ChatMem 安全导入；
- WebDAV 与本地文件夹增量同步；
- 备份、恢复、更新检查与诊断；
- Windows Credential Locker 保存 WebDAV 密码；
- 登录启动开关；
- 45 种主流 Agent/CLI 安装检测，已安装项优先，未安装项默认关闭；
- Claude、Codex、Gemini、Kimi、Hermes、Antigravity、OpenCode 与 ZCode
  本地历史只读导入；
- 记忆复核、检查点与交接核心流程；
- 菜单与快捷键；
- 与 macOS 对齐的 22 项原生 MCP 工具，覆盖历史检索、记忆治理、
  检查点、交接、运行产物、Wiki、索引、冲突与实体图。

八类本地历史均通过共享核心层测试；Windows 桌面端的完整安装与交互状态见
[功能矩阵](../docs/FEATURE_MATRIX.md)。
CI 已验证 x64 与 ARM64 WinUI 3 目标，并对 WinUI 输出目录中的 MCP helper
执行了真实协议冒烟测试。桌面端仍保持 Preview，直到完成真实 Windows 11
安装、启动、窗口和交互验收。

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

## 项目

| 项目 | 职责 |
| --- | --- |
| `AIMemory.Core` | 数据模型、SQLite、同步、备份、导入、诊断和记忆治理 |
| `AIMemory.Windows` | WinUI 3 页面、窗口、菜单、系统集成与 MSIX |
| `AIMemory.Mcp` | Windows stdio MCP helper |
| `AIMemory.Core.Tests` | 核心数据与业务测试 |

默认数据目录为 `%LOCALAPPDATA%\AIMemory`。登录启动在包清单中默认关闭，只有用户在设置中主动启用后才请求。
