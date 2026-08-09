# Windows Codex 0.1.3 功能对齐有限目标

把下面整段作为 Windows 11 实机 Codex（或 Kimi Code）的任务提示词使用。

---

你正在 Windows 11 实机继续开发原生 **AI Memory**。项目必须使用 C#、WinUI 3、Windows App SDK、.NET 10 和 MSIX，并与 macOS `v0.1.3`（提交 `dadcbe6`）的产品行为和数据语义对等。

先完整阅读：

1. `Windows/KIMI_HANDOFF.md`
2. `Windows/parity.json`
3. `Windows/README.md`
4. `docs/RELEASE_NOTES_0.1.3.md`
5. `.github/workflows/build.yml`
6. `Windows/scripts/verify.ps1`
7. `Windows/scripts/smoke-desktop.ps1`

## 唯一目标

在不重写已通过模块、不破坏 macOS 工程、不移动 `v0.1.3` 标签的前提下完成以下 Windows 对齐：

1. 窗口先显示缓存，随后每次主实例启动自动、幂等导入所有已安装且具有受支持历史格式的 Agent/CLI；不再依赖手动刷新，也不自动触发 WebDAV/本地目录上传；
2. 迁移目标从实时安装检测派生，排除源 Agent，显示已安装只读目标并禁用；
3. 新增 Kimi Code `state.json`、`agents/main/wire.jsonl`、`session_index.jsonl` 写入、回读验证、失败撤销、cut/恢复/永久清理；
4. 删除设置中的重复“更新与诊断”，把更新、就绪检查和诊断集中到关于窗口；
5. 使用 Windows 11 原生 Mica/Acrylic/主题资源完成无品牌色常规消息、二级按钮和有色突出返回工作台按钮；
6. 统一历史页全部标签的内容宽度；
7. 在注册 MSIX 下验证登录启动状态；
8. 将 Windows 版本统一为 `0.1.3`/`0.1.3.0`；
9. 完成 x64、ARM64、unsigned MSIX、packaged MCP 与桌面生命周期的既有三项实机门禁。

## 固定执行顺序

1. `git pull --ff-only`，确认 `dadcbe6` 是当前 HEAD 的祖先并记录工作树；
2. 运行一次 `pwsh .\Windows\scripts\verify.ps1`，保存完整基线和退出码；
3. 按“启动自动导入 → 动态目标/Kimi → 关于与材质 → 历史宽度 → 版本文档”顺序做局部修改和最小测试；
4. 运行核心测试及所有 Node 契约检查；
5. 在真实 Windows 11 交互会话构建 x64/ARM64、生成/注册 MSIX并运行桌面 smoke；
6. 做一次完整回归；若发现回归，只修回归后再运行最后一次完整回归；
7. 记录结果、更新 `Windows/parity.json` 和 Windows 文档后停止。

## 验收清单

- [ ] 启动前缓存可见，后台扫描不阻塞首屏；
- [ ] 不点击刷新即可导入至少 Claude/Codex/Kimi 三种测试来源；
- [ ] 第二实例只激活窗口，不开始第二次扫描；
- [ ] 第二次启动不产生重复会话；
- [ ] 单源损坏不阻断其余来源，warning 数真实；
- [ ] 启动导入不触发 WebDAV/本地目录同步；
- [ ] 迁移目标来自实时安装结果，排除源，已安装只读项可见但禁用；
- [ ] Kimi copy 写入目录和动态索引，并通过回读验证；
- [ ] Kimi 失败撤销、cut、回收站恢复和永久清理通过；
- [ ] 设置中不存在“更新与诊断”分类，功能都在关于窗口可操作；
- [ ] 常规消息无绿色品牌底，warning/error 仍具可访问语义；
- [ ] 主窗口/浮层采用 Windows 11 原生材质并具备降级路径；
- [ ] 二级页面按钮统一，返回工作台按钮有足够对比度；
- [ ] 资料库、运行、实体图截图的内容左右边界一致；
- [ ] `StartupTask` 开启、用户禁用、策略禁用和系统设置入口实测；
- [ ] Windows 显示版本 `0.1.3`，MSIX 为 `0.1.3.0`，MCP serverInfo 为 `0.1.3`；
- [ ] `pwsh Windows/scripts/verify.ps1` 通过；
- [ ] x64/ARM64、unsigned MSIX、packaged MCP 22 工具通过；
- [ ] AppsFolder 启动后的单实例、可见窗口、关闭到通知区域和二次启动恢复通过；
- [ ] `Windows/parity.json` 仅按真实证据更新；
- [ ] 最终修改、命令、输出、退出码、截图、产物和回滚路径已记录。

## 边界

- 不转为 Electron、网页壳、Flutter、Qt 或 Python GUI；
- 不修改 macOS 行为来迁就 Windows；
- 不把 165 种安装检测误写成 165 种历史读取器；当前只对具有可靠读取器的来源自动导入；
- 不把残留配置当作已安装，不为未知格式伪造迁移成功；
- 不用 macOS 测试、静态检查或未注册裸 exe 代替 Windows 11 桌面证据；
- 不为清零警告重构已通过模块；
- 不自动发布 Windows 安装包，发布须另行确认。

全部验收项通过后，输出“Windows 0.1.3 功能对齐完成”及证据并停止；若仍有阻断，保留最后可编译状态，列出原始错误、已尝试的不同路径和未受影响部分后停止。

---
