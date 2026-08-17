# AeroCode → AeroAgent 接管重做开发计划表

**日期**：2026-08-17
**目标**：将 AeroCode（笔记应用 + AI 工具箱）重做为**全能型开放式 Agent 客户端**——Windows 桌面端 + Android APK，具备 MOA（Mixture-of-Agents）能力：多模型 API 同时接入、统一对话、统一调度分工协作完成任务。
**原则**：零虚假（所有功能真实数据流/真实模型调用/真实持久化）、渐进式复杂度（每阶段可独立验证）、双 AI 互审（每阶段 Builder+Reviewer）。

---

## 一、现状盘点（实测侦察结论）

### 可复用资产（真实、质量合格）

| 资产 | 位置 | 复用方式 |
|---|---|---|
| Provider 层：`IAiProvider`（同步/流式 Chat + HealthCheck + 能力标志）+ 12 实现（DeepSeek/Qwen/Kimi/GLM/OpenAI/OpenRouter/Ollama/LmStudio/MiniMax/Claude/Custom/OpenAICompatible） | `src/AeroCode.AI/Providers/` | **原样复用**为 MOA 的模型接入层 |
| `ProviderFactory` + per-provider Polly 弹性管线（限流/重试/熔断/超时） | `src/AeroCode.AI/Providers/ProviderFactory.cs` + `Resilience/` | 原样复用；MOA 子任务失败回退依赖它 |
| Chat 协议模型：`ChatMessage/ChatRequest/ChatResponse/ChatChunk`（含 thinking 支持） | `src/AeroCode.AI/Models/AiModels.cs` | 原样复用 |
| Harness：ReAct Agent、EventBus（9 事件）、TaskGraph（DAG 拓扑执行）、Compactor、PermissionPolicy、PatchEngine | `src/AeroCode.Harness/` | 复用为 MOA worker 的工具执行引擎 |
| Skills：3 方言解析器、3 级渐进加载、调用学习环 | `src/AeroCode.Skills/` | 原样复用 |
| Core：`Result<T>`、EF Core SQLite、FTS5 | `src/AeroCode.Core/` | 复用；扩展会话/消息实体 |
| MCP Server（笔记工具 12 个） | `src/AeroCode.Mcp/` | 保留；另建 MCP **Client** 接外部工具 |
| DI 组合根、SettingsService、AppDataPaths | `src/AeroCode.App/` | 重构为双平台生命周期 |

### 必须新建（目标缺口）

| 缺口 | 新建项目/模块 |
|---|---|
| 统一对话域（会话/消息/流式聚合/模型归属标注/持久化） | `src/AeroAgent.Conversation/` |
| MOA 编排器（能力画像 + 4 策略调度 + 聚合 + 成本核算） | `src/AeroAgent.Moa/` |
| 统一对话 UI + 调度过程可视化 | `AeroCode.App` 重构 |
| Android 头 + APK | `src/AeroCode.App.Android/`（net9.0-android） |
| 模型/Provider/策略管理全 CRUD UI | 设置系统扩展 |

### 环境缺口（Phase 0 解决）

- ❌ .NET 9 SDK（安装中，dotnet-install 脚本 → `%USERPROFILE%\.dotnet`）
- ❌ Android workload（`dotnet workload install android`）
- ❌ JDK 17（Android 构建必需）
- ❌ Android SDK 平台/构建工具（workload 自动供给 + 手动补 cmdline-tools）

---

## 二、目标架构

```
src/
├── AeroCode.Core/            ← 保留：Result<T>、EF Core、FTS5（扩展会话/消息实体）
├── AeroCode.AI/              ← 保留：Provider 层（MOA 的模型接入基座）
├── AeroAgent.Conversation/   ← 新建：统一对话域
│   ├── Models/                ChatSession、ChatMessageEntity、MessagePart
│   ├── Data/                  ConversationDbContext（SQLite，会话+消息+归属）
│   ├── Services/              SessionService、ChatOrchestrationFacade
│   └── Streaming/             StreamAggregator（多模型流合并、归属标注）
├── AeroAgent.Moa/            ← 新建：MOA 编排器
│   ├── Profiles/              ModelProfile（强项标签/上下文窗口/成本/速度）
│   ├── Strategies/            SingleStrategy、RouterStrategy、
│   │                          DecomposeStrategy、EnsembleStrategy、PipelineStrategy
│   ├── Planning/              TaskPlanner（planner 模型拆解子任务 DAG）
│   ├── Assignment/            ModelAssigner（按能力画像分配子任务）
│   ├── Aggregation/           Synthesizer（聚合模型合成最终答复）
│   └── Accounting/            CostTracker（token/费用/延迟核算）
├── AeroCode.Harness/         ← 保留：worker 工具执行引擎
├── AeroCode.Skills/          ← 保留
├── AeroCode.Mcp/             ← 保留 server；新增 McpClient 接外部工具
├── AeroCode.App/             ← 重构：统一对话 UI + 调度可视化 + 双平台生命周期
└── AeroCode.App.Android/     ← 新建：Android 头（net9.0-android → APK）
```

**依赖方向**（单向，禁循环）：
`App / App.Android → Conversation → Moa → AI(Providers) + Harness + Skills → Core`

**统一对话数据流**：
```
用户输入 → ChatOrchestrationFacade → 策略选择（Single/Router/Decompose/Ensemble/Pipeline）
  → [Decompose 路径] TaskPlanner(planner 模型) → 子任务 DAG
     → ModelAssigner 按画像分配 → 并行 worker（各模型流式执行，可带工具）
     → Synthesizer(聚合模型) 合成 → 最终答复
  → 全程消息带归属（model_id + role: planner/worker/judge/synthesizer）
  → StreamAggregator 汇入统一对话流 → UI 渲染（含调度过程面板）
  → ConversationDbContext 持久化（会话/消息/归属/成本）
```

---

## 三、MOA 调度分工设计（核心）

### 3.1 模型能力画像（ModelProfile）

每个已配置模型一份画像（用户可编辑 + 运行自学习）：
- `strengths[]`：code / writing / analysis / translation / math / vision / planning / review
- `contextWindow`、`maxOutputTokens`
- `costPerMIn/MOut`（用于成本核算与预算控制）
- `speedTier`：fast / medium / slow（影响路由偏好）
- 能力标志继承自 `IAiProvider`：SupportsStreaming / SupportsToolCalling / SupportsThinking

### 3.2 五种编排策略

| 策略 | 触发 | 流程 | 适用 |
|---|---|---|---|
| SINGLE | 默认/用户指定模型 | 直连单模型流式 | 简单问答 |
| ROUTER | 自动路由开启 | 快速模型分类任务类型 → 按画像路由到最优模型 | 日常混合请求 |
| DECOMPOSE（分工） | 复杂任务/手动 | planner 拆子任务 DAG → 按画像并行分配 → synthesizer 聚合 | 多步骤复杂任务 |
| ENSEMBLE（集成） | 高置信需求 | N 模型并行作答 → judge 模型裁决/合成 | 关键决策 |
| PIPELINE（流水线） | 质量敏感 | 起草模型 → 评审模型 → 修订模型 顺序接力 | 写作/代码产出 |

### 3.3 统一对话协议

每条消息实体携带：`session_id / role(user|assistant|system|tool) / parts[] / attribution{model_id, provider_id, strategy_role(planner|worker|judge|synthesizer|router), tokens_in/out, cost, latency_ms} / parent_message_id`（树形，支持分工过程展开）。

### 3.4 失败处理

子任务级：Polly 重试 → 同策略备用模型回退 → 降级标注（诚实返回 partial + degraded 标记，绝不伪造完成）。预算控制：单请求成本上限可配置，超限中止并报告。

---

## 四、分阶段开发计划表

### Phase 0 — 环境与基线（本阶段立即执行）

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 0.1 | .NET 9 SDK 安装 | `%USERPROFILE%\.dotnet` | `dotnet --list-sdks` 显示 9.0.x |
| 0.2 | Android workload + JDK 17 | workload 清单 | `dotnet workload list` 含 android |
| 0.3 | 基线构建存量 sln | bin 产物 | `dotnet build AeroCode.sln` 0 错误 |
| 0.4 | 基线测试实跑 | 测试结果 | 存量测试套件实跑，记录真实通过数（宣称 174，实测为准） |
| 0.5 | git 初始化/提交基线 | 版本库 | 后续所有改动可追溯 |

### Phase 1 — 会话域 + 统一对话（Windows）

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 1.1 | `AeroAgent.Conversation` 项目骨架 + 实体（ChatSession/ChatMessageEntity/Attribution） | 新项目 | 编译过 |
| 1.2 | ConversationDbContext + 迁移 + SessionService（会话 CRUD/重命名/删除/搜索） | 持久化层 | xUnit：建会话→写消息→重开读回一致 |
| 1.3 | ChatOrchestrationFacade（SINGLE 策略先行：真实流式调用 + 取消 + 错误透传） | 编排门面 | xUnit + 冒烟：真实 provider mock-server 流式断言 |
| 1.4 | StreamAggregator（chunk 合并、归属标注、中断恢复） | 流聚合器 | 单测：乱序/中断/多段流断言 |
| 1.5 | 统一对话 UI（会话列表 CRUD + 消息流渲染 Markdown + 流式打字机 + 停止按钮 + 模型选择） | XAML + VM | 手工冒烟脚本 + UI 结构测试 |
| 1.6 | 历史会话持久化验证（重启 App 会话不丢） | — | 进程级存活断言 |

### Phase 2 — MOA 编排器

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 2.1 | ModelProfile 模型 + 画像管理（settings 持久化 + 自学习记录） | Profiles | 单测：画像读写/强项匹配 |
| 2.2 | RouterStrategy（路由模型调用 + 分类提示词 + 路由决策落消息归属） | 策略 | 单测（本地 mock LLM server 真实 HTTP）+ 集成 |
| 2.3 | TaskPlanner + DecomposeStrategy（子任务 DAG 生成 → 复用 TaskGraph 执行） | 策略 | 集成测试：2 子任务并行真实执行 |
| 2.4 | ModelAssigner（画像匹配 + 成本预算 + 回退链） | 分配器 | 单测：能力匹配/预算中止/回退 |
| 2.5 | EnsembleStrategy + judge 裁决 | 策略 | 集成测试：双模型并行 + judge 合成 |
| 2.6 | PipelineStrategy（顺序接力 + 阶段产物可见） | 策略 | 集成测试 |
| 2.7 | Synthesizer + CostTracker | 聚合/核算 | 单测：token/成本累加正确 |
| 2.8 | 调度过程可视化 UI（策略选择器 + 子任务面板：哪个模型在做什么/状态/成本） | UI | 冒烟脚本 |

### Phase 3 — Windows 客户端完整化

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 3.1 | Provider/模型管理全 CRUD UI（增删改查 + 连通性测试 + 密钥走环境变量/加密存储） | 设置页 | 冒烟：增→测→删全链路 |
| 3.2 | 策略配置 UI（默认策略/路由开关/预算上限/模型分工绑定） | 设置页 | 配置持久化断言 |
| 3.3 | MCP Client（接外部 MCP 工具服务器，工具注入 worker） | McpClient | 对接本地测试 MCP server 实测 |
| 3.4 | 工具调用 UI（工具执行授权 3 级权限复用 PermissionPolicy） | UI | 冒烟 |
| 3.5 | 保留域整合（笔记/Skills/Code Review 作为内建工具域挂入统一对话） | 整合 | 对话内调用笔记工具实测 |

### Phase 4 — Android + APK

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 4.1 | App 生命周期双平台化（desktop + single-view 分支） | App 重构 | 桌面回归不退化 |
| 4.2 | `AeroCode.App.Android` 头项目（net9.0-android + Avalonia.Android） | 项目 | `dotnet build` 过 |
| 4.3 | 平台服务适配（文件路径/对话框/输入法/触摸滚动） | 服务层 | 模拟器或设备冒烟 |
| 4.4 | 触摸 UI 适配（抽屉式会话列表/底部输入栏） | XAML 变体 | 布局审查 |
| 4.5 | APK 打包签名（debug 签名先行；release 签名方案文档化） | `*-Signed.apk` | `aapt dump badging` 信息正确 + 安装验证（有设备则实测，否则诚实标注） |

### Phase 5 — 全量验证 + 交付

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| 5.1 | 全量测试矩阵（单测 + 集成 + mock-LLM-server E2E + 双数据库无关） | 测试报告 | 全绿，覆盖率核心域 ≥90% |
| 5.2 | 双 AI 互审（Builder/Reviewer 独立上下文 + 红队对抗） | 审查记录 | P0/P1 清零 |
| 5.3 | 端点/功能全扫 + 零虚假 grep（mock/stub/TODO/NotImplemented 零命中） | 扫描报告 | 0 命中 |
| 5.4 | 打包：Windows（publish 单文件）+ Android（signed APK）+ 源码归档 | 交付包 | 包完整性校验 |
| 5.5 | 交付记分卡（实测证据链 + 诚实残留） | RELEASE_SCORECARD | — |

---

## 五、质量门（每阶段强制）

1. 编译 0 错误；核心域测试覆盖率 ≥90%。
2. 零虚假红线：grep `mock|stub|placeholder|NotImplemented|TODO` 零命中（测试基建内的本地 mock LLM server 除外——它是真实 HTTP 服务器，用于无 API key 环境的端到端验证，属测试工具非产品代码）。
3. 每阶段结束 Builder→Reviewer 互审，P0/P1 清零才进入下一阶段。
4. 所有 LLM 路径验证：有 key 用真实调用，无 key 用本地 OpenAI 兼容 mock server 走真实 HTTP（禁止进程内假实现）。

## 六、风险与诚实边界

| 风险 | 应对 |
|---|---|
| 本机无 Android 真机 | APK 构建+签名可完成；真机实测若不可行则诚实标注"模拟器/真机未验证" |
| Avalonia Android 成熟度 | Phase 4 前做最小冒烟头验证可行性，不可行则改 Kotlin 薄壳 + 共享核心（预案） |
| 无商用 LLM key 时 MOA 验证 | 本地 mock LLM server（真实 HTTP、可多实例模拟多模型）完成全链路 E2E |
| 存量大改回归风险 | 每 Phase 独立分支提交 + 基线测试回归 |
