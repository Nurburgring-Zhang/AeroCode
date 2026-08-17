# ANALYSIS 01 — Hermes Agent 0.21.1 深度拆解

> **来源**: [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent)  
> **本地路径**: `D:/minimax/代码/hermes-agent` (8859 files, 139.7 MB)  
> **最新版本**: 0.21.1 (2026-08)  
> **许可证**: MIT  
> **GitHub Stars**: 214,000+ (Nous Research 旗下，融资 $75M / 估值 $15亿)  

---

## 1. 定位与差异化

**一句话**: Hermes Agent 不是聊天机器人，是 **"The agent that grows with you"** —— 一个长期运行、自主进化的数字员工。

**三大核心差异** (vs OpenClaw / nanobot / Claude Code / Cursor)：

| 维度 | Hermes | OpenClaw | Claude Code | Cursor |
|---|---|---|---|---|
| **自学习闭环** | ✅ 任务→技能→改进 | ❌ | ❌ | ❌ |
| **持久记忆** | ✅ 三层 + 8 外部 provider | ✅ FTS5 only | ❌ | ❌ |
| **技能生态** | ✅ 639 (74内置+44官方+521社区) | 数百 | 内置 | 内置 |
| **平台 gateway** | ✅ 7 平台 (Telegram/Discord/Slack/WhatsApp/Signal/飞书/钉钉) | 13+ | ❌ | ❌ |
| **部署后端** | ✅ 6 种 (Local/Docker/SSH/Daytona/Singularity/Modal) | 本地/Docker | 本地 | 本地 |
| **Honcho 用户建模** | ✅ 12 层辩证身份 | ❌ | ❌ | ❌ |
| **跨平台会话连续性** | ✅ | ❌ | ❌ | ❌ |
| **开源协议** | MIT | MIT | 闭源 | 闭源 |

---

## 2. 核心架构 (4 大子系统)

### 2.1 学习闭环 (Closed Learning Loop) — **移植优先级 P0**

```
┌─────────────────────────────────────────────────────┐
│  任务执行 (Use 47+ tools)                            │
│       ↓                                              │
│  任务评估 (显式反馈 + 隐式接受信号)                    │
│       ↓                                              │
│  自动创建 Skill (任务完成、5+ 工具调用、用户纠正)        │
│       ↓                                              │
│  保存到 ~/.hermes/skills/<name>/SKILL.md              │
│       ↓                                              │
│  下次任务匹配 → 加载 → 执行 → 失败自修补              │
└─────────────────────────────────────────────────────┘
```

**Skill 创建的触发条件** (Hermes 实现细节)：
1. 任务复杂 (≥ 5 次工具调用) 并成功完成
2. 任务过程中遇到错误 / 死胡同，最终找到可行路径
3. 用户纠正了它的做法
4. 发现了非显而易见的工作流

**Skill 自我改进** (我们叫"自审自查"的核心参考实现)：
- Agent 几周后再次使用 Skill → 命令报错 → 自动用 patch 修复 → 下次不再报错
- 整个流程 **零用户干预**

### 2.2 4 层记忆系统 — **移植优先级 P0**

```
外部层 (External Plugins)  ← Honcho / Mem0 / OpenViking / Hindsight / Holographic / RetainDB / ByteRover / Supermemory
冷层 (Cold Storage FTS5)   ← SQLite + FTS5 全文索引，所有历史会话，搜索 + LLM 摘要 (Gemini Flash)
温层 (Warm Persistent)     ← MEMORY.md (~2,200 字符, ~800 tokens, 8-15 条) + USER.md (~1,375 字符, ~500 tokens, 5-10 条)
                           ↑ 注入到每次 system prompt，冻结快照模式 (保护 prefix cache 性能)
热层 (Hot Working)         ← 当前会话上下文 + 系统提示词
```

**关键设计决策**：
- **冻结快照**: 记忆在会话启动时一次性注入，会话中不更新（保护 DeepSeek prefix cache 性能）
- **有界记忆**: 严格字符限制，防止 system prompt 膨胀
- **8 外部 provider 插件化**: 不内置强制，~/.hermes/plugins/ 目录发现
- **渐进加载**: 技能三级加载（列表 3K tokens → 完整内容 → 参考文件）

**SKILL.md frontmatter 标准** (必须严格遵守 — 我们要移植)：

```yaml
---
name: my-skill                    # 必填, 短横线
description: Brief description.   # 必填, ≤ 60 字符, 一句话, 末尾句号
version: 1.0.0                    # 必填
author: Your Name                 # 必填, 人优先, 工具次之
license: MIT                      # 必填
platforms: [macos, linux]         # 可选, 平台门控
required_environment_variables:   # 可选, 安全 setup-on-load
  - name: MY_API_KEY
    prompt: API key help
    help: Where to get it
    required_for: full functionality
metadata:
  hermes:
    tags: [Category, Subcategory]
    related_skills: [other-skill]
    fallback_for_toolsets: [web]  # 仅当主工具不可用时显示
    requires_toolsets: [terminal] # 仅当某工具可用时显示
---

# Skill Title
2-3 句 intro

## When to Use
## Prerequisites
## How to Run
## Quick Reference
## Procedure
## Pitfalls
## Verification
```

### 2.3 自注册工具系统 (registry.register) — **移植优先级 P0**

```python
# Hermes tools/registry.py 核心
def register(name, toolset, schema, handler, check_fn=None):
    """工具自注册 — 文件 import 时自动发现"""

# 每个工具文件 (如 terminal_tool.py)
@register_when_imported
def my_tool(param1: str, param2: int = 10) -> str:
    return do_work(param1, param2)

MY_TOOL_SCHEMA = {"type": "function", "function": {"name": "my_tool", ...}}

registry.register(
    name="my_tool",
    toolset="my_toolset",       # 工具集分组
    schema=MY_TOOL_SCHEMA,
    handler=my_tool,
    check_fn=lambda: True,       # 依赖检查
)
```

**Toolsets 分组** (Hermes 设计)：
- `hermes-core` — 基础会话、记忆、技能管理
- `web` — 搜索、抓取、浏览器
- `terminal` — 终端执行 (Docker/SSH/Local 抽象)
- `file` — 文件操作
- `browser` — Browserbase session
- `mcp` — MCP 集成
- `vision` — 图像分析
- `media` — 图像生成 / TTS

**关键设计**：通过 toolset 启用/禁用不同平台的工具集。

### 2.4 多平台消息网关 + 6 种执行后端 — **移植优先级 P1**

**Gateway 架构**：
```
用户 → Telegram/Discord/Slack/WhatsApp/Signal/飞书/钉钉
              ↓
       GatewayRunner (统一进程)
              ↓
       Session Store (跨平台会话连续性)
              ↓
       Agent Runtime (Hermes Agent Core)
              ↓
   ┌──────────┼──────────┬──────────┐
   Local   Docker     SSH      Daytona  Modal
```

**关键特性**：
- 跨平台会话连续性 (Telegram 开始的对话可以在 Discord 继续)
- 语音消息自动转文字
- 共享斜杠命令体系
- DM 配对认证

---

## 3. 关键文件 (Hermes 实测)

| 文件 | 大小 | 核心职责 |
|---|---|---|
| `run_agent.py` | 385 KB | AIAgent 类 — 核心会话循环、工具调度、session 持久化 |
| `cli.py` | 878 KB | HermesCLI 类 — 交互式 TUI, prompt_toolkit 集成 |
| `hermes_state.py` | 520 KB | SQLite session DB + FTS5 全文搜索 |
| `model_tools.py` | 73 KB | 工具编排 (tools/registry.py 的薄封装) |
| `toolsets.py` | - | 工具集分组 |
| `agent/prompt_builder.py` | - | System prompt 组装 (identity + skills + context files + memory) |
| `agent/context_compressor.py` | - | 上下文压缩 (达到限制时 LLM 摘要) |
| `agent/auxiliary_client.py` | - | 辅助 OpenAI 客户端 (摘要、视觉) |
| `agent/memory_provider.py` | - | 8 个外部记忆 provider 抽象 |

---

## 4. 核心移植清单 (AeroCode V3.0)

### 4.1 Skills 引擎 → `AeroCode.Skills` (新建项目)

**移植**：
- [ ] SKILL.md frontmatter 解析器 (YamlDotNet)
- [ ] Skills 三级加载 (list 3K → 完整内容 → 参考文件)
- [ ] 平台门控 (Windows/macOS/Linux)
- [ ] required_environment_variables 安全注入
- [ ] Skills Hub 安装器 (从 .agents/skills/ 或 git URL 加载)
- [ ] 渐进式加载 (避免 system prompt 膨胀)
- [ ] **自动创建 Skill** (5+ 工具调用 + 成功)
- [ ] **自修补 Skill** (执行失败时自动 patch)

### 4.2 Memory 引擎 v2 → `AeroCode.Memory` (新建项目, 升级 AeroCode.Core)

**移植**：
- [ ] 4 层记忆 (外部 + FTS5 冷 + MEMORY.md/USER.md 温 + Working 热)
- [ ] SQLite + FTS5 全文索引 (我们已有 SQLite，需加 FTS5 virtual table)
- [ ] MEMORY.md (≤ 2,200 字符) + USER.md (≤ 1,375 字符) 自动管理
- [ ] 冻结快照注入 system prompt 模式 (保护 token 经济性)
- [ ] 8 外部 provider 抽象 (我们只实现 1-2 个接口，未来扩展)
- [ ] 跨对话记忆搜索 + LLM 摘要

### 4.3 Agent Harness → `AeroCode.Harness` (新建项目, 包装 AIAgent)

**移植**：
- [ ] AIAgent 主循环 (ReAct-style)
- [ ] 自注册工具 (`registry.register`)
- [ ] Toolsets 分组与平台适配
- [ ] 上下文压缩 (auto-summarization at 50% threshold)
- [ ] Session 持久化 (SQLite)
- [ ] Provider 抽象 (OpenAI 兼容 + Anthropic + 自定义)

### 4.4 自我进化循环 (Self-Evolution Loop)

**移植**：
- [ ] 任务后反思 (post-task reflection): "这个方法可以复用吗？"
- [ ] 显式反馈 + 隐式接受信号检测
- [ ] 失败自修补 (patch skill on failure)
- [ ] 成功率统计 + 自动优化

### 4.5 Honcho 辩证用户建模 (部分移植)

**移植**：
- [ ] USER.md 自动更新 (从用户反馈中抽取偏好)
- [ ] 12 层身份追踪 (简化版：身份/目标/偏好/痛点/项目/沟通风格/...)

### 4.6 工具集分组 + 平台适配

**移植**：
- [ ] Toolsets 文件 (Core / Web / Terminal / File / Browser / Mcp / Memory / Skills / Vision / Media)
- [ ] 平台开关 (Windows vs Linux vs macOS)

---

## 5. 移植到 AeroCode V3.0 的对应模块映射

| Hermes 模块 | AeroCode V3.0 落点 | 优先级 |
|---|---|---|
| `tools/registry.py` | `AeroCode.Skills/Registry/SkillRegistry.cs` | P0 |
| `agent/prompt_builder.py` | `AeroCode.Harness/PromptBuilder.cs` | P0 |
| `agent/context_compressor.py` | `AeroCode.Harness/ContextCompressor.cs` | P0 |
| `hermes_state.py` (FTS5) | `AeroCode.Memory/SqliteMemoryStore.cs` | P0 |
| `MEMORY.md/USER.md` 管理 | `AeroCode.Memory/MarkdownMemoryStore.cs` | P0 |
| `agent/memory_provider.py` | `AeroCode.Memory/Providers/IMemoryProvider.cs` | P1 |
| `run_agent.py` AIAgent | `AeroCode.Harness/Agent.cs` | P0 |
| `skills/` 加载器 | `AeroCode.Skills/Loader/SkillLoader.cs` | P0 |
| `optional-skills/` | `AeroCode.Skills/Bundled/` | P1 |
| `plugins/memory/honcho` | `AeroCode.Memory/Providers/HonchoProvider.cs` | P2 |

---

## 6. 我们学到的"硬规则" (HARDLINE for AeroCode V3.0)

1. **description ≤ 60 字符** — system prompt 经济性
2. **平台门控必须真实** — Windows 不能用 fcntl/termios，要 psutil
3. **os.kill(0) 在 Windows 是广播 Ctrl+C** — 用 psutil.pid_exists
4. **shutil.which** — Windows 没有 grep/awk/fuser/lsof
5. **pathlib** — 不用字符串拼接
6. **CLI shim .cmd** — subprocess.run(["agent-browser"]) 在 Windows 失败，要 shutil.which
7. **UTF-8 BOM** — Windows Notepad 存 .yaml 用 encoding="utf-8-sig"
8. **CRLF vs LF** — Windows .cmd/.bat 用 newline="\r\n"
9. **跨平台信号** — SIGALRM/SIGCHLD/SIGHUP 在 Windows AttributeError, 用 getattr(signal, "SIGKILL", signal.SIGTERM)
10. **OneDrive 路径** — Desktop/Documents/Pictures 在 Windows 可能被重定向到 %USERPROFILE%\OneDrive\

---

## 7. 给 V3.0 实施的具体建议

**Stage 1 (Skills 引擎)**:
- 优先做 SKILL.md 解析 + 加载器 (因为这是基础设施)
- 不做 Honcho / Atropos (过度)

**Stage 2 (Agent Harness)**:
- 实现 Agent 主循环 (ReAct)
- 自注册工具机制
- 上下文压缩

**Stage 3 (Memory 引擎 v2)**:
- SQLite FTS5 virtual table
- MEMORY.md/USER.md 自动管理
- 跨对话搜索

**Stage 4 (Self-Evolution)**:
- 任务后反思
- 失败自修补
- 成功率追踪

---

## 8. 风险与权衡

| 风险 | 缓解 |
|---|---|
| Skill 自动创建质量 | 只在 5+ 工具调用 + 显式成功时创建；质量门控 |
| FTS5 性能 | 已用 SQLite，可加 `CREATE VIRTUAL TABLE ... USING fts5` |
| Memory 膨胀 | 严格字符限制 + 冻结快照 |
| 跨平台兼容 | 复用 Hermes 跨平台规则 |
| 过深的移植 | 只做核心 3-4 个模块，Atropos/Honcho 留接口 |

---

## 9. 一句话总结

> Hermes 是 **"会自己学新技能的工匠"** — 我们移植其学习闭环、4 层记忆、自注册工具三大子系统，让 AeroCode 真正成为"会进化的 AI 笔记伙伴"。
