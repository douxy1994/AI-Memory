<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# 项目来源与迁移

## 来源

AI Memory 来源于 [Rimagination/ChatMem](https://github.com/Rimagination/ChatMem)。

ChatMem 建立了本地优先 AI 编程记忆层的核心方向：聚合 Agent 历史、沉淀项目知识、生成检查点与交接，并通过 MCP 把上下文带回新的会话。AI Memory 保留这一产品目标，同时对客户端、数据边界和平台集成进行了完整重构与功能更新。

## 重构范围

- macOS 从跨平台 UI 重写为 Swift 6、SwiftUI 与 AppKit；
- Windows 以 C#、WinUI 3 与 Windows App SDK 独立实现；
- 原生 SQLite 数据层替代对旧运行时桥接的依赖；
- 同步改为记录级增量上传、下载和跳过；
- 备份、恢复、导入和迁移增加校验、版本与原始数据保护；
- 扩展 Agent/CLI 检测、历史读取、记忆治理、检查点和交接；
- 使用 AI Memory 独立的名称、应用身份、数据目录和凭据空间。

## 与 ChatMem 的关系

AI Memory 不是对 `Rimagination/ChatMem` 的官方发布，也不代表原作者。本仓库按
GNU Affero General Public License v3.0 发布，并在用户界面和文档中使用自己的品牌。

## 数据迁移

从 ChatMem 导入时：

1. AI Memory 只读打开 ChatMem 源数据；
2. 检查数据库结构和可读性；
3. 备份 AI Memory 当前数据；
4. 将导入内容写入 AI Memory 独立目录；
5. 写入失败时保留原数据并恢复稳定状态。

不应把 AI Memory 数据目录直接指向 ChatMem，也不应在没有备份的情况下覆盖 ChatMem 数据。
