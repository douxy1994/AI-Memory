# Kimi Code Windows 端有限目标

把下面整段作为 Windows 端 Kimi Code 的任务提示词使用。

---

你正在 Windows 11 实机继续开发原生 **AI Memory**。项目必须使用 C#、WinUI 3、Windows App SDK、.NET 10 和 MSIX，并与当前 macOS AI Memory 的功能与数据语义对等。

先完整阅读：

1. `Windows/KIMI_HANDOFF.md`
2. `Windows/parity.json`
3. `Windows/README.md`
4. `.github/workflows/build.yml`
5. `Windows/scripts/verify.ps1`
6. `Windows/scripts/smoke-desktop.ps1`

## 唯一目标

在不重写已通过模块、不破坏 macOS 工程、不扩大产品范围的前提下，在真实 Windows 11 环境完成 Windows 原生版剩余的三项强验证，并修复验证过程中发现的真实阻断问题：

1. x64 与 ARM64 WinUI 3 Release 构建及 x64 unsigned MSIX；
2. 构建产物内 packaged `aimemory-mcp.exe` 的 22 工具运行 smoke；
3. 注册 MSIX/生成清单后经 AppsFolder 启动的单实例、窗口可见、关闭到通知区域、二次启动恢复生命周期 smoke。

同时维持以下不变量：

- Agent/CLI 清单两端 ID 一致；
- 未安装项目默认关闭；
- 已安装项目排序靠前；
- 不把残留配置误判为已安装且启用；
- WebDAV 与本地目录仍为增量同步；
- 所有用户数据和凭据保持独立并可恢复；
- `Copyright © 2026 douxy1994` 与 `AGPL-3.0-only` 保持完整。

## 固定执行顺序

### 1. 校验接手版本

```powershell
git status --short --branch
git rev-parse HEAD
git log --oneline -8
```

HEAD 必须是 `033bde5` 或其后继，并包含交接文档列出的关键提交。版本较旧时，只报告缺少的提交和当前 HEAD，停止在旧代码上开发。

### 2. 运行一次完整基线

```powershell
pwsh .\Windows\scripts\verify.ps1
```

保存每条命令、退出码和原始错误。已经通过的步骤不重写。

### 3. 复现 MSIX 与桌面 smoke

严格复用 `.github/workflows/build.yml` 的 `Build unsigned MSIX package` 与 `Register WinUI package for desktop smoke` 步骤，在有 Explorer 的交互式 Windows 11 会话运行 `smoke-desktop.ps1`，并保存：

- x64/ARM64 构建输出；
- MSIX、AppxManifest 和 helper 路径；
- `AIMemorySourceRevision.txt` 内容；
- smoke JSON 结果；
- `%LOCALAPPDATA%\AIMemory\startup.log`；
- 所有退出码。

### 4. 只修真实失败

对每个独立缺陷最多进行 **3 次有实质差异的修复尝试**：

- 第 1 次：根据错误和日志修直接根因；
- 第 2 次：更换技术路径，不重复只调参数；
- 第 3 次：最小化复现后做最后一次局部修复。

每次修改后先跑最小相关测试。第 3 次仍失败时：

1. 回到最后一个可编译状态；
2. 记录错误、影响范围、三次不同尝试和明确阻断点；
3. 继续处理其他互不依赖的验收项；
4. 不再对该缺陷开始第 4 轮。

### 5. 最多两次完整回归

- 所有局部修复完成后运行第 1 次完整 `verify.ps1` + MSIX/桌面 smoke；
- 如果第 1 次完整回归暴露新的回归，只修这些回归；
- 修复后运行第 2 次也是最后一次完整回归；
- 第 2 次通过后立即进入收尾；若仍有失败，按阻断项记录，不再循环全量检查。

## 明确禁止的循环

- 不重新审计整个 macOS 与 Windows 代码库；
- 不重构已通过模块；
- 不为了清零警告反复重写；
- 不因个人代码风格调整目录、命名或架构；
- 不重复运行相同命令却不产生新证据；
- 不增加用户未要求的新产品功能；
- 不在验收通过后继续微调 UI；
- 不自动推送、发布 Release 或删除远端内容。

## 验收清单

以下项目逐项记录 `PASS`、证据路径和退出码：

- [ ] 当前 HEAD 为 `033bde5` 或后继，工作树状态已记录；
- [ ] `pwsh Windows/scripts/verify.ps1` 通过；
- [ ] Windows Core 测试全部通过；
- [ ] WinUI XAML/code-behind 检查通过；
- [ ] x64 Release 构建通过；
- [ ] ARM64 Release 构建通过；
- [ ] x64 unsigned MSIX 生成；
- [ ] x64 与 ARM64 helper 架构匹配；
- [ ] packaged MCP helper 初始化成功并返回 22 个工具；
- [ ] AppsFolder 启动后只有一个进程；
- [ ] 主窗口可见且标题含 `AI Memory`；
- [ ] `startup.log` 含 `launch.complete`；
- [ ] 关闭窗口后进程保留、窗口隐藏；
- [ ] 二次启动恢复同一进程和窗口；
- [ ] 通知区域的打开、同步、退出可用；
- [ ] 已安装 Agent/CLI 排在未安装项目之前；
- [ ] 至少抽查 3 个未安装 Agent/CLI，均默认关闭；
- [ ] WebDAV 密码可粘贴，验证显示真实结果；
- [ ] WebDAV 和本地目录同步显示过程及上传/下载/跳过结果；
- [ ] 重启后设置和数据恢复；
- [ ] `Windows/parity.json` 只根据真实证据更新；
- [ ] 最终工作树、修改文件、测试结果和剩余问题已记录。

## 自动终止条件

满足以下任一条件后立即停止本轮开发并输出最终报告：

### A. 完成

全部验收项通过，`Windows/parity.json` 的三项 `verification-required` 有真实 Windows 证据并更新为 `implemented`。输出：

1. 修改文件；
2. 三项验证的命令、退出码与证据路径；
3. 人工冒烟结果；
4. 数据兼容说明；
5. 最终结论“Windows 版验收完成”。

输出后停止，不再继续优化。

### B. 有界收敛

任一缺陷已完成三次不同修复尝试，或已经执行两次完整回归仍有失败。保留最后可编译状态，输出：

1. 已通过项目；
2. 未通过项目；
3. 原始错误和证据路径；
4. 三次不同尝试；
5. 具体阻断点；
6. 不受影响的剩余功能状态；
7. 最终结论“Windows 版部分完成，存在明确阻断”。

输出后停止，不开始新的审计或第 4 次修复。
---
