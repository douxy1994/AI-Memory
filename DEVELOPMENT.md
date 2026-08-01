<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# AI Memory 开发指南

AI Memory 是一个双平台原生工程。macOS 与 Windows 共享产品行为和数据语义，但不共享 UI 框架，也不以网页壳实现界面。

## 技术基线

| 平台 | UI 与系统集成 | 数据与网络 | 包装 |
| --- | --- | --- | --- |
| macOS | Swift 6、SwiftUI、AppKit、ServiceManagement | SQLite3、Foundation、URLSession、Keychain | `.app` 与本地签名 universal DMG |
| Windows 11 | C#、WinUI 3、Windows App SDK | Microsoft.Data.Sqlite、HttpClient、PasswordVault | MSIX |

## macOS

### 要求

- macOS 14 或更高版本；
- Xcode 16 或更高版本；
- 可选：XcodeGen，用于修改 `project.yml` 后重新生成工程。

### 常用命令

```bash
# 重新生成 Xcode 工程（仅 project.yml 变化后需要）
xcodegen generate

# 完整测试
xcodebuild \
  -project AIMemory.xcodeproj \
  -scheme AIMemory \
  -configuration Debug \
  -destination 'platform=macOS' \
  -derivedDataPath .build/DerivedData \
  test

# Release 构建
xcodebuild \
  -project AIMemory.xcodeproj \
  -scheme AIMemory \
  -configuration Release \
  -destination 'platform=macOS' \
  -derivedDataPath .build/DerivedData \
  build

# 打包、固定本地签名、DMG 校验和 SHA-256
./script/package_macos_release.sh
```

### 目录职责

- `AIMemory/ViewControllers`：SwiftUI 页面和交互；
- `AIMemory/Stores/AppStore.swift`：单一应用状态与业务编排；
- `AIMemory/Persistence`：SQLite、设置、凭据、备份与导入；
- `AIMemory/Services`：Agent 历史、同步、迁移、更新与系统服务；
- `AIMemory/AIMemoryApp.swift`：生命周期、窗口、菜单和状态栏；
- `AIMemoryMCP`：随应用打包的原生 stdio MCP helper；
- `AIMemoryTests`：数据迁移、同步、更新、回收站和设置测试。

## Windows 11

### 要求

- Windows 11；
- Visual Studio 2022/2026 的 Windows application development 工作负载；
- Windows 11 SDK `10.0.26100` 或更高版本；
- .NET 10 SDK。

### 验证

```powershell
pwsh ./Windows/scripts/verify.ps1
```

该脚本验证核心测试、WinUI 工程、x64/ARM64 目标和功能矩阵。macOS 主机可以运行 Windows Core 测试，但 WinUI XAML 编译器必须在 Windows 主机执行。

## 数据兼容原则

1. `schemaVersion` / SQLite `user_version` 必须显式递增；
2. 迁移应幂等，可重复执行；
3. 替换数据库前先校验并建立恢复点；
4. 不在无备份的情况下覆盖 ChatMem 原始数据；
5. macOS 与 Windows 对相同字段保持同一语义；
6. 新字段必须为旧设置提供默认值或兼容读取路径。

## 提交前检查

- macOS 测试通过；
- Windows Core 测试通过；
- Windows 源码与 XAML 静态验证通过；
- 没有提交 `.build`、`bin`、`obj`、`tmp` 或本机用户数据；
- 可见按钮、菜单与设置项连接真实逻辑；
- README 与功能矩阵没有把 Preview 能力写成稳定发布；
- 新的 Release 必须同时说明支持的平台、最低系统版本和校验方式。

## 发布

macOS `v0.1.0` 已发布 universal DMG 与 SHA-256 校验文件。它使用项目固定的本地代码签名身份，未使用 Apple Developer ID 公证；发布说明必须如实标明首次打开的系统确认步骤。

后续发布流程至少包含：

1. 运行 `./script/package_macos_release.sh`，验证签名、双架构、敏感文件排除、DMG 与 SHA-256；
2. Windows MSIX 签名与真实 Windows 11 x64/ARM64 安装验证；
3. GitHub Release 资产名称包含 `AIMemory`、版本和架构；
4. Release notes 清楚说明数据库迁移和兼容边界；
5. 应用内更新源使用：
   `https://api.github.com/repos/douxy1994/AI-Memory/releases/latest`。
