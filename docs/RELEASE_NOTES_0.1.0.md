<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# AI Memory 0.1.0

AI Memory 0.1.0 是原生 macOS 客户端的首个公开版本。应用使用 Swift、SwiftUI、AppKit 与 Apple 官方 Framework 构建，面向多个 AI Agent 和 CLI 提供本地优先的历史、记忆、检查点与接续工作台。

## macOS

- 聚合 Claude、Codex、Gemini、Kimi、Hermes、Antigravity、OpenCode 与 ZCode 本地历史；
- 提供工作台、统一历史检索、收藏、回收站、项目记忆、检查点、交接包、运行产物与 Wiki；
- 支持 Agent/CLI 安装检测，已安装项目优先显示，未安装项目默认不启用；
- 支持增量 WebDAV、本地文件夹同步、恢复点备份、校验恢复与 ChatMem 数据安全导入；
- 提供原生菜单、快捷键、菜单栏图标、单实例窗口、登录启动与 GitHub Releases 更新检查；
- 提供 22 项本地 MCP 工具，供受支持的 Agent 检索历史并恢复项目上下文。

## 下载与安装

- `AI-Memory-0.1.0-macOS-universal.dmg`：同时支持 Apple silicon 与 Intel Mac，要求 macOS 14 或更高版本；
- `AI-Memory-0.1.0-macOS-universal.dmg.sha256`：DMG 的 SHA-256 校验文件。

当前构建使用项目固定的本地代码签名身份，尚未使用 Apple Developer ID 公证。首次打开时如果 macOS 拦截，请在“系统设置 → 隐私与安全性”中确认打开。

## Windows 11

Windows 11 原生客户端正在使用 C#、WinUI 3 与 Windows App SDK 开发。x64 与 ARM64 构建和核心自动化测试已经完成，但本版本暂不提供 Windows 安装包；完成真实 Windows 11 桌面安装与交互验收后再单独发布。

## 数据与隐私

Release 资产不包含密码、WebDAV 凭据、用户历史、数据库、设置文件、私钥或本机配置。AI Memory 默认在本机保存数据；只有用户明确执行同步、迁移、删除或 Agent 集成安装时才会修改对应目标。
