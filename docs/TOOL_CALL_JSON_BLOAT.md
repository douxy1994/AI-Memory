<!--
Copyright © 2026 douxy1994
SPDX-License-Identifier: AGPL-3.0-only
-->

# tool_calls.input_json 递归转义膨胀（P0 数据损坏）

> 给接手修复的 AI/开发者：本文档自包含。按「根因 → 修复 → 迁移 → 回归」顺序执行即可，不需要先读其他文档。
> 实测发现于 2026-08-03，用户库 `aimemory.db` 因此从 ~200 MB 膨胀到 19 GB。

## 1. 现象

单个用户库的实测数据：

| 指标 | 数值 |
| --- | --- |
| `aimemory.db` 实际大小 | 19.0 GB |
| `tool_calls` 表占用 | 12.9 GB（占全库 99.7%） |
| `tool_calls` 行数 | 26,924 |
| 真实内容总量（还原后） | **1.9 MB** |
| 最大单条 `input_json` | **381,962,721 B（364 MB）** |
| 该条还原后 | 20,370 B |
| 放大倍数 | 最高 **45,892×** |

其余所有表（`memory_entities`、`document_embeddings`、`messages`、FTS 索引）合计仅几十 MB。**膨胀 100% 来自 `input_json`，与 `output_text` 无关。**

膨胀记录的内容形如：

```
"\"\\\"\\\\\\\"\\\\\\\\\\\\\\\"\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\"..."
```

取前 100,000 字符去掉所有反斜杠后只剩 **32 个字符**——99.97% 是纯转义符。

长度呈严格的 2 的幂分布，证实每轮翻倍：

```
381,962,721 = 2^28.51      146,805,009 = 2^27.13
227,551,679 = 2^27.76      141,301,739 = 2^27.07
166,726,581 = 2^27.31      138,415,956 = 2^27.04
```

对一条 2,621,661 B 的记录反复 `json.loads`，每轮精确减半，19 轮后还原出 225 B 的真实内容：

```
2,621,661 → 1,310,941 → 655,581 → 327,901 → ... → 301 → 225
```

还原结果是合法的工具输入（一段 node_repl JS 代码），**数据没有丢失，只是被 19 层引号包裹**。

## 2. 根因

放大发生在 macOS 端「读取 → 回写」的闭环里，涉及三处代码。

### 2.1 读取：解析失败后回退返回原始文本

`AIMemory/Persistence/NativeConversationStore.swift:1805`

```swift
private static func jsonObject(from text: String) -> Any {
    guard let data = text.data(using: .utf8),
          let value = try? JSONSerialization.jsonObject(with: data)
    else { return text }        // ← BUG 起点
    return value
}
```

`JSONSerialization.jsonObject(with:)` **不传 `.fragmentsAllowed` 时拒绝顶层片段**。JSON 顶层是字符串、数字、布尔、null 的都会抛错。

`input_json` 恰恰几乎全是顶层字符串。实测该库全部 3,329 条 `exec` 记录的 `input_json` 首字符都是 `"`，**没有一条是 object**——因为 `exec`/`node_repl` 这类工具的输入本身就是一段代码文本，序列化后就是顶层 JSON 字符串。

于是每次读取都走 `else` 分支，返回**带引号的原始文本**（`String` 类型），而不是解析后的值。

调用点有三处：`:300`、`:385`、`:463`。

### 2.2 中转：String 被 JSONValue 忠实接收

`AIMemory/Models/ChatMemModels.swift:276`

```swift
init(from decoder: Decoder) throws {
    let c = try decoder.singleValueContainer()
    if c.decodeNil() { self = .null }
    else if let v = try? c.decode(Bool.self) { self = .bool(v) }
    else if let v = try? c.decode(Double.self) { self = .number(v) }
    else if let v = try? c.decode(String.self) { self = .string(v) }   // ← 命中
    ...
}
```

上一步返回的带引号文本被装进 `.string(...)`。此时 `ToolCall.input` 已经比真实值多了一层引号。

### 2.3 回写：再编码一次

`AIMemory/Persistence/NativeConversationStore.swift:1205`

```swift
let input = (try? encoder.encode(tool.input))
    .flatMap { String(data: $0, encoding: .utf8) } ?? "null"
```

`JSONValue.encode(to:)` 的 `.string` 分支执行 `c.encode(v)`，把字符串编码成 JSON 字符串字面量——**再加一层引号并转义内部所有引号和反斜杠，长度翻倍**。

### 2.4 闭环

```
input_json = "code"                      (1 层，正确)
  ↓ 读：fragment 解析失败 → 回退返回 `"code"` 含引号
  ↓ JSONValue = .string("\"code\"")
  ↓ 写：encode → "\"code\""              (2 层)
  ↓ 读：仍是 fragment，仍失败
  ↓ 写：                                  (4 层)
  ↓ ...                                   (2^n 层)
```

**每次触发 export/import/sync 循环，长度翻倍。** 19 轮即从 225 B 涨到 2.6 MB，28 轮涨到 364 MB。

关键点：这不是「日志攒多了」，而是**同一条记录被反复重写**。所以加保留期、定期删旧数据**都不解决问题**——单条记录就能吃掉几百 MB。

### 2.5 触发路径

任何调用 `readConversation` / `exportAllConversationsForSync` 后又写回的流程都会推进一轮：

- WebDAV/本地同步的 export → import 往返
- `NativeHistoryImporter` / `NativeAdditionalHistoryImporter` 重复导入
- 任何「读出会话详情 → 修改 → 保存」的路径

### 2.6 Windows 端：不同的 bug，同一处设计缺陷

`Windows/src/AIMemory.Core/Persistence/ConversationRepository.cs:401`

```csharp
try {
    using var document = JsonDocument.Parse(reader.GetString(2));
    input = document.RootElement.Clone();
} catch (JsonException) {
    input = JsonSerializer.SerializeToElement<object?>(null);   // ← 静默丢弃
}
```

`System.Text.Json` 的 `JsonDocument.Parse` **默认允许顶层片段**，所以 Windows 端不会膨胀。但它的失败分支**把无法解析的输入静默替换成 `null`，直接丢数据**。

两端对「input_json 不是合法 JSON」的处理都是错的，只是错的方向相反：macOS 无限膨胀，Windows 静默丢失。**修复必须同时覆盖两端**，否则跨平台同步会让一端的数据在另一端消失。

## 3. 修复

### 3.1 macOS：读取允许顶层片段（必须）

`NativeConversationStore.swift:1805`

```swift
private static func jsonObject(from text: String) -> Any {
    guard let data = text.data(using: .utf8) else { return NSNull() }
    if let value = try? JSONSerialization.jsonObject(
        with: data, options: [.fragmentsAllowed]
    ) {
        return value
    }
    // 真正不是 JSON 的历史脏数据：作为纯文本值返回，绝不返回原始带引号文本
    return text
}
```

加 `.fragmentsAllowed` 后，`"code"` 会正确解析成 Swift `String` `code`（不含引号），回写时再编码回 `"code"`，**长度稳定，闭环消失**。

### 3.2 macOS：回写前做幂等断言（防御）

即使 3.1 修好，也应阻止异常长度写入数据库。`NativeConversationStore.swift:1205` 附近：

```swift
for tool in message.toolCalls {
    var input = (try? encoder.encode(tool.input))
        .flatMap { String(data: $0, encoding: .utf8) } ?? "null"

    // 防御：单条工具输入不应超过 1 MB。超限说明遇到了递归转义或异常大的载荷，
    // 截断并留下可诊断的标记，绝不让它进库继续翻倍。
    if input.utf8.count > Self.maxToolInputBytes {
        let original = input.utf8.count
        input = Self.jsonString([
            "_truncated": true,
            "_original_bytes": original,
            "_preview": String(input.prefix(2000)),
        ])
    }
    ...
}
```

配套常量：

```swift
/// 单条 tool_call 输入的入库上限。超过此值几乎必然是异常数据。
static let maxToolInputBytes = 1_048_576
```

`output_text` 建议同样处理（当前实测未膨胀，但同样无上限）。

### 3.3 Windows：解析失败不得丢数据

`ConversationRepository.cs:401`

```csharp
JsonElement input;
var raw = reader.GetString(2);
try
{
    using var document = JsonDocument.Parse(raw);
    input = document.RootElement.Clone();
}
catch (JsonException)
{
    // 不要丢成 null——把原始文本作为 JSON 字符串保留，
    // 与 macOS 端的回退行为保持一致，避免跨端同步时数据消失。
    input = JsonSerializer.SerializeToElement(raw);
}
```

### 3.4 数据迁移：还原历史脏数据

修代码只阻止继续翻倍，**已膨胀的记录必须单独还原**。加一次性迁移（Swift/C# 实现思路等价于下面已验证的 Python 脚本）：

```python
def unwrap(text):
    """反复 json.loads 直到解码失败或解出非字符串。返回 (最内层值, 剥离层数)。"""
    cur, depth = text, 0
    while True:
        try:
            v = json.loads(cur)
        except Exception:
            break
        if not isinstance(v, str):
            break          # 解出 object/array，这层是真实结构，停止
        cur = v
        depth += 1
        if depth > 200:    # 安全上限
            break
    return cur, depth

# 还原：json.dumps(inner) —— 恢复成与未受污染记录一致的单层编码
```

**这个算法对正常记录是幂等的**：正常记录 `"code"` 剥 1 层得 `code`，再解失败即停，`dumps` 回去与原值相同。所以可以安全地全表跑。

实测结果（26,924 行全表）：

```
候选（>10 KB）  1,136 条
可还原            963 条
跳过（已正常）    173 条
还原前     13,111.9 MB
还原后          1.9 MB
回收         12.80 GB
```

迁移后必须 `VACUUM`——SQLite 不会自动把空闲页还给文件系统。实测 `VACUUM` 后 `aimemory.db` 从 13 GB 降到 **183 MB**，`PRAGMA quick_check` 返回 `ok`，26,924 行全部保留。

### 3.5 备份策略（次要但必须一起改）

`backups/` 下每份自动备份都是主库的**完整副本**。主库 19 GB 时，10 份自动备份 = 63 GB，比主库本身还严重。建议：

- 备份份数上限（如保留最近 3 份）+ 按总体积上限双重约束
- 主库超过阈值（如 2 GB）时先告警，不要无声地复制
- 优先用 SQLite `VACUUM INTO` 生成紧凑备份，而非文件级 `copy`

### 3.6 WAL 增长

实测 `aimemory.db-wal` 独立涨到 21 GB。直接原因是泄漏的 `aimemory-mcp` helper 进程长期持有连接，checkpoint 无法完成（实测残留 14 个，AIMemory 主程序当时并未运行）。建议：

- helper 进程退出时确保关闭连接；主程序退出时回收所有 helper
- 定期 `PRAGMA wal_checkpoint(TRUNCATE)`
- 启动时检测 WAL 体积异常并告警

## 4. 回归测试

至少覆盖以下四条，缺一条都可能让 bug 复活。

```swift
func testTopLevelJSONStringSurvivesRoundTripUnchanged() throws {
    // 核心回归：顶层 JSON 字符串往返 N 次，长度必须恒定
    let store = try makeStore()
    let original = JSONValue.string("const x = 1;\nconsole.log(\"hi\");")
    var conversation = makeConversation(toolInput: original)

    var previousLength = -1
    for round in 0..<5 {
        try store.save(conversation)
        conversation = try store.readConversation(id: conversation.id)
        let length = try store.rawInputJSON(toolCallID: conversation.messages[0].toolCalls[0].id).utf8.count
        if round > 0 {
            XCTAssertEqual(length, previousLength, "第 \(round) 轮长度变化，递归转义回归了")
        }
        previousLength = length
    }
    XCTAssertEqual(conversation.messages[0].toolCalls[0].input, original)
}

func testFragmentTypesAllRoundTrip() throws {
    // 数字、布尔、null 同样是顶层片段，同样会命中原 bug
    for value in [JSONValue.number(42), .bool(true), .null, .string("plain")] {
        try assertRoundTripStable(input: value)
    }
}

func testOversizedInputIsTruncatedNotStored() throws {
    let huge = JSONValue.string(String(repeating: "x", count: 4_000_000))
    let stored = try storeAndReadRaw(input: huge)
    XCTAssertLessThan(stored.utf8.count, 1_100_000)
    XCTAssertTrue(stored.contains("_truncated"))
}

func testMigrationUnwrapsHistoricalBloatAndIsIdempotent() throws {
    // 20 层包裹的脏数据 → 还原成单层；再跑一次迁移结果不变
    let bloated = (0..<20).reduce("\"payload\"") { acc, _ in jsonEncode(acc) }
    let once = try runMigration(on: bloated)
    XCTAssertEqual(once, "\"payload\"")
    XCTAssertEqual(try runMigration(on: once), once)
}
```

Windows 端对应补 `ConversationDetailRepositoryTests`：非法 JSON 的 `input_json` 读出后**不得为 null**。

## 5. 验收标准

修复完成后，逐条核对：

1. `jsonObject(from:)` 传了 `.fragmentsAllowed`，三处调用点（`:300`、`:385`、`:463`）行为一致
2. 写入路径有长度上限，超限截断并标记
3. Windows 端解析失败保留原文，不再写 `null`
4. 迁移脚本跑完 + `VACUUM`，`SELECT MAX(LENGTH(input_json)) FROM tool_calls` 回到 10^5 量级
5. 上述回归测试全绿，尤其是「往返 5 次长度恒定」
6. 备份份数/体积有上限
7. 连续触发 10 次同步后 `aimemory.db` 大小稳定

自检 SQL：

```sql
-- 应远小于 1 MB；修复前该库是 381,962,721
SELECT MAX(LENGTH(input_json)) FROM tool_calls;

-- 应接近 0；修复前 1,136 条
SELECT COUNT(*) FROM tool_calls WHERE LENGTH(input_json) > 10000;

-- 首字符仍会大量是 `"`（顶层字符串是合法的），但长度必须正常
SELECT SUBSTR(input_json,1,1) AS c, COUNT(*), MAX(LENGTH(input_json))
FROM tool_calls GROUP BY c;
```

## 6. 已在用户机器上执行的操作（2026-08-03）

避免重复劳动，以下**数据侧**修复已完成，**代码侧未改动**：

- 还原 963 条膨胀记录，`aimemory.db` 19 GB → 183 MB，`quick_check` 通过，26,924 行全保留
- `PRAGMA wal_checkpoint` 清空 21 GB WAL
- 清理 `backups/` 全部历史备份、20 个散落的 `aimemory.db.bak-*`
- 终止 14 个泄漏的 `aimemory-mcp` helper 进程

**代码侧第 3 节全部未做，bug 仍在。** 只要再触发一轮同步，膨胀会重新开始。

### 清理 helper 进程的注意事项

`pgrep -f "aimemory-mcp"` 会匹配到 Claude Code 的会话进程——它们的 `--mcp-config` JSON 命令行里含 `aimemory-mcp` 这个路径字符串。实测 28 个匹配里只有 14 个是真的 helper，**`pkill -f "aimemory-mcp"` 会连带杀掉 14 个 Claude Code 会话**。正确做法是按可执行名精确匹配：

```bash
ps -axo pid=,comm= | awk '$2 ~ /aimemory-mcp$/ {print $1}'
```
