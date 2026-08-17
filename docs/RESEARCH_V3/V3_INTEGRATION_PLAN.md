# AeroCode V3.0 — 集成方案 (Integration Plan)

> **生成时间**: 2026-08-14
> **作者**: Mavis (auto-generated)
> **状态**: Phase 1 实施中

---

## 0. 8 项目综合能力图谱

| 来源 | 核心能力 | 移植到 AeroCode V3.0 |
|---|---|---|
| **Hermes** | 学习闭环 + 4 层记忆 + 自注册工具 + 多平台 gateway | `AeroCode.Memory` + `AeroCode.Harness/Registry` |
| **OpenCode** | Permission + Project + Patch + 75+ providers | `AeroCode.Harness/Permission` + `AeroCode.Harness/PatchEngine` |
| **oh-my-opencode** | Multi-Model Orchestration + Parallel Sub-agents + AST | `AeroCode.AI/ModelRouter` + `AeroCode.Harness/SubAgent` |
| **DeepSeek Harness** | Cordis Plugin + 4 Preset + Plan Mode + Compaction | `AeroCode.Harness/Presets` + `AeroCode.Harness/PlanMode` |
| **DeepSeek-Reasonix** | Cache-First Loop + Tool Repair + Reasoning Harvest | `AeroCode.AI/ToolCallRepairer` + `AeroCode.AI/ReasoningHarvester` |
| **Matt Pocock Skills** | TDD + Code Review + Bug Diagnosis + Grill with Docs | `AeroCode.Skills/Engineering/*` |
| **Google eng-practices** | 8 维度 Code Review + Small CLs + CL Description | `AeroCode.Harness/CodeReview` + `AeroCode.Harness/PatchEnforcer` |
| **CodeFlow** | Fast Apply + E2B Sandbox + Next.js UI | `AeroCode.Harness/FastApply` (P2) |
| **Avernet** | Multi-Bot Profile + BaaS + 蚂蚁生产验证 | `AeroCode.Harness/Profiles` |

---

## 1. AeroCode V3.0 架构总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                         AeroCode V3.0                                 │
├─────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  ┌────────────────────── AeroCode.App (UI) ──────────────────────┐   │
│  │  Tabs: 笔记 | AI 助手 | Code Review (新) | 性能监控 (新)        │   │
│  │  TopBar: Profile 选择器 | Model 选择 | V4-Flash/Pro 自动切换   │   │
│  └────────────────────────────┬─────────────────────────────────┘   │
│                               │                                      │
│  ┌────────────────────────────┴─────────────────────────────────┐   │
│  │  AeroCode.Harness (NEW) — Agent 引擎                           │   │
│  │  ┌────────────┬────────────┬──────────────┬─────────────┐   │   │
│  │  │  Agent.cs  │ Presets/   │ PlanMode.cs  │ EventBus.cs │   │   │
│  │  │  (主循环)   │ (4 预设)   │ (写保护)      │ (pub/sub)   │   │   │
│  │  ├────────────┼────────────┼──────────────┼─────────────┤   │   │
│  │  │ Permission │ PatchEng.  │ Compaction   │ Hooks       │   │   │
│  │  │ (权限)     │ (代码 patch)│ (上下文压缩)   │ (钩子)      │   │   │
│  │  ├────────────┼────────────┼──────────────┼─────────────┤   │   │
│  │  │ CodeReview │ FastApply  │ Goals        │ SubAgent    │   │   │
│  │  │ (8 维度)   │ (Cursor)   │ (目标管理)    │ (子代理)     │   │   │
│  │  └────────────┴────────────┴──────────────┴─────────────┘   │   │
│  └────────────────────────────┬─────────────────────────────────┘   │
│                               │                                      │
│  ┌────────────────────────────┴─────────────────────────────────┐   │
│  │  AeroCode.Skills (NEW) — 技能引擎                              │   │
│  │  ┌──────────────┬──────────────┬──────────────────────────┐   │   │
│  │  │ SKILL.md     │ SkillLoader  │ SkillRegistry            │   │   │
│  │  │ Parser       │ (三级加载)    │ (注册中心)                │   │   │
│  │  ├──────────────┼──────────────┼──────────────────────────┤   │   │
│  │  │ Engineering/ │ Bundled/     │ AutoCreate               │   │   │
│  │  │ (MattP)      │ (Hermes)     │ (5+ 工具调用 → 创建)       │   │   │
│  │  ├──────────────┼──────────────┼──────────────────────────┤   │   │
│  │  │ Productivity/│ Hub/         │ SelfPatch                │   │   │
│  │  │ (Hermes)     │ (Hub安装)     │ (失败自修补)              │   │   │
│  │  └──────────────┴──────────────┴──────────────────────────┘   │   │
│  └────────────────────────────┬─────────────────────────────────┘   │
│                               │                                      │
│  ┌────────────────────────────┴─────────────────────────────────┐   │
│  │  AeroCode.Memory (NEW) — 4 层记忆                              │   │
│  │  ┌──────────────┬──────────────┬──────────────────────────┐   │   │
│  │  │ Working      │ MEMORY.md    │ FTS5 (Cold Storage)      │   │   │
│  │  │ (当前会话)    │ USER.md      │ (SQLite 全文索引)         │   │   │
│  │  │              │ (温层)        │                          │   │   │
│  │  └──────────────┴──────────────┴──────────────────────────┘   │   │
│  │  + 8 External Provider 接口 (Honcho/Mem0/...)                  │   │
│  └────────────────────────────┬─────────────────────────────────┘   │
│                               │                                      │
│  ┌────────────────────────────┴─────────────────────────────────┐   │
│  │  AeroCode.AI (V2 升级) — 11 Providers + 3 New                  │   │
│  │  ┌──────────────────┬───────────────────┬──────────────────┐  │   │
│  │  │ 11 Providers     │ 6 Capabilities    │ NEW:             │  │   │
│  │  │ (V2 已有)        │ (V2 已有)          │                  │  │   │
│  │  │ + OpenAI兼容     │ + Summarizer      │ ┌──────────────┐ │  │   │
│  │  │ + Claude         │ + Translator      │ │ToolCall      │ │  │   │
│  │  │ + DeepSeek       │ + AutoTagger      │ │Repairer      │ │  │   │
│  │  │ + Qwen           │ + SemanticSearch  │ ├──────────────┤ │  │   │
│  │  │ + Kimi/GLM/etc   │ + Writer          │ │ModelRouter   │ │  │   │
│  │  │                  │ + QA              │ │(V4-Flash/Pro)│ │  │   │
│  │  │                  │                   │ ├──────────────┤ │  │   │
│  │  │                  │                   │ │Reasoning     │ │  │   │
│  │  │                  │                   │ │Harvester     │ │  │   │
│  │  │                  │                   │ └──────────────┘ │  │   │
│  │  └──────────────────┴───────────────────┴──────────────────┘  │   │
│  └────────────────────────────┬─────────────────────────────────┘   │
│                               │                                      │
│  ┌────────────────────────────┴─────────────────────────────────┐   │
│  │  AeroCode.Core (V2 保留) — Models/Data/Services                 │   │
│  │  Notes | Notebooks | Tags | Search                            │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                       │
│  ┌────────────────────── AeroCode.Mcp (V2 保留) ───────────────────┐  │
│  │  13 Tools + 6 Prompts (外部 AI 调用 AeroCode)                   │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                       │
│  ┌────────────────────── AeroCode.Tests (V2 + V3) ─────────────────┐  │
│  │  V2: 22 tests | V3: +30 tests = 52+ tests                       │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 2. 实施阶段 (6 Stages)

### Stage 1: Skills 引擎 (Hermes + Matt Pocock + DSH 模式)

**目标**: SKILL.md 解析 + 三级加载 + 自动创建 + 自修补

**新增项目**: `src/AeroCode.Skills/`

**核心文件**:
- `Models/Skill.cs` — 技能实体 (Name, Description, Frontmatter, Body, Tools, PlatformGates)
- `Models/SkillFrontmatter.cs` — YAML frontmatter 解析
- `Loader/SkillLoader.cs` — 三级加载 (List 3K → Content → Refs)
- `Loader/SkillParser.cs` — 解析 SKILL.md
- `Registry/SkillRegistry.cs` — 技能注册中心 (类似 Hermes registry.register)
- `Bundled/Engineering/CodeReview.cs` — Matt Pocock code-review skill 实现
- `Bundled/Engineering/Tdd.cs` — Matt Pocock TDD skill
- `Bundled/Engineering/DiagnoseBugs.cs` — Matt Pocock bug diagnosis
- `Bundled/Engineering/GrillWithDocs.cs` — Matt Pocock grill
- `Bundled/Engineering/SetupSkills.cs` — 初始化
- `Bundled/Productivity/SummarizeNote.cs` — Hermes-style
- `Bundled/Productivity/TranslateNote.cs` — Hermes-style
- `AutoCreate/SkillCreator.cs` — 5+ 工具调用 + 成功 → 创建 Skill
- `AutoCreate/SkillPatcher.cs` — Skill 失败时自动 patch

**测试**: 6+ unit tests

**Build/Test 目标**: 0 errors / 0 warnings / 6+ tests pass

---

### Stage 2: Harness 引擎 (DSH + OpenCode + Hermes 融合)

**目标**: Agent 主循环 + Permission + Patch + Plan Mode + 4 Preset + Compaction

**新增项目**: `src/AeroCode.Harness/`

**核心文件**:
- `Agent/Agent.cs` — 主循环 (Hermes ReAct + DSH profile-aware)
- `Agent/AgentLoop.cs` — ReAct loop 封装
- `Permission/Permission.cs` — 权限系统 (OpenCode + DSH guard)
- `Permission/PermissionPolicy.cs` — 策略定义
- `Permission/CommandApproval.cs` — 命令审批 (DSH guard)
- `Patch/PatchEngine.cs` — OpenCode-style patch (search/replace + 模糊匹配)
- `Patch/PatchEnforcer.cs` — Google Small CLs 强制 (< 200 行)
- `Patch/FastApply.cs` — CodeFlow-style (Cursor inline edit)
- `PlanMode/PlanMode.cs` — DSH-style 写保护
- `Compaction/Compactor.cs` — 上下文压缩 (DSH + Hermes)
- `Compaction/SlidingWindow.cs` — 滑动窗口
- `Compaction/LlmSummarizer.cs` — LLM 摘要
- `Presets/Preset.cs` — DSH-style preset
- `Presets/StandardPreset.cs` — 标准
- `Presets/PtcPreset.cs` — 程序化工具调用
- `Presets/MinimalPreset.cs` — 极简
- `Presets/CreativePreset.cs` — 创造
- `Presets/PresetService.cs` — preset 加载/保存/切换
- `EventBus/EventBus.cs` — pub/sub (DSH events)
- `Hooks/HookSystem.cs` — 5 核心 hooks (DSH)
- `CodeReview/CodeReviewer.cs` — 8 维度 (Google + Matt Pocock)
- `CodeReview/ComplexityChecker.cs` — 复杂度
- `CodeReview/NamingChecker.cs` — 命名
- `CodeReview/DocumentationChecker.cs` — 文档
- `CodeReview/CommitMessageGenerator.cs` — CL Description (Google)
- `Goals/Goal.cs` — 任务目标管理
- `Goals/GoalService.cs`
- `SubAgent/SubAgent.cs` — 子代理 (DSH + OmO)
- `SubAgent/SubAgentPool.cs` — 并行 sub-agent
- `Diagnostics/DiagnosticsService.cs` — Token/成本/缓存统计 (DSH + Reasonix)

**测试**: 15+ unit tests

---

### Stage 3: Memory 引擎 v2 (Hermes 4 层 + FTS5)

**目标**: 4 层记忆 + 跨对话搜索 + 自动记忆管理

**新增项目**: `src/AeroCode.Memory/`

**核心文件**:
- `Layers/WorkingMemory.cs` — 热层 (当前会话上下文)
- `Layers/WarmMemory.cs` — 温层 (MEMORY.md + USER.md)
- `Layers/ColdMemory.cs` — 冷层 (SQLite + FTS5)
- `Layers/ExternalMemory.cs` — 外部 (Honcho/Mem0 接口)
- `Storage/SqliteMemoryStore.cs` — SQLite FTS5 存储
- `Storage/MarkdownMemoryStore.cs` — MEMORY.md / USER.md 文件
- `Storage/MemorySchema.cs` — FTS5 virtual table schema
- `Search/CrossSessionSearcher.cs` — 跨对话搜索 (Hermes FTS5 + LLM 摘要)
- `Search/LlmSummarizer.cs` — Gemini Flash 摘要
- `Injection/SystemPromptInjector.cs` — 冻结快照注入
- `Management/MemoryManager.cs` — 自动 add/replace/remove
- `Management/UserProfileTracker.cs` — USER.md 自动更新
- `Providers/IMemoryProvider.cs` — 8 外部 provider 接口
- `Providers/HonchoProvider.cs` — Honcho (辩证用户建模, 简化)
- `Providers/Mem0Provider.cs` — Mem0 (语义搜索, 简化)

**测试**: 8+ unit tests

**依赖**: `Microsoft.Data.Sqlite` (已有, 加 FTS5 扩展)

---

### Stage 4: AI 增强 (Reasonix + OmO 模式)

**目标**: Tool Repair + Model Router + Reasoning Harvest

**修改项目**: `src/AeroCode.AI/`

**核心文件** (新增):
- `Repair/ToolCallRepairer.cs` — Reasonix tool 自愈
- `Repair/Json5Parser.cs` — JSON5 容错解析
- `Repair/SchemaAwareFixer.cs` — 按 schema 补字段
- `Routing/ModelRouter.cs` — 自动 V4-Flash/Pro 切换 (Reasonix + OmO)
- `Routing/TaskComplexityEstimator.cs` — 任务复杂度评估
- `Routing/RoutingRules.cs` — 路由规则
- `Harvesting/ReasoningHarvester.cs` — 捡回 R1 思维链里的工具调用
- `Harvesting/ThinkBlockParser.cs` — 解析 <think> 块

**测试**: 6+ unit tests

---

### Stage 5: Code Review UI + Profile 选择器 (Google + Avernet)

**目标**: MainWindow 集成 Code Review Tab + Profile 选择器

**修改项目**: `src/AeroCode.App/`

**核心文件** (新增):
- `Views/CodeReviewView.axaml` + `.cs` — Code Review Tab
- `ViewModels/CodeReviewViewModel.cs` — 8 维度结果展示
- `Views/ProfileSelectorView.axaml` + `.cs` — 顶部 Profile 选择器
- `ViewModels/ProfileSelectorViewModel.cs`
- `Views/DiagnosticsView.axaml` + `.cs` — 性能监控 Tab
- `ViewModels/DiagnosticsViewModel.cs`
- `Services/CodeReviewService.cs` — 包装 Harness/CodeReview
- `Services/ProfileService.cs` — 包装 Harness/Presets
- `Services/DiagnosticsAggregator.cs`

**修改文件**:
- `MainWindow.axaml` — 新增 Tab + TopBar
- `App.axaml.cs` — 启动时加载默认 Profile

**测试**: 5+ unit tests (CodeReviewService)

---

### Stage 6: 集成测试 + 端到端验证 + V3_DELIVERY.md

**目标**: Build / Test / Smoke 全部通过 + 完整交付文档

**测试**:
- 52+ unit tests
- 端到端: 笔记创建 → AI 分析 → Code Review → 写入 → Memory 持久化

**交付物**:
- `docs/V3_DELIVERY.md` — 完整 V3 报告
- `docs/V3_USER_GUIDE.md` — 用户使用指南
- `docs/V3_DASHBOARD.html` — V3 仪表盘 (可视化)
- `docs/V3_THOUGHT_GRAPH.html` — V3 思维导图

---

## 3. 关键设计决策

### 3.1 Memory 4 层实现

| 层 | 存储 | 容量 | 速度 | 用途 |
|---|---|---|---|---|
| Working | In-memory List | 2-4K tokens | 即时 | 当前会话上下文 |
| Warm | MEMORY.md (2,200 char) + USER.md (1,375 char) | ~1,300 tokens | 即时 | 关键事实/用户偏好 |
| Cold | SQLite + FTS5 | 无限制 | 搜索 (LLM 摘要) | 历史会话 |
| External | Honcho/Mem0 (可选) | 无限制 | 异步 | 高级建模 |

### 3.2 Preset 系统

| Preset | System Prompt | Tools | Model | Safety |
|---|---|---|---|---|
| Standard | 通用助手 | 全套 | auto | ask for dangerous |
| PTC | 代码生成 | 编程为主 | V4-Pro (强) | strict |
| Minimal | 极简调试 | shell + file | V4-Flash (快) | permissive |
| Creative | 探索模式 | 全套 + debug | auto | permissive |

### 3.3 Profile 系统 (Avernet 简化)

| Profile | Skills | 模型 | 用途 |
|---|---|---|---|
| 笔记管理 | SummarizeNote, AutoTagger, SemanticSearcher | V4-Flash | 笔记整理 |
| 代码审查 | CodeReview, ComplexityChecker, NamingChecker | V4-Pro | PR review |
| Bug 诊断 | DiagnoseBugs, GrillWithDocs | V4-Pro | 排错 |
| 项目规划 | ToSpec, ToTickets, DomainModeling | V4-Pro | 大任务 |

### 3.4 Permission 系统 (OpenCode + DSH 融合)

| 工具 | 默认策略 | 用户可配置 |
|---|---|---|
| read_file | allow | ✓ |
| write_file | ask | ✓ |
| edit_file | ask | ✓ |
| run_shell (safe) | allow | ✓ |
| run_shell (rm, mv, etc) | ask | ✓ |
| run_shell (sudo, format) | deny | ✓ |
| web_search | allow | ✓ |
| git_push | ask | ✓ |
| web_browser | ask | ✓ |

### 3.5 Patch 强制 (Google Small CLs)

- 单文件最大: 200 行
- 单次 patch 最大: 10 个文件
- 超出时: 拒绝并建议拆分

---

## 4. 与 V2.0 的兼容性

**保留**:
- 4 项目结构 (Core/AI/Mcp/App/Tests)
- 11 LLM Providers
- 6 Capabilities
- 13 MCP Tools + 6 Prompts
- AI Assistant Tab
- SettingsService (settings.json)
- 22 单元测试

**新增**:
- 3 个项目 (Skills / Harness / Memory)
- 30+ 单元测试 (V3)
- 2 个新 Tab (Code Review / Diagnostics)
- 1 个新 TopBar (Profile 选择器)

**修改**:
- AI 项目: +3 新模块 (Repairer / Router / Harvester)
- App 项目: +2 Tab + TopBar
- 不破坏 V2.0 任何 API

---

## 5. 风险与缓解

| 风险 | 缓解 |
|---|---|
| V3 范围太大, 实施周期长 | 分 6 阶段, 每阶段独立可测 |
| Memory FTS5 性能 | 已用 SQLite, FTS5 是 SQLite 原生扩展, 性能可靠 |
| Skill 自动创建质量 | 5+ 工具调用 + 显式成功才创建; 严格 frontmatter 校验 |
| Tool Repair 误修复 | schema-aware, 失败时回退原始调用 |
| 跨平台兼容性 | 复用 Hermes 跨平台规则, psutil + pathlib |
| Patch 引擎复杂度 | 参考 OpenCode + Cursor 实现, 简化版 |
| Code Review 误报 | 用户可关闭某些维度, UI 提供详细解释 |

---

## 6. 验证标准 (Definition of Done)

**V3.0 完成条件**:
- [ ] 7 个项目编译通过 (Core/AI/Mcp/App + Skills/Harness/Memory)
- [ ] 0 build errors
- [ ] 0 build warnings (TreatWarningsAsErrors=true)
- [ ] 52+ 单元测试全部通过
- [ ] App 启动 6s 内, 无异常
- [ ] Code Review Tab 可用
- [ ] Profile 选择器可用
- [ ] AI 助手 Tab 仍可用 (V2 兼容)
- [ ] MCP server 仍可用 (V2 兼容)
- [ ] Skills 自动创建可演示
- [ ] Memory 跨对话搜索可演示
- [ ] Tool Call Repair 可演示
- [ ] Model Router V4-Flash/Pro 自动切换可演示
- [ ] V3_DELIVERY.md 完整 (≥ 10 KB)

---

## 7. 进度追踪 (Live)

| Stage | 状态 | 文件数 | LOC | 测试 |
|---|---|---|---|---|
| V1.0 (基础) | ✅ Done | 35 | 1520 | 16 |
| V2.0 (AI/MCP) | ✅ Done | 72 | 3221 | 22 |
| Stage 1 (Skills) | 🔄 Next | - | - | - |
| Stage 2 (Harness) | ⏳ Pending | - | - | - |
| Stage 3 (Memory) | ⏳ Pending | - | - | - |
| Stage 4 (AI 增强) | ⏳ Pending | - | - | - |
| Stage 5 (UI) | ⏳ Pending | - | - | - |
| Stage 6 (验证) | ⏳ Pending | - | - | - |

---

## 8. 一句话总结

> **AeroCode V3.0 = Hermes 学习闭环 + OpenCode 工程纪律 + DSH 插件框架 + Reasonix cache 优化 + Matt Pocock 工程哲学 + Google Review 方法论 + Avernet Profile 模式 + CodeFlow 体感** —— 8 项目深度融合，本地优先、双向 AI、跨对话记忆、自我进化的生产级 Agent 操作系统。
