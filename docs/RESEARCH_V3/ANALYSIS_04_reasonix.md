# ANALYSIS 04 — DeepSeek-Reasonix v1.17.15 深度拆解

> **来源**: [esengine/DeepSeek-Reasonix](https://github.com/esengine/DeepSeek-Reasonix)  
> **最新版本**: v1.17.15 (2026-07-18)  
> **许可证**: MIT  
> **GitHub Stars**: 13,900+ (80+ 贡献者, 32 releases)  
> **状态**: 正在 clone 到 `D:/minimax/代码/AeroCodeV3_externals/DeepSeek-Reasonix`  
> **官网**: reasonix.io / esengine.github.io/DeepSeek-Reasonix  

---

## 1. 定位与差异化

**Reasonix** = **"DeepSeek 原生编程 Agent"**。

**核心设计哲学**:
> "Cache stability isn't a feature you turn on; it's an invariant the loop is designed around."

**三大核心机制** (Cache-First Loop / Tool-Call Repair / R1 Thought Harvest):
1. **Cache-First Loop** — append-only 运行循环, 历史消息只追加不修改
2. **Tool-Call Repair** — schema-aware 自动修复畸形工具调用
3. **R1 Thought Harvest** — 捡回推理链里逃逸的工具调用

**实测数据** (2026-05-01, 真实用户单日):
- 输入 token: **4.35 亿**
- Cache hit 率: **99.82%**
- 实际花费: **~$12**
- 同等无缓存负载: ~$61
- **省 5 倍** (V4-Flash 缓存后 $0.014/M, 未缓存 $0.07/M)

---

## 2. 核心架构

### 2.1 Cache-First Loop (核心创新)

```
普通 Agent 循环:                     Reasonix Cache-First Loop:
  user msg                              user msg
  ↓                                     ↓
  LLM call (前缀 = 之前所有)             LLM call (前缀 = 之前所有)
  ↓                                     ↓
  tool_call                             tool_call
  ↓                                     ↓
  tool result (append)                  tool result (append)  ← 只追加
  ↓                                     ↓
  LLM call (前缀 = 之前所有)             LLM call (前缀 = 之前所有) ← 完全相同前缀
  ↓                                     ↓
  context 变了 (前缀变了)                context 没变 (前缀相同)
  cache MISS!                           cache HIT! (V4-Flash $0.014)
  (V4-Flash $0.07)
```

**核心 invariant**: **消息和工具结果全部 append, 从不 mutate**。

实现细节:
1. **append-only** — 消息、工具结果一律尾部追加, 绝不修改历史
2. **no marker** — 不使用 cache_control 之类的标记触发器
3. **stable order** — 工具调用顺序与时间戳完全确定性
4. **prefix-survive** — 即使 dispatch 多次工具, 前缀仍命中

**移植价值**: 这是 AeroCode V3.0 节省 token 成本的关键。

### 2.2 Tool-Call Repair (自愈机制)

```typescript
// 4 轮内部处理
1. parse — JSON5 / 容错解析, 识别常见畸形写法
2. reshape — 按 schema 重排字段名, 修补默认值
3. retry — 修复失败时优雅回报, 让模型自我纠正
4. log — 所有修复动作可在 reasonix replay 中回放
```

**为什么重要**: DeepSeek R1 偶尔会输出格式不正确的 tool call (JSON 错误、字段缺失、类型不匹配)。普通 agent 要么报错要么重试 (成本翻倍)。Reasonix 自动修复, 不走重试, 不破坏 prefix cache。

**移植到 AeroCode**:
- 在 Provider 层加 `ToolCallRepairer`
- 输入: raw tool_calls from LLM
- 输出: repaired tool_calls
- 修复策略: schema-aware JSON 解析, 字段填充, 类型转换

### 2.3 R1 Thought Harvest (推理链回收)

```typescript
// DeepSeek R1 思维链特点:
// 推理过程中想到了要调用工具, 但没走 tool_call 路径
// 1<think>{"name": "write_file", "args": {...}}</think> (有工具调用语法但没正式发出)

// Reasonix scavenge pass
function harvest(thinkBlock: string): ToolCall[] {
  // 扫描 <think> 块, 识别工具调用语法
  // 抓出, 重新走 dispatch 通道
}
```

**移植到 AeroCode**:
- 解析 DeepSeek `reasoning_content` 字段
- 提取其中的 tool-call 语法
- 与正式 tool_call 合并 dispatch

### 2.4 Auto 模型切换 (V4-Flash / V4-Pro)

```
用户任务
  ↓
任务复杂度评估:
  - token 长度 < 4K?     → V4-Flash (默认, 便宜)
  - 工具调用 > 10 次?     → V4-Pro (强)
  - 代码生成 / 架构?      → V4-Pro
  ↓
命令级 override:
  - /pro 单回合切到 Pro
  - /preset max 整个 session Pro
  ↓
轮次结束自动压缩上下文
```

**移植到 AeroCode**:
- 在 Provider 层加 `ModelRouter`
- 输入: 任务特征 (长度、复杂度、工具数)
- 输出: 模型 ID (V4-Flash or V4-Pro)
- 支持用户 override (UI 命令)

### 2.5 Skills 系统 (兼容 Hermes)

```
.reasonix/skills/<name>.md
```

**frontmatter**:
```yaml
---
name: my-skill
description: Brief description.
runAs: subagent          # inline / subagent
allowed-tools: [read_file, write_file]   # 隔离
---
```

**特性**:
- `runAs: subagent` 在独立子循环里执行, 不污染主会话上下文
- `allowed-tools` 隔离权限
- 兼容 Claude Code 格式的 skills (可直接加载)

### 2.6 沙箱 + 计划门

**沙箱**:
- 所有原生工具沙箱化到启动目录
- 不会乱碰其他文件

**计划门** (`/plan`):
- 进入只读审计门
- 所有修改需要用户批准才写入
- `/apply` 真正写入

**移植价值**: AeroCode 当前没有写保护!

### 2.7 Replay & Events

**完整事件流落盘**:
```
.reasonix/events/
  2026-05-01-001.jsonl
  2026-05-01-002.jsonl
```

**可回放**:
- `reasonix replay <session-id>`
- 统计 token / cache / 成本
- 便于审计

**移植到 AeroCode**:
- 完整事件流落盘 (JSONL)
- 回放 UI
- 成本统计

### 2.8 QQ 远程通道

- 把当前会话扩展为 QQ 远程交互通道
- 支持移动端远程接入 Agent 会话

**移植到 AeroCode**:
- P2 (未来支持多平台)

### 2.9 Desktop 桌面版 (Tauri)

- 多标签页
- 右侧面板显示当前会话读/写的文件
- 底部实时成本/缓存/token 计数
- 用同一个 ~/.reasonix config, 不需要额外配置
- 预发布版, Windows 上有 SmartScreen 警告

**移植到 AeroCode**:
- P2 (我们已有 Avalonia 桌面, 不需要 Tauri)

---

## 3. Reasonix 命令系统

| 命令 | 用途 |
|---|---|
| `reasonix` / `reasonix code` | 启动编程代理 (最常用) |
| `reasonix chat` | 纯对话模式, 不带文件系统工具 |
| `reasonix run "任务"` | 一次性执行, 输出流到 stdout |
| `reasonix doctor` | 检查环境、API key、MCP 配置 |
| `/pro` | 当前这轮切到 V4-Pro |
| `/preset max` | 整个 session 用 Pro |
| `/plan` | 进入只读审计模式 |
| `/skill new <name>` | 新建一个 Skill |
| `/skill list` | 列出 skills |

---

## 4. Reasonix 适用的关键场景

| 场景 | Reasonix 优势 |
|---|---|
| 长时编程任务 (几小时~几天) | append-only loop 防止 context 膨胀 |
| 高频 API 调用 (CI/CD) | 99% cache hit 降低 token 成本 |
| 成本敏感型 (个人/初创) | V4-Flash 默认, 比通用工具便宜 3-5 倍 |
| 远程协作 (QQ) | 手机端远程接入 |
| 终端工作流 | git diff + ls, 贴合开发习惯 |

---

## 5. 核心移植清单 (AeroCode V3.0)

### 5.1 Cache-First Loop → `AeroCode.Harness/AppendOnlyLoop.cs` (P0)

**核心实现**:
```csharp
public class AppendOnlyLoop
{
    // 永远只追加, 永不修改历史
    public void Append(ChatMessage msg) { /* append to list */ }
    
    // prefix cache key 永远等于 "all messages joined"
    public string GetCacheKey() => string.Concat(messages);
}
```

**移植到 AeroCode**:
- 改造 AIAgent 主循环为 append-only
- 移除所有"压缩历史"的逻辑 (改用 LLM 摘要压缩)
- 与 DeepSeek / V4-Flash 深度集成

### 5.2 Tool-Call Repair → `AeroCode.AI/ToolCallRepairer.cs` (P0)

**核心实现**:
```csharp
public static class ToolCallRepairer
{
    public static ToolCall[] Repair(ToolCall[] raw, ToolSchema[] schemas)
    {
        // 1. JSON5 容错解析
        // 2. 按 schema 补字段
        // 3. 类型转换
        // 4. 失败时优雅返回
    }
}
```

### 5.3 R1 Thought Harvest → `AeroCode.AI/ReasoningHarvester.cs` (P1)

**核心实现**:
```csharp
public static class ReasoningHarvester
{
    public static ToolCall[] Harvest(string reasoningContent, ToolSchema[] schemas)
    {
        // 扫描 reasoning_content
        // 识别 {"name": "...", "args": {...}} 模式
        // 解析, 合并到正式 tool_call
    }
}
```

### 5.4 Auto Model Router → `AeroCode.AI/ModelRouter.cs` (P0)

**核心实现**:
```csharp
public class ModelRouter
{
    public string Route(TaskContext ctx)
    {
        if (ctx.EstimatedTokens < 4000 && ctx.ToolCalls < 10)
            return "deepseek-v4-flash";  // 默认, 便宜
        return "deepseek-v4-pro";  // 复杂
    }
}
```

### 5.5 沙箱 + 计划门 → `AeroCode.Harness/Sandbox.cs` (P0)

**核心实现**:
```csharp
public class Sandbox
{
    public string Root { get; }
    public bool PlanMode { get; set; }
    
    // 所有文件操作验证在 Root 内
    // PlanMode 下, 写入操作返回 PendingEdit, 不直接写入
}
```

### 5.6 Replay & Events → `AeroCode.Harness/EventStore.cs` (P1)

**核心实现**:
```csharp
public class EventStore
{
    public void AppendEvent(string sessionId, Event evt) { /* write to .jsonl */ }
    public IEnumerable<Event> Replay(string sessionId) { /* read .jsonl */ }
    public CostSummary Summarize(string sessionId) { /* aggregate */ }
}
```

### 5.7 Skills (兼容 Hermes + Reasonix) → 复用 `AeroCode.Skills/` (P0)

兼容两者的 frontmatter schema。

---

## 6. Reasonix 与 Hermes 的对比

| 维度 | Reasonix | Hermes |
|---|---|---|
| **目标模型** | **DeepSeek 专用** (V4-Flash/Pro) | 200+ 模型 (OpenRouter) |
| **核心优化** | **Cache hit** (append-only loop) | **自学习闭环** |
| **技能创建** | 手动 (`/skill new`) | **自动** (5+ 工具调用 + 成功) |
| **自修补** | ❌ | ✅ |
| **Tool Repair** | ✅ (schema-aware) | ❌ |
| **Reasoning Harvest** | ✅ (R1 thought) | ❌ |
| **Provider 数** | 1 (DeepSeek) | 200+ |
| **平台 gateway** | ❌ (只有 CLI) | 7 平台 |
| **部署后端** | 本地 | 6 种 (Local/Docker/SSH/...) |
| **跨平台** | Windows/macOS/Linux | 需 WSL2 |
| **持久记忆** | ❌ | ✅ 4 层 |
| **QQ 远程** | ✅ | ❌ (Telegram/Discord) |
| **MCP** | ✅ | ✅ |
| **Cache 优化** | **99.82%** | 无特定优化 |

**互补关系**:
- Reasonix 提供 **token 成本压缩** + **tool call 自愈** + **reasoning 回收**
- Hermes 提供 **学习闭环** + **4 层记忆** + **多平台**
- 两者合起来 = AeroCode V3.0 的"效率 + 智能"双引擎

---

## 7. 给 V3.0 实施的具体建议

**Stage 4: Token 压缩 (Reasonix 模式)**:
- AppendOnlyLoop 改造 AIAgent
- ToolCallRepairer 集成到 AI 层
- ModelRouter 自动 V4-Flash/Pro 切换
- Sandbox + PlanMode 写保护

**Stage 5: Reasoning Harvest (Reasonix 模式 + Hermes Memory)**:
- ReasoningHarvester 集成到 AI 层
- 与 Hermes 的 Memory 4 层整合 (R1 thought → 写入 MEMORY.md)

**Stage 6: Events & Replay (Reasonix 模式)**:
- EventStore JSONL 落盘
- Replay UI (在 MainWindow)

---

## 8. 一句话总结

> Reasonix 提供 **"DeepSeek 原生 cache-first 极致省钱 + tool 自愈 + reasoning 回收"** 三大法宝。我们移植其 append-only loop + tool repair + model router，让 AeroCode V3.0 在 DeepSeek 上的运行成本降到 1/5，且更稳定。
