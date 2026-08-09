<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# AI Memory 0.1.3

AI Memory 0.1.3 改进本机历史自动接续、跨 Agent 迁移和 macOS 原生界面，并修复设置及历史页面中的多个一致性问题。

## 启动自动同步本机历史

- 每次打开应用都会检测当前 Mac 上存在可读数据的 Agent/CLI；
- 先从 AI Memory 独立索引恢复界面，再在后台增量导入全部已安装来源并刷新列表；
- 导入使用稳定会话标识幂等更新，手动“扫描/刷新”不再是正常启动的必要步骤；
- WebDAV 和本地文件夹仍只在用户已配置或主动触发时同步，启动导入不会自动上传历史。

## 动态迁移目标与 Kimi Code

- “迁移对话”的目标列表改为根据本机实际检测结果动态生成，不再显示与安装状态脱节的固定四项；
- 新增 Kimi Code 原生会话写入：生成会话状态、主 Agent wire 记录和 `session_index.jsonl` 登记；
- Kimi Code 与 Claude、Codex、Gemini、OpenCode 一样，在迁移后由 AI Memory 重新导入并核对消息数量和首条用户消息；
- 写入或回读失败会删除目标会话及 Kimi 索引项，保留源对话；未验证为可写的已安装格式会显示“当前格式只读”，不会报告虚假迁移成功。

## macOS Liquid Glass 与页面一致性

- macOS 26 及以上使用 SwiftUI 系统 Liquid Glass，较早系统自动回退到原生材质；
- 消息提示改为无色玻璃，移除突兀的绿色底色；
- 工作台和二级页面动作按钮统一使用系统玻璃按钮；
- 返回工作台按钮保留有色 prominent glass，避免在复杂内容上看不清；
- 修复历史页“资料库”与运行、经历、实体图谱等标签内容宽度不一致。

## 设置与关于

- 将重复的“更新与诊断”设置分类合并到“关于”页，更新检查、自动检查、安装和升级就绪诊断集中展示；
- 修复“登录时自动启动”开关：正确处理系统的需批准状态，并可直接打开 macOS“登录项”设置；
- 设置侧栏精简为通用、Agent 集成、数据同步与备份三类；更新、升级就绪和运行诊断集中到独立“关于”窗口。

## 下载与安装

- `AI-Memory-0.1.3-macOS-universal.dmg`：同时支持 Apple silicon 与 Intel Mac，要求 macOS 14 或更高版本；
- `AI-Memory-0.1.3-macOS-universal.dmg.sha256`：DMG 的 SHA-256 校验文件。

当前构建使用项目固定的本地代码签名身份，尚未使用 Apple Developer ID 公证。首次打开时如果 macOS 拦截，请在“系统设置 → 隐私与安全性”中确认打开。已安装 0.1.2 的用户也可以使用应用内更新入口升级。

## Windows 11

- `AI-Memory-0.1.3-Windows-x64.msix`：Windows 11 x64 原生 WinUI 3 客户端；
- `AI-Memory-0.1.3-Windows-x64.msix.sha256`：MSIX 的 SHA-256 校验文件。

Windows 版同步对齐了启动后台导入、动态迁移目标、Kimi Code 原生会话写入/回滚/恢复、Mica、统一历史页宽度，以及独立“关于”窗口。设置只保留通用、Agent 集成、数据同步与备份三个分类。真实 Windows 11 验收覆盖 95/95 核心测试、x64/ARM64 Release 构建、22 项 MCP 工具、MSIX 单实例和通知区域生命周期、实际 Agent 集成、登录启动开关及 WebDAV 增量同步。

Windows 资产按照交接验收要求提供 unsigned MSIX。请只从本 Release 下载安装包，并在安装前核对 SHA-256。

## 数据与隐私

Release 资产不包含密码、WebDAV 凭据、用户历史、数据库、设置文件、私钥或本机配置。应用数据继续保存在各平台独立的数据目录中，更新应用不会删除这些数据。
