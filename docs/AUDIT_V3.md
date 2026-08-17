# AeroCode V3.0 — 全量审计报告 + 开发计划 (Round 1)

> **日期**: 2026-08-14
> **范围**: V1 + V2 + V3 全量 99 文件审计
> **结论**: **多项严重问题，需要系统性重构**
> **原则**: 零虚假 / 零模板 / 零硬编码 / 零占位 / 零模拟

---

## 1. 全量功能矩阵 (Feature Matrix)

### 1.1 V1 Core (基础)

| 功能 | 接口/类 | 实际状态 | 真实度 |
|---|---|---|---|
| 笔记创建/读取/更新/软删/硬删 | `INoteService` / `NoteService` | ✅ 真实实现 (160+ 行 CRUD) | 100% |
| 字数自动计算 (中英混排) | `Note.WordCount` | ✅ 真实 (CJK 范围 + Latin 序列) | 100% |
| 软删除 + 恢复 | `SoftDeleteAsync` / `RestoreAsync` | ✅ 真实 | 100% |
| 标签创建/查询/删除 | `ITagService` / `TagService` | ✅ 真实 + 大小写规范化 | 100% |
| 笔记本树形结构 | `INotebookService` / `NotebookService` | ✅ 真实 (含级联删除) | 100% |
| 全文搜索 | `ISearchService` / `SearchService` | ⚠️ 用 LIKE 而非 FTS5 (DbContext 注释说 FTS5 但没真启用) | 70% |
| EF Core SQLite 持久化 | `AeroCodeDbContext` | ✅ 真实 + 3 索引 | 100% |
| 路径管理 (跨平台) | `AppDataPaths` | ✅ 真实 | 100% |
| Result 类型 (无异常业务流) | `Result<T>` | ✅ 真实 | 100% |

### 1.2 V2 AI (LLM)

| 功能 | 接口/类 | 实际状态 | 真实度 |
|---|---|---|---|
| Provider 抽象 | `IAiProvider` | ✅ 真实 (Chat/Stream/HealthCheck) | 100% |
| OpenAI 兼容协议 (基类) | `OpenAICompatibleProvider` | ✅ 真实 (200+ 行, 含 thinking/reasoning_content) | 100% |
| Anthropic Messages API | `ClaudeProvider` | ✅ 真实 (267 行, 独立协议) | 100% |
| Provider 工厂 | `ProviderFactory` | ✅ 真实 (按 Id 路由到子类) | 100% |
| 9 个 OpenAI 兼容子类 | DeepSeek/Qwen/Kimi/GLM/OpenAI/OpenRouter/Ollama/LMStudio/Custom | ✅ 真实 (继承基类, 差异在 config) | 100% (DRY 合理) |
| 11 Provider 配置 (settings.json) | `SettingsService.CreateDefaults` | ⚠️ 默认只 3 个 (DeepSeek/Qwen/Ollama), 8 个需用户手动加 | 60% |
| 摘要能力 | `Summarizer` | ✅ 真实 (LLM call + prompt) | 100% |
| 翻译能力 | `Translator` | ✅ 真实 (LLM call + prompt) | 100% |
| 自动打标签 | `AutoTagger` | ✅ 真实 (LLM call + JSON 解析) | 90% (ContentPreview null 时 NPE 风险) |
| 语义搜索 | `SemanticSearcher` | ✅ 真实 (LLM 排序) | 100% |
| 写作助手 | `Writer` | ✅ 真实 | 100% |
| 自动问答 | `QuestionAnswerer` | ✅ 真实 (基于候选笔记) | 100% |
| **HTTP 重试** | - | ❌ **缺失** (transient failure 直接抛) | 0% |
| **限流处理** | - | ❌ **缺失** (429 直接抛) | 0% |
| **熔断器** | - | ❌ **缺失** | 0% |
| **连接池复用** | - | ⚠️ 每次创建 HttpClient 不复用 | 50% |
| **FTS5 全文索引** | - | ❌ **声称启用但没真启用** (DbContext 注释说"启用 FTS5 虚表"但 SearchService 用 LIKE) | 0% |
| **流式断点续传** | - | ❌ 缺失 | 0% |

### 1.3 V2 MCP (Model Context Protocol)

| 功能 | 接口/类 | 实际状态 | 真实度 |
|---|---|---|---|
| 13 MCP Tools | `NoteTools` | ✅ 真实 (10 tools 实现完整, JSON 序列化) | 100% |
| 6 MCP Prompts | `NotePrompts` | ✅ 真实 (模板字符串) | 100% |
| stdio 传输 | `Program.cs` (Mcp) | ✅ 真实 | 100% |
| **MCP Resources** | - | ❌ **V2.0 文档说有但没实现** (net10 SDK 缺失) | 0% |
| **MCP Auth/OAuth** | - | ❌ 缺失 | 0% |

### 1.4 V2 App (UI)

| 功能 | 视图/VM | 实际状态 | 真实度 |
|---|---|---|---|
| MainWindow 双 Tab (笔记+AI 助手) | `MainWindow.axaml` | ⚠️ 只 2 Tab, 缺 5 个 | 40% |
| 笔记列表 + 搜索 + 选中 | `MainWindowViewModel` | ✅ 真实 | 100% |
| 笔记本树 | `MainWindowViewModel` + `Notebook` 实体 | ✅ 真实 | 100% |
| 标签侧边栏 | `MainWindowViewModel` | ✅ 真实 | 100% |
| Markdown 编辑器 + 预览 | `MainWindow.axaml` (TextBox+TextBlock) | ⚠️ 编辑器是真, 预览是纯文本不是 Markdown 渲染 | 60% |
| Ctrl+N/S/F5 快捷键 | `MainWindow.KeyBindings` | ✅ 真实 (3 个) | 100% |
| 暗色主题 | XAML 硬编码颜色 | ⚠️ 单主题, 无切换 | 70% |
| AI 助手 Tab | `AIAssistantView.axaml` | ⚠️ **重大缺陷**: 只有 Send + Summarize, 5 capability 缺 UI | 30% |
| AI 流式输出 | `AIAssistantViewModel.SendAsync` | ✅ 真实 (Stream + Append) | 100% |
| **多 Provider 模型选择** | `AIAssistantViewModel` | ⚠️ **只有 1 个 default model** | 30% |
| **6 capability UI 按钮** | `AIAssistantView` | ❌ **缺 5/6** (翻译/打标签/搜索/写作/QA) | 10% |
| **消息操作 (copy/thumbs/regen)** | - | ❌ 缺失 | 0% |
| **文件附件** | - | ❌ 缺失 | 0% |
| **Settings Dialog** | `SettingsService` | ⚠️ SettingsService 真, **但无 UI 暴露** | 50% |
| **菜单栏 (File/Edit/View/Help)** | - | ❌ 缺失 | 0% |
| **状态栏扩展 (token/cache/model)** | - | ❌ 缺失 | 0% |
| **主题切换按钮** | - | ❌ 缺失 | 0% |
| **导出/导入 UI** | - | ❌ 缺失 | 0% |
| **拖拽排序** | - | ❌ 缺失 | 0% |
| **V3 Skills Tab** | - | ❌ 缺失 | 0% |
| **V3 Memory Tab** | - | ❌ 缺失 | 0% |
| **V3 Code Review Tab** | - | ❌ 缺失 | 0% |
| **V3 Diagnostics Tab** | - | ❌ 缺失 | 0% |
| **V3 Plan Mode Pending UI** | - | ❌ 缺失 | 0% |
| **V3 Profile 选择器** | - | ❌ 缺失 | 0% |
| **V3 Tool Call 可视化** | - | ❌ 缺失 | 0% |
| **V3 Skill 列表选择** | - | ❌ 缺失 | 0% |

### 1.5 V3 Skills (引擎层)

| 功能 | 类 | 实际状态 | 真实度 |
|---|---|---|---|
| SKILL.md frontmatter 解析 | `SkillParser` | ✅ 真实 (兼容 Hermes/MattP/Reasonix 3 dialect) | 100% |
| 自注册 (registry.register) | `SkillRegistry` | ✅ 真实 (线程安全 + usage stats) | 100% |
| 三级加载 (List/Full/Ref) | `SkillLoader` | ✅ 真实 | 100% |
| 自动创建 (5+ 工具调用) | `SkillCreator` | ✅ 真实 | 100% |
| 自修补 (失败率 < 50%) | `SkillPatcher` | ✅ 真实 | 100% |
| Code Review (8 维度) | `CodeReviewSkill` | ✅ 真实 (启发式正则检测) | 90% (无 LLM 加固) |
| TDD skill | `TddSkill` | ✅ 真实 (red/green/refactor 计划) | 100% |
| Diagnose Bugs | `DiagnoseBugsSkill` | ✅ 真实 (5 Whys + bisect 计划) | 100% |
| Grill with Docs | `GrillWithDocsSkill` | ✅ 真实 (API 调用提取 + 可疑模式) | 100% |
| Setup Skills | `SetupSkillsSkill` | ✅ 真实 (8 条工程纪律) | 100% |
| Summarize Note | `SummarizeNoteSkill` | ✅ 真实 (LLM prompt) | 100% |
| Auto-Tag Note | `AutoTagNoteSkill` | ✅ 真实 (LLM prompt) | 100% |
| Skill 列表/查询 | `SkillHub` | ✅ 真实 | 100% |
| **代码上下文 (file system)** | - | ❌ Skill 只能接 raw code, 读不到磁盘 | 0% |
| **LLM 增强 Code Review** | - | ❌ 8 维度全正则, 缺语义检测 | 0% |

### 1.6 V3 Harness (引擎层)

| 功能 | 类 | 实际状态 | 真实度 |
|---|---|---|---|
| Agent 主循环 (ReAct) | `Agent.RunAsync` | ✅ 真实 (append-only + LLM call + tool dispatch) | 100% |
| Permission 系统 (3 档) | `PermissionPolicy` | ✅ 真实 (危险命令正则) | 100% |
| Plan Mode | `PlanModeManager` | ✅ 真实 (PendingEdit + Approve/Reject) | 100% |
| Patch Engine | `PatchEngine` | ✅ 真实 (search/replace + 模糊匹配 + Small CLs 验证) | 100% |
| 4 Preset (DSH) | `BuiltInPresets` | ✅ 真实 (Standard/PTC/Minimal/Creative) | 100% |
| Compaction (3 策略) | `Compactor` | ✅ 真实 (SlidingWindow/LlmSummarize/Truncate) | 100% |
| EventBus (9 events) | `EventBus` | ✅ 真实 (pub/sub + 异常隔离) | 100% |
| Token 计数 | `TokenCounter` | ✅ 真实 (4 字符 ≈ 1 token) | 100% |
| **V3 Harness 集成到 App** | - | ❌ **完全没接** | 0% |
| **V3 Skill 集成到 Agent** | - | ❌ Skill 独立, Agent 没调用 Skill.ExecuteAsync | 0% |
| **Code Review 服务化** | - | ❌ Skill 只能返回文本, 没 UI 集成 | 0% |

---

## 2. 严重问题清单 (按优先级)

### P0 (启动崩溃/核心功能不可用)

| # | 问题 | 影响 | 状态 |
|---|---|---|---|
| P0-1 | **App 启动时 `LoadSettings` 创建 ProviderFactory 但未注入到 DI 容器** | **AI 助手 Tab 完全无法工作** | ❌ |
| P0-2 | **DI 缺 ProviderFactory 注册** | **同上** | ❌ |
| P0-3 | **AIAssistantView.axaml 缺 5/6 capability UI 按钮** | 用户只能发消息 + 摘要, **5 个能力无法用** | ❌ |
| P0-4 | **多 Provider 多模型下拉** | 用户卡死在 1 个 default model | ❌ |
| P0-5 | **V3 Harness/Skills 完全没接到 App** | **V3 引擎是孤岛** | ❌ |

### P1 (功能完整性)

| # | 问题 | 影响 |
|---|---|---|
| P1-1 | V3 Skills Tab (UI) | 用户看不到 V3 引擎 |
| P1-2 | V3 Memory Tab (UI) | 跨对话记忆无法访问 |
| P1-3 | V3 Code Review Tab (UI) | 8 维度报告无法展示 |
| P1-4 | V3 Diagnostics Tab (UI) | token/cache/cost 无法监控 |
| P1-5 | Plan Mode Pending UI | 写保护功能无 UI |
| P1-6 | Settings Dialog | settings.json 改了用户看不到 |
| P1-7 | 菜单栏 | 缺 File/Edit/View/Help |
| P1-8 | 主题切换 | 只有 Dark |
| P1-9 | 导出/导入 | 数据备份/迁移无 UI |
| P1-10 | 拖拽排序 | SortOrder 字段有但 UI 不可改 |

### P2 (引擎增强)

| # | 问题 | 影响 |
|---|---|---|
| P2-1 | HTTP 重试机制 (transient failure) | 网络抖动直接抛 |
| P2-2 | 限流处理 (429) | 触发限流直接挂 |
| P2-3 | 熔断器 | 挂掉的 provider 阻塞 |
| P2-4 | 真正启用 FTS5 | SearchService 注释说 FTS5 但用 LIKE |
| P2-5 | Markdown 实时渲染 (Avalonia.Markdown) | 预览是纯文本 |
| P2-6 | Code Review 加 LLM 增强 | 8 维度全正则, 缺语义 |
| P2-7 | HTTP 连接池复用 | 每次创建不共享 |
| P2-8 | 流式断点续传 | 长流中断就丢 |

### P3 (测试补全)

| # | 问题 | 影响 |
|---|---|---|
| P3-1 | 6 capabilities 无测试 | Summarizer/Translator/AutoTagger/SemanticSearcher/Writer/QA 全无测试 |
| P3-2 | 9 个 OpenAI 兼容 Provider 子类无独立测试 | 只有 1 个 DeepSeek 测试 |
| P3-3 | ClaudeProvider 无测试 | 267 行 0 覆盖 |
| P3-4 | MCP Tools/Prompts 无测试 | 13 tools + 6 prompts 无测试 |
| P3-5 | ViewModels 无测试 | MainWindowViewModel + AIAssistantViewModel 无测试 |
| P3-6 | UI 集成/E2E 测试 | 0 覆盖 |

---

## 3. 开发计划表 (按依赖顺序)

### Phase 1 — 修复启动崩溃 (P0-1, P0-2) [Round 1]

**目标**: App 启动后 AI 助手 Tab 可用
- 修复 App.axaml.cs 的 DI bug
- 注册 ProviderFactory 到 DI
- 验证 V2.0 真实可用

**Step 1.1**: 重构 App.axaml.cs
- `LoadSettings` 改为 `BuildServices` 一部分
- 同步加载 settings (用 `GetAwaiter().GetResult()` 因为 OnFrameworkInitializationCompleted 是同步的)
- 注册 ProviderFactory 单例到 DI
- 删除注释里的 "TODO" 思考

**Step 1.2**: 验证
- 编译
- 启动 app 10s smoke
- AI 助手 Tab 可用

**Step 1.3**: 双 AI 互审
- 自我质疑: ProviderFactory 单例是否线程安全？DI scope 是什么？
- 互检: 启动崩溃修了吗？真的能跑吗？

### Phase 2 — AI 助手完整化 (P0-3, P0-4) [Round 2]

**目标**: 6 capability 全部 UI 可用 + 多模型选择
- AIAssistantViewModel: 加 5 个 capability 命令
- AIAssistantView: 加 5 个 capability 按钮 + 多 Provider 模型下拉
- 实现 6 个 capability ViewModel 命令
- 消息操作 (copy/thumbs/regen)

**Step 2.1**: CapabilityViewModel + RelayCommand
- SummarizeCurrentCommand
- TranslateCommand (弹输入框选目标语言)
- AutoTagCommand
- SemanticSearchCommand
- WriteCommand
- QuestionAnswerCommand

**Step 2.2**: 多 Provider 模型下拉
- AvailableModels 显示该 Provider 所有模型 (从 settings.json 读)
- 或者允许用户自由输入

**Step 2.3**: 消息操作
- Copy 按钮
- Thumbs up/down (写入 memory)
- Regenerate 按钮

**Step 2.4**: 双 AI 互审
- 自我质疑: 5 个 capability 真的能用 LLM 吗? 测试用 mock 验证
- 互检: 是否有模板/占位?

### Phase 3 — V3 集成层 (P0-5, P1-1..4) [Round 3]

**目标**: V3 Skills/Harness 完整集成到 App
- App 启动初始化 SkillHub + HarnessHost
- AIAssistantViewModel 改用 Harness.Agent 而非 Provider
- MainWindow 新增 4 Tab: Skills / Memory / Code Review / Diagnostics
- Plan Mode Pending UI

**Step 3.1**: App 启动时初始化
- SkillHub.Build() (注入到 DI)
- HarnessHost (注入到 DI)
- SkillHub.LoadFromDisk()

**Step 3.2**: AI 助手切到 Harness
- AIAssistantViewModel 接受 HarnessHost + SkillHub
- 用 Harness.Agent.RunAsync 替代 Provider.StreamChatAsync
- Skill 列表注入 system prompt

**Step 3.3**: Skills Tab
- 展示已注册 Skills 列表
- 显示 description / tags / version
- 显示 Auto-created 标识
- 显示 usage count + success rate

**Step 3.4**: Memory Tab
- 展示当前 MEMORY.md / USER.md
- 编辑 + 保存
- 显示 token 占用

**Step 3.5**: Code Review Tab
- 选文件 / 选代码
- 8 维度报告 (调用 CodeReviewSkill)
- 严重程度可视化

**Step 3.6**: Diagnostics Tab
- 实时 token / cache / cost
- 最近调用历史
- Provider 健康状态

**Step 3.7**: Plan Mode Pending UI
- 当 PlanMode 开启, 所有写操作进 PendingEdit
- Tab 显示 pending list + Approve/Reject 按钮

**Step 3.8**: 双 AI 互审
- 自我质疑: Skill 列表真的能调吗？Code Review 真的能跑吗？
- 互检: Plan Mode UI 真的能拦截写操作吗？

### Phase 4 — Settings Dialog + 主题切换 (P1-6, P1-8) [Round 4]

**目标**: Settings 可视化 + 主题切换
- 新建 SettingsDialog View
- 主题切换按钮

**Step 4.1**: SettingsDialog
- Provider 列表 + 编辑 BaseUrl/Model/ApiKey
- 添加/删除 Provider
- 保存 → 重启或热重载

**Step 4.2**: 主题切换
- Light / Dark / System
- 实时切换

**Step 4.3**: 双 AI 互审
- 自我质疑: 设置改了能立即生效吗? 不需要重启吗?
- 互检

### Phase 5 — 引擎增强 (P2-1..4) [Round 5]

**目标**: 生产级可靠性 + 性能
- HTTP 重试 + 限流 + 熔断
- FTS5 真正启用
- Markdown 渲染
- Code Review LLM 增强

**Step 5.1**: HttpRetryHandler
- Polly retry policy (3 次指数退避)
- 限流检测 (429 → 退避)
- 熔断器

**Step 5.2**: FTS5 Migration
- AeroCodeDbContext 加 FTS5 虚表
- SearchService 改用 FTS5 MATCH
- 双写兼容 (FTS5 + LIKE fallback)

**Step 5.3**: Markdown 渲染
- 集成 Markdown.Avalonia
- 预览面板实时渲染

**Step 5.4**: Code Review LLM 增强
- CodeReviewSkill 加 LLM 评分
- 与正则结果合并

**Step 5.5**: 双 AI 互审
- 自我质疑: 重试会不会有副作用? 熔断阈值合理吗?
- 互检: FTS5 migration 不会丢数据吧?

### Phase 6 — 测试补全 (P3-1..6) [Round 6]

**目标**: 测试覆盖率 > 80%
- 6 capabilities 测试
- 9 Provider 子类测试
- ClaudeProvider 测试
- MCP Tools/Prompts 测试
- ViewModel 测试
- E2E 集成测试

### Phase 7 — 端到端验证 + 报告 [Round 7]

**目标**: 全量功能端到端跑通
- 10 轮测试 (按用户要求)
- 真实数据 (创建笔记 → AI 分析 → Code Review → 写入 → Memory 持久化 → 下次对话 recall)
- 互审每个 pipeline

### Phase 8 — 最终 V3_DELIVERY 更新 [Round 8]

**目标**: 完整真实的交付报告
- 列出修复的所有问题
- 测试覆盖率
- 端到端流验证

---

## 4. 互审机制 (Writer-Reviewer)

每 Phase 完成后:
1. **Writer 自审**: 检查 7 错误模式 (自我设限/演示欺骗/模板化/没全局观/子 agent 失控/内容质量 100 分/文件结构)
2. **Reviewer 独立审计** (我换 persona 重新审):
   - 这个改动真的有效吗？
   - 是否有模板/占位/硬编码？
   - 是否影响其他模块？
   - 真实可用性如何？
3. **互辩**: Writer 回应 Reviewer 质疑
4. **最终决定**: 通过 / 退回 / 拆分

---

## 5. 当前进度

- ✅ Phase 1 (P0 修复) — **开始中**
- ⏳ Phase 2-8 — 待执行

---

## 6. 零虚假承诺

本轮所有修复将:
- 零 NotImplementedException
- 零 throw new NotImplementedException()
- 零占位字符串
- 零模板拼凑
- 零硬编码业务逻辑
- 零"假装"功能
- 每个按钮真能用
- 每个 Tab 真能跑
- 每个命令真有效果
