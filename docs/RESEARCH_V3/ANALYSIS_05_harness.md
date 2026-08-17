# ANALYSIS 05 — DeepSeek Harness v0.1 深度拆解

> **来源**: [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)  
> **本地路径**: `D:/minimax/代码/AeroCodeV3_externals/deepseek-harness` (7412 files, 45.5 MB)  
> **npm 包**: `@deepseek-ai/dsh` v0.1.0-rc.5  
> **许可证**: MIT  
> **发布**: 2026-08-13 (developer preview, 快速迭代中, 会有 breaking changes)  

---

## 1. 定位与差异化

**DeepSeek Harness (DSH)** = **"Model + Harness = Agent"** 公式中的 Harness 部分。

**官方定义** (中文): "由 DeepSeek 自己开发的 Agent Harness: 它不是新的基础模型或者一个 API 客户端, 而是负责把模型接入文件系统、终端、网页、代码工具和其他 Agent, 并组织上下文、工具调用和任务执行的一整套 Agent 运行框架。"

**核心设计哲学**: **Everything is a plugin** —— 一切皆插件。

**为什么这很重要**:
- 不是"DeepSeek 版 Claude Code" 或 "DeepSeek 版 Codex"
- 是一套 **可重组的 Agent 基础设施** —— 用户可通过配置替换任何模块

**与 OpenCode 的关系**:
- OpenCode 是 "AI-powered development tool" (TUI + Desktop + Web)
- DSH 是 "Agent 运行时 + 插件生态" (类似 Cordis + profile/bundle 组合)
- **DSH 更像框架, OpenCode 更像产品**

---

## 2. 核心架构 (Cordis 插件元框架)

### 2.1 Cordis 基础

DSH 建立在 [Cordis](https://github.com/cordiverse/cordis) 之上。Cordis 是一个 **"时空可组合"** 编程范式, 核心机制:

```
┌─────────────────────────────────────┐
│      Cordis Runtime (Context)        │
│                                       │
│   Plugin A ──→ 注册 Service X         │
│   Plugin B ──→ 注册 Event Y          │
│   Plugin C ──→ 订阅 Event Y          │
│                                       │
│   卸载 Plugin A → 自动 unwinding     │
└─────────────────────────────────────┘
```

- 插件贡献 typed services + events + reversible effects
- 一切 plugin 注册都是 effects, 卸载时自动 unwind
- 没有特权核心, 通过"挂载 plugin 替换"扩展

### 2.2 Profiles + Bundles 分层组合

```
启动 dsh:
  Layer 1: dsh-base (基础 bundle: 模型、工具、持久化、沙箱、审批、设置、凭证、遥测)
  Layer 2: dsh-web-app (Web 应用) OR dsh-headless (无服务 one-shot)
  Layer 3: user profile (用户自定义 bundles)
  Layer 4: cordis.patch.yml (用户配置覆盖)
  Layer 5: --patch overlay (命令行覆盖)
```

**关键概念**:
- **Profile**: 命名的 plugin tree composition, 存放在 Harness home
- **Bundle**: Cordis config rows + 代码 的分发格式
- **Layered Composition**: 每层都是 ordered list, 后续层可 patch 前面层

**实现细节**:
```json
// package.json dsh field
{
  "dsh": {
    "profile": "web",         // 我是个 profile, 列出 bundles
    "bundle": "patch.yml"     // 我是个 bundle, 指向 patch 文件
  }
}
```

### 2.3 40+ Packages (子系统清单)

| Package | 职责 | AeroCode V3 移植点 |
|---|---|---|
| `core` | Cordis 集成 + 核心 context | 概念移植 (DI 容器) |
| `llm` | 模型适配 (OpenAI/Anthropic 兼容) | 已有 |
| `mcp` | MCP 集成 | 已有, 增强 |
| `skill` | Skill 系统 | **核心移植** |
| `tool` / `tools` | 工具系统 | **核心移植** |
| `session` | 会话管理 | **核心移植** |
| `context` | 上下文管理 + 压缩 | **核心移植** |
| `compaction` | 上下文压缩 | **P0 移植** |
| `subagent` | Sub-agent 编排 | **P1 移植** |
| `goal` | 任务目标管理 | **P1 移植** |
| `plan` | Plan mode (类似 /plan) | **P0 移植** |
| `preset` | Agent Preset (不同提示词/工具/规则) | **P0 移植** |
| `shell` | Shell 执行 | **P0 移植** |
| `fs` | 文件系统 | **P0 移植** |
| `code-runtime` | 代码执行 | **P1 移植** |
| `lsp` | LSP 集成 | P2 |
| `e2b` | E2B 沙箱 | P2 |
| `sandbox` | 沙箱系统 | **P0 移植** |
| `guard` | 权限 + 命令审批 | **P0 移植** (与 OpenCode Permission 合并) |
| `settings` | 设置管理 | 已有 |
| `credentials` | 凭证管理 (API keys) | 已有 |
| `session-query` | 会话查询 | **P1 移植** |
| `storage` | 持久化存储 | **P0 移植** |
| `interaction` | 交互 (输入/输出) | UI 集成 |
| `hooks` | 钩子 (pre/post 工具调用) | **P1 移植** |
| `feedback` | 反馈机制 | **P1 移植** (用于 grill-me) |
| `identity` | 身份管理 | 简化 |
| `spill` | 输出溢出 (超长内容) | **P1 移植** |
| `schedule` | 调度 (cron) | P2 |
| `jobs` | 异步任务 | P1 |
| `runtime-diagnostics` | 运行时诊断 | **P1 移植** |
| `acp` | Agent Communication Protocol | P2 |
| `api` | HTTP API | 已有 |
| `attachment` | 附件 (图像/文件) | 已有 |
| `sdk` | SDK | 简化 |
| `examples` | 示例 plugins | 参考 |

**4 种预设模式** (官方公布):
1. **标准 (Standard)**: 搭载全套工具组件, 满足通用开发
2. **PTC (Programmatic Tool Calling)**: 由模型生成代码, 编排多轮工具链式调用
3. **极简 (Minimal)**: 仅保留 Shell、文件编辑两大工具, 用于最小环境下模型基准测试
4. **创造 (Creative)**: 支持查看运行时状态, 内存内调试 Cordis 插件, 自定义生成全新运行模式

**Agent Preset** (DSH 设计): 为同一套系统安装不同的提示词、工具和运行规则

---

## 3. 关键设计模式

### 3.1 Service Registration (服务注册)

```typescript
// Cordis service registration
ctx.service('llm', {
  ready: (ctx) => ctx.registry.get('llm.adapter'),
  dispose: () => {},
})

ctx.service('tool.write_file', {
  ready: (ctx) => new WriteFileTool(),
  dispose: () => {},
})
```

**移植到 AeroCode**:
- 用 C# `IServiceCollection` + `IHostedService` 模式
- 插件 = 模块 + 启动/卸载钩子

### 3.2 Event Subscription (事件订阅)

```typescript
// DSH 事件
ctx.on('tool.before_call', (toolName, args) => {
  // 审批 / 日志 / 修改
})

ctx.on('tool.after_call', (toolName, result) => {
  // 缓存 / 监控
})

ctx.on('session.start', (session) => { ... })
ctx.on('session.end', (session) => { ... })
```

**移植到 AeroCode**:
- `AeroCode.Harness/EventBus.cs`
- 简单 pub/sub
- 事件类型: `ToolCalled`, `SkillLoaded`, `MemoryUpdated`, `SessionStart`, `SessionEnd`

### 3.3 Reversible Effects (可逆副作用)

```typescript
// 写文件 = 副作用
ctx.effect(async () => {
  await fs.writeFile(path, content)
  // 卸载时自动调用清理
  return () => fs.unlink(path + '.bak')
})
```

**移植到 AeroCode**:
- Patch Engine 自动备份 + 失败回滚 (已有规划)

### 3.4 Capability Seams (能力接缝)

DSH 文档特别提到 **"capability seams"** —— 这是 DSH 的核心可扩展点：
- LLM 适配
- 工具注册
- 会话存储
- 沙箱
- 凭据
- UI 主题

每个 seam 都是 plugin mount point。

**移植到 AeroCode**:
- 设计 `ICapability` 接口
- 各子系统实现 (LLM / Tool / Session / Sandbox / Credential / Theme)

### 3.5 Defensive Patterns (防御模式)

DSH 文档专门有 `defensive-patterns.md` —— 这是 DSH 的"安全工程"哲学：
- 所有写操作前快照
- 所有危险命令需审批
- 所有 LLM 输出需 schema 验证 (类似 Reasonix Tool-Call Repair)
- 所有外部 API 调用需重试 + 降级
- 所有 prompt injection 需检测

**移植到 AeroCode**:
- 与 Hermes 安全机制 + OpenCode Permission + Reasonix Tool Repair 整合

---

## 4. 4 种预设模式 (AeroCode V3.0 对应)

| DSH 模式 | 描述 | AeroCode V3 对应 |
|---|---|---|
| **Standard** | 全套工具 | 默认 `GeneralAgent` preset |
| **PTC** | 模型生成代码, 编排多轮工具 | `CodeGenAgent` preset (优先用 V4-Pro) |
| **Minimal** | 仅 Shell + File Edit | `MinimalAgent` preset (用于 benchmark) |
| **Creative** | 运行时调试 + 自定义 plugin | `DevMode` preset (开发者模式, 显示内部状态) |

**用户可自定义 Preset**:
- 不同的 system prompt
- 不同的 tools 集合
- 不同的 model 路由策略
- 不同的 safety policy

---

## 5. 核心移植清单 (AeroCode V3.0)

### 5.1 Agent Preset System → `AeroCode.Harness/Presets/` (P0)

**移植**:
- [ ] Preset 实体 (Name, SystemPrompt, Tools, ModelRouting, SafetyPolicy)
- [ ] PresetService (Load, Save, Switch, Delete)
- [ ] 4 个默认 Preset (Standard / PTC / Minimal / Creative)
- [ ] 用户可自定义 Preset (UI)
- [ ] 启动时 `--preset <name>` override

### 5.2 Plugin/Profile Architecture → `AeroCode.Plugins/` (P1)

**移植**:
- [ ] IPlugin 接口 (OnLoad, OnUnload, RegisterServices, RegisterEvents)
- [ ] Plugin Loader (发现 ~/.aerocode/plugins/ 中的 plugin)
- [ ] Profile 概念 (named composition of plugins)
- [ ] Layered Composition (用户配置覆盖)

### 5.3 Compaction (上下文压缩) → `AeroCode.Harness/Compaction.cs` (P0)

**移植**:
- [ ] Auto-summarization at threshold (default 50%)
- [ ] Sliding window (保留最近 N 轮)
- [ ] Important-message pinning (保护关键消息)
- [ ] LLM 摘要策略 (与 Hermes context_compressor 合并)

### 5.4 Plan Mode → `AeroCode.Harness/PlanMode.cs` (P0)

**移植**:
- [ ] `PlanMode` 状态 (true = read-only, false = write-enabled)
- [ ] 所有写操作 (write_file / run_shell / edit_file) 在 PlanMode 下返回 PendingEdit
- [ ] 用户 UI 确认 (XAML 弹窗)
- [ ] `/apply` 真正写入

### 5.5 Goal System → `AeroCode.Harness/Goal.cs` (P1)

**移植**:
- [ ] Goal 实体 (Description, Status, SubGoals, Success Criteria)
- [ ] GoalService (Create, Update, Decompose, Track)
- [ ] 任务后反思 (post-task reflection) → 更新 Goal
- [ ] 与 mattpocock `to-tickets` skill 整合

### 5.6 Subagent System → `AeroCode.Harness/SubAgent.cs` (P1)

**移植**:
- [ ] SubAgent 隔离 (独立 context, 独立 model, 独立 tools)
- [ ] 主 agent → 委托 sub-agent
- [ ] 等待 / 轮询 / 取消 sub-agent
- [ ] 并行 sub-agent (`Promise.all` 模式)

### 5.7 Hooks System → `AeroCode.Harness/Hooks.cs` (P1)

**移植**:
- [ ] 5 个核心 hooks: `pre_tool_call`, `post_tool_call`, `pre_llm_call`, `post_llm_call`, `on_session_end`
- [ ] Hook 链 (顺序执行)
- [ ] Hook 可修改 args / 阻止调用

### 5.8 Feedback System → `AeroCode.Harness/Feedback.cs` (P1)

**移植**:
- [ ] 用户显式反馈 (thumbs up/down)
- [ ] 隐式反馈检测 (用户重做、撤销)
- [ ] 反馈 → Memory 更新 (类似 Hermes)

### 5.9 Runtime Diagnostics → `AeroCode.Harness/Diagnostics.cs` (P1)

**移植**:
- [ ] Token 实时统计
- [ ] Cache hit 率
- [ ] 工具调用次数
- [ ] 成本估算
- [ ] UI 显示 (与 Reasonix Desktop 类似)

---

## 6. DSH 与其他项目对比

| 维度 | DSH | OpenCode | Hermes | Claude Code |
|---|---|---|---|---|
| **语言** | TypeScript | TypeScript | Python | ? |
| **架构** | Cordis plugin | monorepo | Python module | 闭源 |
| **可扩展性** | **极致 (一切 plugin)** | 中 (provider) | 中 (skill) | 低 |
| **预设模式** | **4 种** | ❌ | 1 (default) | 1 |
| **Plan Mode** | ✅ | ❌ | ❌ | ❌ |
| **Sub-agent** | ✅ | ✅ (OmO) | ✅ (delegate) | ✅ |
| **Provider 数** | 多 | 75+ | 200+ | 1 |
| **OSS 协议** | MIT | MIT | MIT | 闭源 |
| **成熟度** | v0.1.0-rc.5 (preview) | 158k⭐ | 214k⭐ | 闭源 |

**互补关系**:
- DSH 提供 **Profile/Bundle 组合 + Plan Mode + 4 Preset + Defensive Patterns**
- Hermes 提供 **学习闭环 + 4 层记忆 + 多平台 gateway**
- OpenCode 提供 **75+ providers + Permission + Project + LSP**
- Reasonix 提供 **Cache-First Loop + Tool Repair + Reasoning Harvest**
- 4 者合起来 = AeroCode V3.0 的"完整 Agent 操作系统"

---

## 7. 给 V3.0 实施的具体建议

**Stage 2: Agent Harness (融合 DSH + Hermes + OpenCode)**:
- Agent 主循环 (DSH profile 概念)
- 4 个默认 Preset (Standard / PTC / Minimal / Creative)
- Preset Switcher (UI)
- Plan Mode (P0 安全)
- Compaction (上下文压缩)
- Hooks (5 个核心)
- Event Bus (pub/sub)

**Stage 3: Plugin Architecture (DSH 模式)**:
- IPlugin 接口
- Plugin Loader
- 用户可挂载第三方 plugin
- 这部分可以晚一点做 (P1)

**Stage 4: Goal + Sub-agent (DSH 模式)**:
- Goal 系统
- Sub-agent 编排
- 任务后反思

**Stage 5: Diagnostics (DSH + Reasonix 模式)**:
- Token 实时统计
- Cache hit 率
- 成本估算 UI

---

## 8. 一句话总结

> DSH 提供 **"Cordis 插件元框架 + 4 模式 + Plan Mode + Compaction + Defensive Patterns"**。我们移植其 preset 系统 + plan mode + hooks + sub-agent 模式，让 AeroCode V3.0 成为可扩展、可配置、防御性强的 Agent 操作系统。
