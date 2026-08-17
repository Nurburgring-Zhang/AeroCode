# ANALYSIS 02 — OpenCode 0.x + oh-my-opencode 5.0 深度拆解

> **来源**: [sst/opencode](https://github.com/sst/opencode) + [oh-my-opencode](https://github.com/code-yeongyu/oh-my-opencode)  
> **本地路径**: `D:/minimax/代码/opencode` (6513 files, 126.1 MB) + `D:/minimax/代码/oh-my-opencode` (7981 files, 86 MB)  
> **许可证**: MIT  
> **GitHub Stars**: OpenCode 158k+ (sst/opencode) | oh-my-opencode 5.0.0-beta.7 (Multi-Model Orchestration)  

---

## 1. 定位与差异化

**OpenCode** (sst/opencode)：
- **"AI-powered development tool"** — TUI + Desktop + Web 多端 AI 编码工具
- **75+ LLM providers** — 业界最广
- **多模型 + 隐私最佳** — 全部在本地运行

**oh-my-opencode (OmO)**：
- **"The Best AI Agent Harness - Batteries-Included OpenCode Plugin"**
- **Multi-Model Orchestration** — 同时调度 Anthropic / OpenAI / Google / DeepSeek
- **Parallel Background Agents** — 后台并行 sub-agent
- **Crafted LSP/AST Tools** — 深度代码理解

**两者的关系**: oh-my-opencode 是 OpenCode 的 **plugin**（通过 `@opencode-ai/plugin` workspace），不是替代。OmO 提供多模型编排和并行 sub-agent，OpenCode 提供底座。

---

## 2. 核心架构

### 2.1 OpenCode monorepo 布局 (30+ packages)

```
opencode/
├── packages/
│   ├── opencode/            # 核心 TUI 引擎
│   │   └── src/
│   │       ├── agent/       # 代理循环
│   │       ├── provider/    # 75+ LLM providers
│   │       ├── session/     # 会话管理
│   │       ├── permission/  # 权限系统
│   │       ├── skill/       # 技能系统 (!!!)
│   │       ├── mcp/         # MCP 集成
│   │       ├── lsp/         # LSP 集成
│   │       ├── patch/       # 代码 patch
│   │       ├── project/     # 项目管理
│   │       ├── plugin/      # 插件系统
│   │       ├── question/    # 交互式问答
│   │       └── server/      # HTTP API
│   ├── app/                 # Next.js Web UI
│   ├── cli/                 # 命令行
│   ├── desktop/             # Tauri 桌面
│   ├── core/                # 核心抽象
│   ├── llm/                 # LLM 客户端
│   ├── protocol/            # 协议类型
│   ├── schema/              # 校验 schema
│   ├── sdk/                 # SDK
│   ├── tui/                 # TUI 框架
│   ├── ui/                  # UI 组件
│   ├── effect-sqlite-node/  # SQLite Effect 集成
│   ├── effect-drizzle-sqlite/
│   ├── containers/          # 容器化部署
│   ├── function/            # Lambda 部署
│   ├── identity/            # 身份认证
│   ├── mcp/                 # MCP 复用
│   ├── server/              # HTTP server
│   ├── slack/               # Slack 集成
│   └── ...                  # 30+ total
├── .opencode/               # OpenCode 自身配置
├── AGENTS.md                # 给 AI 助手的项目说明
├── CONTEXT.md               # 项目上下文
└── CONTRIBUTING.md
```

### 2.2 关键子系统 (packages/opencode/src)

| 模块 | 职责 | AeroCode V3 移植点 |
|---|---|---|
| `agent/` | Agent 循环 (plan/execute) | `AeroCode.Harness/Agent.cs` |
| `provider/` | 75+ LLM 适配 | **不移植** (我们已有 11 providers) |
| `session/` | 会话持久化 | `AeroCode.Memory/SessionStore.cs` |
| `permission/` | 文件/命令权限系统 | **P0 移植** — AeroCode 缺这个 |
| `skill/` | 技能系统 | `AeroCode.Skills/` |
| `mcp/` | MCP 集成 | **已有**, 增强 |
| `lsp/` | LSP 集成 (TypeScript/Python/Go/Rust) | **P2 移植** (长任务) |
| `patch/` | 文件 patch 引擎 | `AeroCode.Harness/PatchEngine.cs` |
| `project/` | 项目管理 (git init, file tree) | **P1 移植** — 我们缺 |
| `plugin/` | 插件系统 | `AeroCode.Plugins/` |
| `question/` | 交互式问答 (ask_user) | `AeroCode.Harness/Question.cs` |
| `bus/` | 事件总线 | `AeroCode.Harness/EventBus.cs` |

### 2.3 75+ Providers 模式 (OpenCode 核心优势)

OpenCode `packages/opencode/src/provider/` 包含一个 **统一抽象 + 75+ 适配器**：
- 每个 provider 一个文件 (openai.ts / anthropic.ts / bedrock.ts / google.ts / ...)
- 统一 `Provider` 接口
- 统一 `transform()` (消息格式转换)
- 统一 `error.ts` (错误归一化)
- 统一 `model-status.ts` (健康检查)
- 统一 `auth.ts` (认证)

**这正是我们 AeroCode.AI 已经实现的 11 providers 模式**。**不重复造轮子**，但可以参考它的"model-status 健康检查"和"统一错误归一化"。

### 2.4 oh-my-opencode Multi-Model Orchestration

**核心创新**：
```
单一任务 → OmO 调度器 →
  ├── 简单分析任务 → DeepSeek V4-Flash (便宜)
  ├── 代码生成 → Claude Opus 4 (强)
  ├── 文档检索 → Gemini Flash (快)
  └── 长上下文分析 → GPT-5.6 (大)
最终合并 → 统一输出
```

**Parallel Background Agents**：
- 主 agent 启动 sub-agent
- sub-agent 在 background 运行
- 主 agent 可以 wait / poll / cancel
- 所有 sub-agent 共享 context 但独立 context window

---

## 3. 关键代码模式

### 3.1 Permission 系统 (OpenCode `src/permission/`)

```typescript
// OpenCode permission 模式
type Permission = 'allow' | 'deny' | 'ask'

const permission = {
  'read_file': 'allow',        // 允许
  'write_file': 'ask',         // 询问
  'bash_command': 'deny',      // 拒绝危险命令
  'git_push': 'ask',           // 询问
}

// 用户可针对每个工具 + 每个路径自定义
```

**移植价值**: AeroCode 当前没有权限系统！任何代码生成都直接写入。这是个 **P0 安全缺口**。

### 3.2 Project 系统 (OpenCode `src/project/`)

```typescript
// OpenCode project 管理
const project = {
  root: '/path/to/project',
  git: { branch, status, diff },
  files: tree,
  lsp: { tsserver, pylsp },
  mcp: { servers: [...] }
}
```

**移植价值**: AeroCode 现在只是单笔记/单笔记本，没有"项目"概念。

### 3.3 Patch Engine (OpenCode `src/patch/`)

```typescript
// OpenCode patch 模式 (类似 Cursor Fast Apply)
const patch = {
  file: 'src/foo.ts',
  search: 'old code',
  replace: 'new code',
  fuzzy: true,           // 允许近似匹配
  validate: 'compile',   // 写入前编译验证
  rollback: true         // 失败回滚
}
```

---

## 4. oh-my-opencode 5.0 关键能力

### 4.1 Multi-Model Orchestration

```typescript
// OmO 模型调度器
const models = {
  'fast-analysis': 'deepseek-v4-flash',     // 便宜
  'code-generation': 'claude-opus-4',       // 强
  'long-context': 'gpt-5.6',                // 1M tokens
  'vision': 'gemini-2.5-pro',               // 图像
}

// 任务自动路由
async function routeTask(task) {
  if (task.complexity < 0.3) return models['fast-analysis']
  if (task.type === 'code') return models['code-generation']
  if (task.contextLength > 100_000) return models['long-context']
  return models['code-generation']
}
```

**移植到 AeroCode V3.0**:
- **任务复杂度评估** (token 长度 + 工具调用数 + 推理深度)
- **自动模型路由** (V4-Flash 默认, V4-Pro 复杂任务)
- 这是 **Reasonix 模式的移植**！

### 4.2 Parallel Background Agents

```typescript
// OmO sub-agent 模式
const subAgent = await mainAgent.delegate({
  task: 'analyze the auth module',
  isolation: 'context',         // 独立 context
  tools: 'read-only',           // 只读工具
  iterations: 15,               // 限制迭代
  resultTruncation: 4000,       // 截断结果
})

// 主 agent 可以并行启动多个
const [a, b, c] = await Promise.all([
  mainAgent.delegate({ task: 'A' }),
  mainAgent.delegate({ task: 'B' }),
  mainAgent.delegate({ task: 'C' }),
])
```

**移植到 AeroCode V3.0**:
- 用于"长任务规划 + 分段执行"
- 例如：分析大型代码库时，并行启动多个 sub-agent 处理不同模块

### 4.3 Crafted LSP/AST Tools

OmO 提供：
- `lsp_hover` — 悬停信息 (类型、文档)
- `lsp_definition` — 跳转到定义
- `lsp_references` — 查找引用
- `lsp_rename` — 重命名 (跨文件)
- `ast_search` — AST 模式搜索 (不是字符串)
- `ast_transform` — AST 级别代码转换 (不是文本替换)

**移植到 AeroCode V3.0**:
- **P2** (长任务) — 我们先做基础 patch，LSP 留接口

---

## 5. 核心移植清单 (AeroCode V3.0)

### 5.1 Permission System → `AeroCode.Harness/Permission` (P0)

**移植**：
- [ ] Permission enum (Allow / Deny / Ask)
- [ ] 工具级权限 (每个 toolset 一个策略)
- [ ] 路径级权限 (`*.cs` 允许, `appsettings.json` 拒绝)
- [ ] 命令级权限 (危险命令检测)
- [ ] 用户确认 UI (XAML 弹窗)

### 5.2 Project System → `AeroCode.Core/Project` (P1)

**移植**：
- [ ] Project 实体 (RootPath, GitInfo, FileTree, LspServers, McpServers)
- [ ] ProjectService (init, switch, close)
- [ ] 与 Notebook 关联 (1 Project → N Notebooks)

### 5.3 Patch Engine → `AeroCode.Harness/PatchEngine` (P0)

**移植**：
- [ ] search/replace 模式
- [ ] 模糊匹配 (允许 ±3 行)
- [ ] 写入前备份
- [ ] 失败回滚
- [ ] 多文件批处理

### 5.4 Event Bus → `AeroCode.Harness/EventBus` (P1)

**移植**：
- [ ] 简单 pub/sub
- [ ] 事件类型 (ToolCalled / SkillLoaded / MemoryUpdated / ...)
- [ ] 用于跨模块解耦

### 5.5 Multi-Model Router → `AeroCode.AI/ModelRouter` (P0)

**移植**：
- [ ] 任务复杂度评估
- [ ] 模型路由表 (fast / strong / long / vision)
- [ ] 自动 fallback (DeepSeek 不可用 → Qwen)

### 5.6 Question/Clarify 系统 (P2)

**移植**：
- [ ] ask_user 工具
- [ ] XAML 弹窗展示问题
- [ ] 答案返回到 agent

---

## 6. OpenCode vs Hermes 对比

| 维度 | OpenCode | Hermes |
|---|---|---|
| **语言** | TypeScript (Bun) | Python (uv) |
| **TUI** | OpenTUI | prompt_toolkit |
| **部署** | Tauri / Web / TUI / CLI | CLI / TUI / Web / Gateway |
| **Provider 数** | 75+ | 8 (依赖 OpenRouter 200+) |
| **学习闭环** | ❌ (但有 skills) | ✅ (核心差异) |
| **MCP 集成** | ✅ | ✅ |
| **LSP 集成** | ✅ (TS/Python/Go/Rust) | ❌ |
| **Gateway** | Slack | 17 平台 (Telegram/Discord/...) |
| **跨平台** | 全平台 | 需 WSL2 (无原生 Windows) |
| **皮肤系统** | ❌ | ✅ Skin Engine |
| **会话存储** | Effect SQLite | SQLite + FTS5 |

**互补关系**: 
- Hermes 的强项是 **学习闭环 + 多平台 gateway** — 我们必须移植
- OpenCode 的强项是 **Permission + Project + LSP** — 我们必须移植
- 两者合起来 = AeroCode V3.0 的目标架构

---

## 7. 给 V3.0 实施的具体建议

**Stage 1: Skills 引擎 (Hermes 优先)**
- 因为 Skills 是所有能力的"载体"

**Stage 2: Agent Harness + Permission (OpenCode + Hermes 融合)**
- Permission 是 P0 安全缺口
- Project 是 P1 (短期可不做)

**Stage 3: Memory 引擎 v2 (Hermes 模式)**
- FTS5 + MEMORY.md/USER.md

**Stage 4: Patch Engine (OpenCode 模式 + Hermes 自修补)**
- 合并 Hermes 的"自修补" + OpenCode 的"patch 引擎"

**Stage 5: Multi-Model Router (OmO 模式 + Reasonix 模式)**
- V4-Flash/Pro auto-switch
- 任务复杂度评估

**Stage 6: Sub-Agent / Background Tasks (OmO 模式)**
- 用于长任务分段执行

---

## 8. 一句话总结

> OpenCode 提供 **Permission + Project + Patch + LSP** 四大工程基础设施；oh-my-opencode 提供 **Multi-Model + Sub-Agent + AST Tools**。我们移植其核心 4 项，让 AeroCode 从"笔记"升级为"AI 工程伙伴"。
