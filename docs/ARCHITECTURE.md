# AeroCode 架构设计

> 2026-08-26 全面刷新（原版 2026-08-14 · Mavis Code × 格林）
> 本文与仓库实际结构对齐；与实现不符时以代码为准并欢迎提 issue。

## 1. 项目结构（10 个工程）

```
src/
├── AeroCode.Core            笔记领域：EF Core + SQLite、Result<T>、服务接口（无 UI 依赖）
├── AeroCode.AI              AI Provider 抽象层
│                            ├── OpenAI 兼容协议（DeepSeek/Qwen/Kimi/GLM/OpenAI/OpenRouter/
│                            │   Ollama/LMStudio/MiniMax/自定义）
│                            ├── Anthropic Messages 协议（ClaudeProvider）
│                            ├── Resilience：重试 + 熔断 + 限流 + 超时（Polly v8）
│                            └── Capabilities：Summarizer/Translator/AutoTagger/Embedding…
├── AeroCode.Skills          技能引擎：SkillHub + 内置技能（Analysis/Engineering/
│                            Productivity/Research 四类）+ SkillCreator 自动建技能
├── AeroCode.Harness         Agent 约束层：权限策略（Allow/Ask/Deny + 持久化决策）、
│                            Preset、PlanMode、Compactor、EventBus、TaskGraph、QualityGate
├── AeroCode.Mcp             MCP 客户端网关（stdio 子进程 + 超时/取消纪律）
│                            与 aerocode-mcp 服务端二进制
├── AeroAgent.Conversation   会话编排：Session/Message 持久化、ChatOrchestrationFacade、
│                            SingleStrategy、流式事件映射、Schema 迁移
├── AeroAgent.Moa            多模型编排：Decompose / Router / Ensemble / Pipeline 四策略、
│                            TurnBudget 成本核算、画像目录、moa-gateway sidecar 生命周期
├── AeroAgent.Autonomy       自治层：Mission 控制器、专家集群（Cluster）、学习/RSI 经验、
│                            Steelman 与澄清
├── AeroCode.App             Avalonia 11 UI（桌面与 Android 共用同一套 View/ViewModel）
└── AeroCode.App.Android     Android 宿主头（ApplicationId: com.aerocode.app）
```

## 2. 分层与数据流

```
┌────────────────────────────────────────────────────────────────┐
│                    AeroCode.App (Avalonia UI)                  │
│   Views(AXAML) ◄── ViewModels(CommunityToolkit.Mvvm)           │
│   DialogService / OverlayService / AppDataPaths ← 平台相关     │
└──────┬───────────────┬──────────────────┬──────────────────────┘
       │               │                  │
┌──────▼─────┐  ┌──────▼───────┐  ┌───────▼────────────────────┐
│ AeroCode.  │  │ AeroAgent.*  │  │ AeroCode.Harness           │
│ Core 笔记  │  │ 会话/MOA/自治 │  │ 权限/Preset/PlanMode/事件  │
│ EF+SQLite  │  └──────┬───────┘  └────────────────────────────┘
└────────────┘         │
                ┌──────▼───────┐        ┌──────────────────┐
                │ AeroCode.AI  │───────►│ 外部 LLM API     │
                │ Provider 层  │        │ (仅环境变量持钥) │
                └──────┬───────┘        └──────────────────┘
                       │
                ┌──────▼───────┐        ┌──────────────────┐
                │ AeroCode.Mcp │───────►│ MCP 工具子进程   │
                └──────────────┘        └──────────────────┘
```

编排主链路（一次用户消息）：

```
ChatViewModel ──► ChatOrchestrationFacade
                    │  （每会话 SemaphoreSlim 门，串行同会话并发提交）
                    ▼
              策略选择（Single / Decompose / Router / Ensemble / Pipeline）
                    │
                    ├──► AeroCode.AI Provider（流式 SSE + 空闲看门狗）
                    ├──► TurnBudget 实量成本核算（未知价格跳过、不估算）
                    └──► ChatEvent 流（TextDelta / ToolCall / Completed /
                          Failed / Cancelled）──► 消息落库 + UI 渲染
```

要点：

- **SingleStrategy 位于 AeroAgent.Conversation**（单模型直连是会话层原生能力），
  其余四策略位于 AeroAgent.Moa。
- 失败与取消是**逐消息落库**的：异常时刻所有在途消息各自落为 Failed/Cancelled
  终态并保留已流出的部分内容（`MarkInFlightTerminalAsync`）。
- 所有 provider 流量都经过同一条 Resilience 管线（重试/熔断/限流/超时），
  流式读取有逐行重置的空闲看门狗，空闲超时按 504 语义上抛。

## 3. 关键设计决策

### 3.1 为什么 Core 与 App 分两个 csproj？

| 原因 | 收益 |
|---|---|
| Core 无 UI 依赖 | 单元测试 0 依赖 Avalonia，跑得快（ms 级） |
| 强制接口隔离 | UI 层不能直接 new Core 类型，必须走 DI |
| 未来加 Linux/Web 端 | 共享 100% Core，只需新写 View |
| 跨端一致 | Android / Windows / Linux 同一份业务逻辑 |

### 3.2 为什么用 `Result<T>` 而不是抛异常？

- 业务错误 ≠ 系统异常；异常只用于真正的 unexpected；
- 编译期强制处理失败分支，杜绝静默失败；
- 系统级故障（如 IOException 读不到配置文件）**大声上抛**而非回退默认——
  静默回退会让下次保存用默认配置覆盖用户真实文件。

### 3.3 存储与原子性

SQLite（EF Core 9，`Microsoft.Data.Sqlite`）承担笔记/会话/消息/任务持久化；
settings、权限决策、MOA 选项、画像目录等 JSON 存储统一采用
「随机临时名 + Move 覆盖」的原子写策略，避免半截文件。

### 3.4 为什么 CommunityToolkit.Mvvm 而不是 ReactiveUI？

微软官方 + Roslyn Source Generator 编译期生成 `[ObservableProperty]` /
`[RelayCommand]`，零运行时反射，学习曲线低，Avalonia 11 兼容。

### 3.5 跨平台路径策略

```csharp
// AppDataPaths.cs
RootDirectory = Path.Combine(
  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
  "AeroCode");
// Windows:  C:\Users\<u>\AppData\Local\AeroCode\
// Android:  /data/data/com.aerocode.app/files/AeroCode/  (Avalonia 注入)
```

没有硬编码任何路径字面量，所有路径走 `AppDataPaths` 服务，跨平台零修改。

### 3.6 安全纪律

- API Key 一律从环境变量读取，配置文件只存变量名（`ApiKeyEnvVar`）；
  MCP 环境变量支持 `${ENV_NAME}` 整体引用，密钥不落盘。
- 工具权限默认 Ask；危险命令模式探测（rm/format/sudo/curl|sh、
  `git push --force` 等）强制升级为询问，正则超时宁升不降。
- 技能侧 URL scheme 白名单、工作区路径包含性校验、git 克隆禁用
  `protocol.ext` 与 `core.fsmonitor`，浏览器技能默认带沙箱。
- 本地优先、无遥测；降级必须显式标注 `[DEGRADED]`，成本只认真实用量。

## 4. 错误处理策略

| 层 | 策略 | 例子 |
|---|---|---|
| Service | 业务错误 → `Result.Fail(msg)`；环境故障大声上抛 | DB 冲突 / IOException |
| Provider | 瞬态 HTTP(5xx/429/408) 走重试+熔断；其余 `AiProviderException` | 限流、密钥错误 |
| 编排 | 失败/取消逐消息落库终态，事件流如实上报 | 在途消息保留部分内容 |
| ViewModel | 检查 `Result.IsSuccess`，转译给用户；fire-and-forget 一律观察异常 | 状态栏提示 |
| View | 不直接处理，只绑定 | — |
| 全局兜底 | `App.axaml.cs` `LogToFile` | 启动期未捕获异常 |

## 5. 测试策略

### 5.1 当前覆盖

`tests/AeroCode.Tests` 共 **900+ 用例**（2026-08-26 基线：914 执行，
895 通过，19 条件跳过），按域划分：

```
tests/AeroCode.Tests/
├── ServiceTests/    Core 服务 CRUD、Provider 协议/流式看门狗、热重载、弹性管线
├── HarnessTests/    权限策略、Preset、PlanMode、补丁引擎、任务图、质量门
├── McpTests/        MCP 工具箱、设置、真实子进程 E2E（无宿主时如实跳过）
├── MoaTests/        五策略编排、预算、网关客户端/sidecar、画像目录
├── ConversationTests/ 会话服务、流式映射、Schema 迁移、错误文本
├── Autonomy/        Mission、专家集群、学习/RSI、Steelman
├── AppTests/        ViewModel、AXAML 资源一致性、授权中间人
├── SkillTests/      技能解析/注册/审计类技能
├── E2ETests/        多轮端到轮次
└── RealLLMSmoke.cs  真 LLM 烟囱（无 MINIMAX_API_KEY 时如实跳过）
```

诚实门控：依赖外部条件（真实 API key、Android SDK、MCP 宿主进程）的用例
使用 Xunit.SkippableFact 跳过并如实标注，不伪造通过。

### 5.2 In-Memory SQLite 模式

```csharp
var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
    .UseSqlite("Data Source=:memory:")
    .Options;
var db = new AeroCodeDbContext(opts);
db.Database.OpenConnection();   // in-memory 必须保持连接
db.Database.EnsureCreated();    // 走 OnModelCreating
```

## 6. 安全与隐私

- 数据完全本地，无云端、不收集任何遥测；
- SQLite 文件 OS 级权限保护（Android 沙箱）；
- 日志只写本地 `logs/`。

## 7. 性能预算

| 场景 | 目标 | 实测 |
|---|---|---|
| 启动到首屏 | < 500ms | TBD（尚未建立基准，不编数据） |
| 1K 笔记列表滚动 | 60 FPS | TBD |
| 搜索 1K 笔记 | < 50ms | TBD |
| 保存 100KB 笔记 | < 20ms | TBD |

## 8. 相关文档

- `DEV_LOG.md` —— 逐轮开发/审计日志（含本轮质量收口记录）
- `AERO_AGENT_PHASE3_PLAN.md` / `AERO_AGENT_PHASE6_DELIVERY.md` —— Agent 阶段规划与交付
- `PHASE5_MASTER_PLAN.md` / `V3_*` —— 各阶段计划与交付报告
- `ANDROID_BUILD.md` —— Android 构建说明
