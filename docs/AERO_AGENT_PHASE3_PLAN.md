# PHASE 3 详细开发计划表 — Windows 客户端完整化 + MCP Client

**日期**：2026-08-17 · 前置：PHASE 2 已提交（c76ac5e，354 测试 0 失败）
**原则**：零虚假（真实进程/真实协议/真实持久化）、每步可验证、双 AI 互审收尾。

## 侦察结论（约束设计的关键事实）

- AeroCode.AI 协议层 **已有完整 tools 支持**：`ChatRequest.Tools`、`ChatResponse.ToolCalls`、`ToolCall/ToolDefinition`，OpenAICompatible/Claude provider 已真实序列化/解析（含流式 delta 累积）。缺的只是编排层接线。
- Harness 已有 `PermissionPolicy`（Allow/Deny/Ask 三级 + run_shell 危险命令 Override）但**无持久化、无 UI 回填通道**；`IToolDispatcher` 接口存在但无任何实现。
- MCP server（AeroCode.Mcp）用官方 SDK `ModelContextProtocol 1.0.0` + stdio，12 个笔记工具；**无任何 client 代码**。
- 设置系统 = settings.json（SettingsService），API key 已是环境变量引用式（ApiKeyEnvVar，不落盘明文）。SettingsDialog 已有 provider 基础 CRUD，但**保存后 ProviderFactory 单例不重建**（改动需重启才生效）；MOA 选项/画像**零 UI**。
- WorkerRunner 目前零工具接入点；Decompose workers 与 Ensemble 候选走非流式（工具循环的天然落点）。

## 子步骤计划表

| # | 任务 | 产出 | 验证标准 |
|---|---|---|---|
| S1 | 配置域强化：settings.json 增加 McpServers 段；`ProviderFactory.Reload(AIOptions)` 热重载（保存即生效）；ChatViewModel provider 列表可刷新 | AI/设置层改动 | 单测：Reload 后 Get 返回新配置 provider；SettingsService McpServers 读写回环 |
| S2 | 工具抽象与授权内核：Moa/Tools 下 `IWorkerToolbox`（Domain/Definitions/InvokeAsync）+ `ToolboxRegistry` + `IPermissionBroker`；PermissionPolicy 增加 Upsert/ListRules；工具调用循环接入 WorkerRunner（非流式多轮：assistant tool_calls → 授权 → 执行 → tool 结果回注 → 直至 finish；工具轮次如实不走打字机流式，注释说明取舍） | Moa + Harness 改动 | 单测：ScriptedProvider 扩展 ToolCalls 脚本，验证两轮工具循环、Deny 回注 "Permission denied"、Ask→broker 往返、未知工具如实报错 |
| S3 | 工具持久化与历史回灌：ChatMessage 增列 ToolCallsJson/ToolCallId/Name；EnsureSchemaAsync 补列；tool 消息（Role=Tool, IsFinal=false）落库；HistoryMapper 还原 assistant(tool_calls)+tool 消息序列 | Conversation 改动 | SchemaMigration 式升级测试 + HistoryMapper 映射测试（严格 role 交替 API 可回灌） |
| S4 | 工具事件与 UI 投影：ChatEvent 增 ToolCallStarted/ToolCallCompleted/ToolCallDenied；ChatViewModel 投影为 Tool 气泡（缩进归属、状态角标） | Conversation + App | VM 单测：事件 → 气泡投影 + 跨会话守卫覆盖新事件 |
| S5 | MCP Client：AeroCode.Mcp/Client 下 McpGateway（官方 SDK McpClient + StdioClientTransport，进程级连接管理/工具发现/调用/重启容错）；McpToolbox : IWorkerToolbox（跨服务器工具名去重映射）；server 配置 CRUD 进 settings.json；MCP server 增加 AEROCODE_DB_PATH 环境变量覆盖（E2E 用临时库，不污染用户真实笔记库） | Mcp + 设置 | 真实进程 E2E：测试内拉起 aerocode-mcp 子进程（dotnet exec），list 12 工具断言、create_note→get_note 往返断言（临时 DB）；找不到宿主则 SkippableFact 如实跳过 |
| S6 | 内建工具域整合：NoteToolbox（12 工具直连 Core 服务，真实 DB）、SkillToolbox（list_skills + run_skill，run_skill 经 SkillHub.ExecuteAsync 真实执行，LlmInvoker 走当前会话默认 provider）；App 组合根接线 ToolboxRegistry（内建 + MCP）；Single/Decompose 会话可用工具开关 | App + Moa | E2E（本地 OpenAI 兼容 mock server 脚本化 tool_calls）：对话"记一条笔记" → 真实工具执行 → AeroCode.db 中出现该笔记；skill 工具同链 |
| S7 | 授权 UI 与权限持久化：JsonPermissionStore（permissions.json）；DialogPermissionBroker（Avalonia 授权对话框：允许/拒绝/记住）；SettingsDialog 权限规则列表段 | App | 冒烟脚本 + store 读写回环单测 |
| S8 | Provider/模型管理全 CRUD UI：SettingsDialog provider 编辑区补全字段（Supports* 三开关、ExtraHeaders/ExtraBody JSON 文本校验）、单个连通性测试按钮、模型画像编辑段（强项多选/上下文/成本/速度/统计只读展示），保存→热重载→ChatViewModel 刷新全链 | App | 冒烟：增→测→改→删全链路；画像保存后 catalog 落盘断言 |
| S9 | 策略配置 UI：MOA 段（默认策略、Router/Planner/Synthesizer/Judge 四角色绑定下拉含"自动分配"、EnsembleSize 2-4、MaxUsdPerTurn），写入单例 MoaOptions + JsonMoaOptionsStore 落盘；DefaultStrategy 进 MoaOptions | App + Moa | 配置持久化断言（改→存→重载回读一致）+ 新会话默认策略生效测试 |
| S10 | 全量验证 + 双 AI 互审 + 提交 PHASE 3：全绿 ≥2 轮、零虚假 grep、Reviewer 独立复审 P0/P1 清零 | 提交 | 质量门全过 |

## 依赖方向（不变式）

`App → Conversation → Moa → AI + Harness + Skills → Core`；Mcp 保持独立（仅引用 Core），App 经组合根装配 McpToolbox。工具抽象落在 Moa（WorkerRunner 消费），权限内核在 Harness，UI/持久化/进程管理在 App 与 Mcp。

## 诚实取舍（提前声明）

1. 工具循环走**非流式** ChatAsync：工具轮次的最终文本一次性投影（无打字机），换协议正确性与可测性；纯文本轮次不受影响。
2. API key 维持**环境变量引用式**（不引入 DPAPI 明文风险），UI 明示变量名与探测结果（有/无，不显示值）。
3. MCP E2E 依赖 dotnet 宿主与构建产物路径，缺失时 SkippableFact 跳过并如实标注，不伪造通过。
