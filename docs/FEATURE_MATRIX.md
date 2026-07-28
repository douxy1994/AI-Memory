# 功能矩阵

状态说明：**已实现**表示源码和最小验证已存在；**Preview** 表示实现已进入仓库，但仍需目标平台完整打包或体验收敛；**规划中**不作为当前可用能力宣传。

| 能力 | macOS | Windows 11 |
| --- | --- | --- |
| 原生桌面 UI | 已实现：SwiftUI + AppKit | Preview：WinUI 3 |
| 单实例与窗口恢复 | 已实现 | 已实现 |
| 工作台与项目归组 | 已实现 | Preview |
| 对话搜索与详情 | 已实现 | Preview |
| 收藏与可恢复回收站 | 已实现 | Preview |
| Claude/Codex/Gemini 历史 | 已实现 | 已实现核心 |
| Kimi/Hermes/Antigravity/OpenCode/ZCode 历史 | 已实现 | 已实现核心 |
| 41 种 Agent/CLI 安装检测 | 已实现集成目录 | 已实现检测目录 |
| 候选记忆复核 | 已实现 | Preview |
| 检查点与交接 | 已实现 | Preview |
| MCP helper | 已实现 | Preview |
| WebDAV 增量同步 | 已实现 | 已实现核心 |
| 本地文件夹增量同步 | 已实现 | 已实现核心 |
| 增量备份与恢复 | 已实现 | 已实现核心 |
| ChatMem 数据导入 | 已实现 | 已实现核心 |
| 系统凭据存储 | Keychain | Credential Locker |
| 登录时启动 | 已实现 | 已实现 |
| GitHub 更新检查 | 已实现 | 已实现核心 |
| 签名公开安装包 | 暂未发布 | 暂未发布 |

Windows 标记为 Preview 的原因是 WinUI 桌面端仍需要在真实 Windows 11 x64/ARM64 主机完成完整构建、安装和交互验证。
