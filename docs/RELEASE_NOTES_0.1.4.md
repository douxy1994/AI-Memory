<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# AI Memory 0.1.4

AI Memory 0.1.4 是 macOS 窗口生命周期修复版本，解决应用仍在运行但主窗口消失、点击 Dock 或菜单栏入口没有反应的问题。

## 主窗口恢复

- 主界面改为具有固定标识的单实例 SwiftUI `Window`，避免窗口组在后台生成或遗留重复窗口；
- 精确捕获并强引用主窗口，关闭按钮只隐藏窗口，不再丢失可恢复的 `NSWindow`；
- 点击 Dock、重新打开应用或使用菜单栏“打开 AI Memory”时，恢复隐藏、最小化或已关闭的窗口；
- 主窗口确实被系统释放时，通过保存的 `openWindow(id:)` 动作重新创建；
- 重复启动继续复用现有进程，保持一个主窗口和一个活跃应用实例。

## 验证

- 连续三轮关闭主窗口并点击 Dock，进程号保持不变且窗口均成功恢复；
- 最小化、隐藏应用、菜单栏恢复和重复启动均通过真实界面测试；
- macOS 测试套件 72 项全部通过；
- Release 为 Apple silicon 与 Intel 通用二进制。

## 下载与安装

- `AI-Memory-0.1.4-macOS-universal.dmg`：支持 Apple silicon 与 Intel Mac，要求 macOS 14 或更高版本；
- `AI-Memory-0.1.4-macOS-universal.dmg.sha256`：DMG 的 SHA-256 校验文件。

当前构建使用项目固定的本地代码签名身份，尚未使用 Apple Developer ID 公证。首次打开时如果 macOS 拦截，请在“系统设置 → 隐私与安全性”中确认打开。应用内更新会下载、验证并替换现有安装，用户数据目录保持不变。

## Windows 11

本版本不包含 Windows 安装包；Windows 原生客户端仍处于 Preview。

## 数据与隐私

Release 资产不包含密码、WebDAV 凭据、用户历史、数据库、设置文件、私钥或本机配置。
