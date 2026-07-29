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
- 原生通知区域图标：关闭主窗口后继续运行，可重新打开、立即同步或彻底退出；
- 可按来源切换的工作台、最近任务、四项核心指标，以及按 Windows、Mac、Linux、Internal、Other 自动识别的电脑分组；
- 可重命名电脑、合并电脑、移动单个项目并恢复自动分组，所有调整只影响展示，不改写原始项目路径；
- 字体设置全局应用于 WinUI，并兼容 macOS 的系统、思源黑体、思源宋体和霞鹜文楷标识；未安装字体由 Windows 自动回退；
- 使用 Windows App SDK MRT Core、`.resw` 与 `x:Uid` 提供完整简体中文和英文界面；
  可跟随 Windows 显示语言，也可在设置中单独选择并在重启后生效；
- 统一历史页中的对话、记忆、检查点、交接、运行、产物、经历与 Wiki，
  对话支持来源筛选、项目多选筛选、按项目/时间线排列、最近更新/创建/标题排序，
  所有来源型条目均可打开原始对话；
- 完整对话详情、收藏备注/标签/置顶/继续卡片，以及带确认、失败反馈和保留期说明的
  可恢复批量回收站；
- SQLite 数据库、可双向读取 macOS/Windows 键名的版本化设置和 ChatMem 安全导入；
- WebDAV 与本地文件夹增量同步；
- 无变化跳过、未变化文件使用 NTFS 硬链接复用的增量恢复点，支持定时备份、
  恢复前安全备份与失败自动回滚；
- Windows Credential Locker 保存 WebDAV 密码；
- 登录启动开关；
- 84 种主流 Agent、通用 AI CLI 与本地模型 CLI 安装检测，兼容无扩展、
  `.exe` 与 `.cmd` 启动器；
  已安装项优先，未安装项即使存在旧配置也保持关闭；
- 16 种具有稳定配置格式的 Agent 可安全启用或关闭 MCP；其中 11 种同时
  安装 AI Memory skill 与受管启动规则。已有配置先备份，部分安装可自动修复；
- Claude、Codex、Gemini、Kimi、Hermes、Antigravity、OpenCode 与 ZCode
  本地历史只读导入；
- 完整对话可原生复制到 Claude、Codex、Gemini 或 OpenCode，并在写入后
  重新导入核对消息数与首条用户消息；“移动”模式会把来源原始历史和完整
  数据快照放入可恢复回收站，恢复及永久删除均覆盖文件型与 SQLite 型来源；
- 总结式迁移可生成带来源证据的继续卡片，不伪装成目标 Agent 的原生历史；
- 记忆复核、检查点与交接核心流程；
- 打开对话后按来源刷新本地历史，并默认每两分钟更新该对话唯一的自动记忆恢复点；
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

## Windows 11 桌面生命周期验收

该测试必须在已登录的 Windows 11 桌面会话运行。它会验证首次启动只有一个
进程、关闭主窗口后进程继续驻留、再次启动不会创建第二个持久进程，并能恢复
原窗口。测试只清理自己启动的进程；检测到已有 AI Memory 时会直接停止。

验证构建输出：

```powershell
$app = Get-ChildItem `
  .\Windows\src\AIMemory.Windows\bin\Release `
  -Recurse -Filter AIMemory.Windows.exe |
  Select-Object -First 1
pwsh .\Windows\scripts\smoke-desktop.ps1 `
  -ExecutablePath $app.FullName
```

验证已安装的 MSIX：

```powershell
$appId = (Get-StartApps | Where-Object Name -eq "AI Memory").AppID
pwsh .\Windows\scripts\smoke-desktop.ps1 `
  -AppUserModelId $appId `
  -ProcessName "AIMemory.Windows"
```

## 项目

| 项目 | 职责 |
| --- | --- |
| `AIMemory.Core` | 数据模型、SQLite、同步、备份、导入、诊断和记忆治理 |
| `AIMemory.Windows` | WinUI 3 页面、窗口、菜单、系统集成与 MSIX |
| `AIMemory.Mcp` | Windows stdio MCP helper |
| `AIMemory.Core.Tests` | 核心数据与业务测试 |

默认数据目录为 `%LOCALAPPDATA%\AIMemory`。登录启动在包清单中默认关闭，只有用户在设置中主动启用后才请求。
