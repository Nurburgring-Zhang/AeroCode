# AeroCode 开发日志

## 2026-08-14 (V1.0)

### 决策
- **产品定位**: 个人生产力/笔记工具 (Grill Me 阶段确认)
- **技术栈**: .NET 9 + Avalonia 11 (跨平台 C# 首选)
- **后端策略**: 纯本地优先,SQLite 文件存储
- **首版范围**: 完整功能版

### 12 阶段流水线执行

| Stage | 产出 |
|---|---|
| 0 预热 | 加载 49 仓库 + Mavis plan |
| 1 Grill Me | 4 个高影响决策 (产品/栈/后端/范围) |
| 2 全局思考 | 跨平台代码共享率 90%+,平台原生体验 |
| 3 检索分析 | 5 轮 web_search: Avalonia 12.0 / Markdown.Avalonia / Microsoft.Data.Sqlite / CommunityToolkit.Mvvm / winget 装法 |
| 4 自质疑 | 反方 3 个: 1-2 周做完整版激进 / 纯本地无后端如何跨设备 / Avalonia 11 Android 稳定性 |
| 5 计划+MoE | 7 任务分配到 7 专家 (架构/数据/UI/测试/文档) |
| 6 执行 | 已写 17 个源文件 + 17 单元测试 |
| 7 中段纠偏 | 持续中 |
| 8 深度自审 | 见末尾 |
| 9 零虚假自测 | 见末尾 |
| 10 双 AI 互审 | 见末尾 |
| 11 交付质检 | 见末尾 |
| 12 记忆归档 | 见末尾 |

### 网络检索摘要

| 检索 | 关键发现 |
|---|---|
| 跨平台栈 2026 | MAUI / Avalonia / Flutter / Tauri 对比 |
| Avalonia 11 Android | 生产可用,被 Unity/JetBrains 信任,12.0 性能 +1867% |
| Markdown.Avalonia | 11.0.0-d1 支持 Avalonia 11.x |
| SQLite .NET 选型 | `Microsoft.Data.Sqlite` + EF Core 最优 |
| winget 装 .NET 9 | `winget install Microsoft.DotNet.SDK.9 --accept-package-agreements` |

### 文件清单 (当前)

```
AeroCode/
├── AeroCode.sln                                ← 解决方案
├── README.md                                   ← 项目主页
├── .gitignore
├── src/
│   ├── AeroCode.Core/                          ← 业务核心 (5 Models, 1 Context, 4 Services, 1 Common)
│   │   ├── Models/   (Note.cs, Notebook.cs, Tag.cs, NoteTag)
│   │   ├── Data/     (AeroCodeDbContext.cs)
│   │   ├── Services/ (4 个 Service 接口 + 4 个实现)
│   │   └── Common/   (Result<T>)
│   └── AeroCode.App/                           ← Avalonia 11 UI
│       ├── Program.cs / App.axaml / App.axaml.cs
│       ├── Services/ (AppDataPaths, DialogService)
│       ├── ViewModels/ (MainWindowViewModel)
│       ├── Views/ (MainWindow.axaml + .cs)
│       └── Converters/ (4 个 IValueConverter)
├── tests/
│   └── AeroCode.Tests/                         ← xUnit
│       └── ServiceTests/  (4 文件, 17 用例)
└── docs/
    ├── ARCHITECTURE.md
    └── DEV_LOG.md
```

### 已知问题

- .NET 9 SDK 装机中 (后台 winget,约 200MB)
- Android 打包待 SDK 装好 + Android SDK 34 安装后验证
- Markdown.Avalonia 在 11.x 下需观察是否有兼容警告 (Windows 直接走 TextBlock 预览,V1 不上 Markdown 渲染管线以避免风险)

### 下一步 (V1.1 候选)

1. 接入 Markdown.Avalonia 真正渲染管线
2. 主题切换 (亮色/暗色)
3. 双向链接 [[wiki]] 解析
4. 导出 Markdown 文件夹
5. Android 真机部署
6. CI/CD GitHub Actions

---

## 2026-08-17 · PHASE 3 / S7 授权 UI 与权限持久化（结论 + 冒烟清单）

### 交付结论

- JsonPermissionStore（%LOCALAPPDATA%\AeroCode\permissions.json）：原子写、缺失/损坏/`"ToolDecisions":null` 一律回退空配置不阻塞启动；枚举按字符串落盘（用户可读可改）。
- DialogPermissionBroker：策略判定 Ask → 真实 Avalonia 授权对话框（允许/拒绝/记住）。纪律：拿不到决定（关闭/取消/无界面/呈现层异常）一律 Deny，绝不静默放行；并发 worker 授权请求信号量串行，门内复检避免重复弹窗，"记住"在门内完成策略写入 + 落盘；对话轮取消 → 决定作废按拒绝收尾。
- SettingsDialog「工具权限」段：全部策略规则三态可编辑（允许/拒绝/每次询问），Save 以保存时刻活跃策略为基线合并 UI 编辑后落盘；开窗前 RefreshFromSources 强制刷新（单例 VM 防陈旧快照）。
- 契约不变式（有测试背书）：显式 Deny 短路一切 Override；Override 只允许升级审慎度（Allow→Ask→Deny），用户设为"每次询问"后危险模式探测不得降级放行；"记住 Allow"不豁免危险模式（run_shell rm/format、delete_note hard=true 仍升级为询问）。
- 测试：新增 23 例（store 回环 8 + broker 11 + SettingsVM 权限段 4），全量 483/468 过/15 网络跳过，连续多轮绿；并发弹窗竞态已压测 8 轮稳定。

### 手工冒烟清单（真实 UI 验收）

1. 启动应用 → 对话中让模型调用会被询问的工具（如未记住决定的 MCP 工具，或临时在设置页把 create_note 改为"每次询问"后说"记一条笔记"）。
2. 授权对话框出现：工具名、参数预览（中文可读）、"记住我的选择"勾选框、允许/拒绝按钮齐备。
3. 点"拒绝"：模型收到 Permission denied 原因并如实改口（不崩溃、不卡死）；工具未执行。
4. 再触发一次同工具 → 再次弹窗 → 勾选"记住"并点"允许"：工具真实执行；%LOCALAPPDATA%\AeroCode\permissions.json 出现该工具 "Allow"。
5. 第三次触发 → 无弹窗直接执行。
6. 重启应用 → 同工具调用不再弹窗（持久化恢复生效）。
7. 打开 设置 → 工具权限：列表含内建笔记/技能工具（MCP 工具在配置启用后也出现）；把该工具改为"拒绝"并 Save → 触发调用被拒且原因如实返回；改回"每次询问"并 Save → 弹窗恢复。
8. 设置页打开期间由对话触发"记住"决定，随后 Save 设置 → 刚记住的决策不被擦除（合并写验证）。

---

## 2026-08-17 · PHASE 3 / S8 Provider/模型管理全 CRUD UI + 热重载链（结论 + 冒烟清单）

### 交付结论

- Provider 编辑区字段补全：SupportsStreaming/SupportsToolCalling/SupportsThinking 三开关 + ThinkingEfforts 档位文本 + ExtraHeaders/ExtraBody 多行 JSON 编辑。校验纪律：JSON 解析失败/空键/null 值 → 原文保留并给出具体错误；切换 provider 时合法文本即时写回 config，非法文本进 `_pendingExtras` 暂存（不丢用户输入）；Save 时任一 provider 有非法暂存 → 整体拒存并指名错误，settings.json 一个字节不动。
- 单个连通性测试：`ProviderFactory.CreateProbe(config)` 一次性探针（不进缓存、独立弹性管线）+ `HealthCheckAsync`（真实最小 chat completion：ping/MaxTokens=4/非流式，30s 超时）。绿路径以真实本地 HttpListener 服务器背书（确认收到请求），红路径对不可达端口如实报 🔴（catch-all 返回 false 不伪造）；Extra JSON 非法时拒绝测试（先提交校验再发网络请求）。
- 模型画像 CRUD：行编辑（ProviderId/ModelId/上下文窗口/最大输出/输入输出成本/速度档位/强项多选复选），运行统计只读展示（"暂无调用记录" 或 "调用 N · 均延 X ms · 失败 F (P%)"）。合并-删除语义：Save 以水合时 `_hydratedProfileKeys` 为删除基线——窗口打开期间运行时自学习的画像绝不被擦除；UI 行 upsert 时保留活画像的实时 Stats（聊天流量统计不被清空）。成本非法/重复键 → 整体拒存。
- 保存→热重载全链：Save → settings.json 落盘 → `ProviderFactory.Reload(AIOptions)`（清缓存/管线、重置熔断）→ `ProvidersChanged` → ChatViewModel 与 AIAssistantViewModel 就地刷新 provider 下拉（选中项仍在则保留，否则落首项/空）。AIAssistantViewModel 补订阅——此前删除 provider 后残留下拉项，选中即 `Get(已删id)` 抛异常。
- 顺带修复两个潜伏生产缺陷（均有回归测试背书）：① SettingsService.LoadAsync 大小写敏感反序列化——Save 写 camelCase、Load 按 PascalCase 读，重载后 provider 字段全丢 → `PropertyNameCaseInsensitive = true`；② `ToAiOptions()` 直接共享活 config 引用——设置页未保存编辑会泄漏进运行中 provider 的 HTTP 请求 → 深拷贝快照隔离。
- 测试：新增 33 例（489 → 522），含 Extra JSON 解析变体、切换提交/暂存、真实服务器 🟢/不可达 🔴、画像落盘 JsonDocument 断言（camelCase settings + PascalCase moa-profiles 双格式）、合并删除保护、快照隔离、双 VM 热重载刷新。全量双轮绿：522 总 / 507 过 / 15 网络跳过 / 0 失败。

### 手工冒烟清单（真实 UI 验收：增→测→改→删全链路 + 画像落盘）

1. 启动应用 → 打开设置 → Provider 区点「添加」新增一个 provider（如 id=testprov，OpenAICompatible，填真实可达 BaseUrl）。
2. 测：选中新 provider → 点「🔌 测试连通」→ 状态栏 🟢（可达端点）；把 BaseUrl 改成 http://127.0.0.1:9/v1 再测 → 如实 🔴（不谎报成功）。
3. 改：切换 SupportsThinking 开关、填 ThinkingEfforts、ExtraHeaders 填合法 JSON → Save → 关闭重开设置，字段回读一致；再填一段非法 JSON → Save 被整体拒绝且 ❌ 指名 provider，切走再切回文本仍在。
4. 删：删除该 provider → Save → 不重启，聊天面板与 AI 助手面板的 provider 下拉立即同步（残留项消失，原选中被删则落首项）。
5. 画像：编辑一个种子画像（改上下文窗口/成本/速度/勾选强项）→ Save → 检查 %LOCALAPPDATA%\AeroCode\moa-profiles.json 该画像字段已更新（PascalCase）；新增画像行 Save 后出现在文件；删除行 Save 后从文件消失。
6. 画像统计：未调用过的画像显示"暂无调用记录"；用该 provider 聊一轮再开设置 → 统计为真实调用数/均延（只读，不可编辑）。
7. 重启应用 → provider 全字段（含 ExtraHeaders/ExtraBody/Supports*）与画像完整恢复。

## 2026-08-17 · PHASE 3 / S9 策略配置 UI：MOA 段全链（结论 + 冒烟清单）

### 交付结论

- DefaultStrategy 进 MoaOptions（首个属性，默认 Single）：新会话的默认编排策略由设置页统一管控。JsonMoaOptionsStore 追加 JsonStringEnumConverter——moa-options.json 里 `"DefaultStrategy": "Router"` 人类可读，且兼容旧版数字值回读；新增回读测试断言原始 JSON 为字符串枚举而非数字。
- 四角色绑定下拉（Router/Planner/Synthesizer/Judge）：选项由 `RoleBindingChoice(Display, Binding)` 实时合成——首项恒为「🤖 自动分配（按画像）」（Binding=null），随后每个已配置 provider 出「id（默认模型）」项，画像 catalog 中 ModelId 非空的画像出「provider :: model」精确绑定项。孤儿绑定（provider 被删后残留的绑定）如实以「（未配置）」标注列出、Save 原样保留绝不静默重置为自动——运行期 ModelResolver 对未配置 provider 自动回退到画像分配，UI 所见即所得。增删 provider 即时重建选项列表且已选项保持。
- EnsembleSize 2-4 钳制：NumericUpDown 限位，Save 与 Hydrate 双侧 `Math.Clamp(x, 2, 4)`（落盘 9→4、读回 1→2 均有测试背书）。MaxUsdPerTurn 校验纪律：留空=不限制（null 落盘）；≤0/NaN/Infinity/无法解析 → 整体拒存，moa-options.json 与单例一个字节不动——因 TurnBudget 对 0 抛异常，门必须拦在 UI 层。三道校验门（Extra JSON → 画像行 → MOA 预算）fail-fast 于任何持久化之前。
- 热生效全链：Save → 就地改写单例 MoaOptions 各字段 → `_moaOptionsStore.SaveAsync` 落盘 → `RaiseOptionsChanged()`。策略在每轮 ExecuteAsync 直读单例字段，就地改写即下一轮热生效（无需重建管线）；OptionsChanged 仅供 UI 订阅。ChatViewModel 订阅后仅在「未选中会话且非流式中」时把策略下拉同步为新默认值——选中会话时下拉代表该会话的策略选择，不被全局默认改写。新会话创建走 `moaOptions.DefaultStrategy`（RecordingSessionService 捕获实参断言）。
- 顺带修复潜伏 P1：App.axaml.cs 曾对 SettingsService 做类型注册（`AddSingleton<SettingsService>()`），而手工 Load 过的实例从未注册——DI 会为 SettingsViewModel 另造一个未 Load 的空实例，设置页将水合空白配置并在 Save 时擦掉 settings.json 全部 provider。改为加载后实例注册 `AddSingleton(settings)`；全量扫 AddSingleton 确认无其他同型遮蔽。
- 测试：新增 18 例（522 → 540）：SettingsViewModelMoaTests 12 例（水合选项合成/预置映射/画像精确绑定项/Save 单例+JsonDocument 落盘断言/冷重载回读一致/空预算 null/越界钳制/预算变体/非法整体拒存/孤儿如实保留/增删 provider 选项联动）、ChatViewModelDefaultStrategyTests 5 例（ctor 种子/新会话实参/无会话跟随/有会话不改写/null 抛）、moa-options 字符串枚举人类可读 1 例。全量双轮绿：540 总 / 525 过 / 15 网络跳过 / 0 失败 ×2。

### 手工冒烟清单（真实 UI 验收：改→存→重载回读一致 + 新会话默认策略生效）

1. 启动应用 → 打开设置 → 滚到「🧭 MOA 编排策略」段：默认策略下拉 5 项（Single/Router/Decompose/Ensemble/Pipeline），四角色下拉首项均为「🤖 自动分配（按画像）」+ 4 个默认 provider 项。
2. 默认策略改为 Router，Router 角色绑定选 deepseek（默认模型），Judge 选「🤖 自动分配」，EnsembleSize 填 3，单轮预算填 0.75 → Save → 状态栏 ✅ 含「MOA Router」。
3. 检查 %LOCALAPPDATA%\AeroCode\moa-options.json：`"DefaultStrategy": "Router"`（字符串非数字）、Router.ProviderId=="deepseek"、Judge==null、EnsembleSize==3、MaxUsdPerTurn==0.75。
4. 不重启：聊天面板当前无选中会话时策略下拉已变 Router；点「新会话」→ 会话策略为 Router（发一轮消息观察路由行为）。已有会话的策略不被全局默认改写。
5. 预算非法验证：填 0 或 -1 或 abc → Save 被整体拒绝 ❌（moa-options.json 时间戳不变）；留空 → Save 成功且文件里 MaxUsdPerTurn 为 null。
6. EnsembleSize 填 9 → Save → 文件回读为 4；填 1 → Save → 回读为 2。
7. 删除一个已被角色绑定的 provider（如 Judge 绑了 qwen 后删 qwen）→ Save → 重开设置：Judge 下拉显示「qwen…（未配置）」孤儿项且仍被选中（未静默重置）；该角色运行时自动回退画像分配。
8. 重启应用 → 默认策略/四角色绑定/EnsembleSize/预算完整恢复（与步骤 3 文件一致）。

## 2026-08-17 · PHASE 3 / S10 全量验证 + 四路 Reviewer 独立复审 + 修复收口（结论）

### 验证方式

S10 不做新功能，只做质量收口：全量重建双轮绿 + 零虚假 grep 全仓扫描 + 四路并行 Reviewer 独立复审（A 审 MOA 策略层 / B 审 App·DI·AXAML / C 审持久化与测试质量 / D 审 S1-S10 交付完整性）。四路报告确认 S1-S9 十项能力全部有真实实现与测试背书，无"只有声明/注释"的假交付；DEV_LOG 数字（489→522→540、双轮绿、15 网络跳过）与仓库现实逐项吻合。

### 复审发现与修复（P0/P1 清零，低成本 P2 一并批修）

- **P0｜AccentPink 资源缺失 → 设置对话框运行时无法加载**（Reviewer-B）：S9 新增的 MOA 段标题引用 `{StaticResource AccentPink}`，而 SettingsDialog 的 Window.Resources 从未定义该键、App.axaml 无全局资源字典——Avalonia 11 对缺失 StaticResource 抛 XamlException，又被 MainWindow 的 catch 吞掉，表现为"点设置无反应"，S7-S9 全部 UI 不可达。修复：补定义 AccentPink(#EC4899)。新增 AxamlResourceConsistencyTests：按 Avalonia 真实查找链（本文件 → 宿主窗口 → App.axaml）静态核对全部视图的资源引用闭包，杜绝再犯。
- **P1｜MaxUsdPerTurn 撕裂读竞态**（Reviewer-A）：`double?` 是 hasValue+value 双字段，CLR 不保证原子读写；设置页保存（UI 线程）与策略每轮读（线程池）交错时可撕裂出 hasValue=true + 陈旧 0.0 → TurnBudget 构造抛异常、该轮莫名失败。修复：get/set 成对持锁。回归：写端 null↔0.75 翻转 + 3 读端并发，断言观察值只可能是完整的 null 或 0.75 且直接构造 TurnBudget 不抛。
- **P1｜工具循环失败路径丢失已发生成本**（Reviewer-A）：RunToolLoopAsync 的四个失败分支（超轮数/两处落库失败/provider 异常）走 FailRunAsync 从不把已累计的真实成本记入 TurnBudget，且 outcome.CostUsd 恒报 0——钱真实花了（各轮消息已带 CostUsd 落库）但预算不记账，同轮后续 worker/judge 可再次花满预算，静默突破用户单轮上限。修复：FailRunAsync 增加 budget+spentUsd 参数，失败收尾如实 AddActual、outcome 返回真实累计。回归：中途失败与超轮数两条真实计价路径断言 SpentUsd/CostUsd 入账。
- **P2 批修**（均带回归测试）：① settings.json 改原子写（随机 tmp+Move，四存储策略对齐）；② LoadAsync 裸 catch 收窄为 JsonException（内容损坏降级默认，IO 故障大声上抛，防"静默变默认后 Save 覆盖用户真实文件"）；③ SaveAsync 重入守卫（IsBusy 之前只设不查，双击并发写可损坏文件）；④ 新增校验门 0：Provider Id 空/重复整体拒存；⑤ GetMessagesAsync 加 ThenBy(Id) 平序键（毫秒同戳消息跨加载排序稳定）；⑥ JsonMoaOptionsStore.LoadAsync 容忍 IOException/UnauthorizedAccessException 降级默认（文件被杀软/备份锁占用不再炸启动，取消异常仍如实上抛）；⑦ 删除 SettingsDialog.Saved 恒假死语义（无消费方）；⑧ 孤儿绑定运行期回归（ghost provider 绑定 → ModelResolver 回退画像自动分配、整轮照常出答）；⑨ TAKEOVER_PLAN 笔记工具数勘误 11→12；⑩ git rm 残留 AeroCode.sln.bak。
- **已知限制（文档化，不在本轮修）**：SaveAsync 跨五文件非事务（中途失败部分落盘，日志已如实标注半变更窗口）；Provider 编辑区直接改活实例（Cancel 不丢弃未保存编辑，既有模式）；工具循环内部与 Ensemble 并行均为"发起前检查"预算语义（单次循环/并行批次内可超支，上限为 N-1 次调用成本）；取消窗口极窄场景可能留 Pending 工具消息；画像并发落盘旧快照后写（自学习统计、下次保存自愈）。

### 收口数字

测试：540 → 550（新增 10 例：AXAML 资源闭包 1、撕裂读/文件锁 2、失败记账 2、孤儿运行期 1、Id 门/损坏回退/原子写 4），全量双轮绿：550 总 / 535 过 / 15 网络跳过 / 0 失败 ×2。零虚假 grep：src 命中全部为分析器 Skill 的检测规则字符串与"禁止 mock"纪律注释，无真实占位。PHASE 3 随本条目后的提交入库。

## 2026-08-18 · PHASE 4 Android 双平台 + 脱敏 + GitHub 发布（结论）

### 交付结论

- **双平台生命周期**：从 MainWindow/SettingsDialog/PermissionDialog 三个 Window 抽出平台无关的 MainView / SettingsView / PermissionDialogView（UserControl），桌面保持模态 Window 壳，Android 走 OverlayService 全屏覆盖层承载同一批视图文件；App.axaml.cs 增加 ISingleViewApplicationLifetime 分支（同一 DI 图 + 同一 MainWindowViewModel），AppDataPaths.RootDirectoryOverride 在 MainActivity.CustomizeAppBuilder 指向 Context.FilesDir/AeroCode（app 私有内部存储，零存储权限；网络权限仅 INTERNET 一条，供 AI Provider API）。
- **AeroCode.App.Android 头项目**：net9.0-android + Avalonia.Android 11.2.2，AvaloniaMainActivity<App>，minSdk 26 / targetSdk 35，AndroidPackageFormat=apk，PIL 生成 192px 启动图标。`dotnet build` 0 错通过，`-t:SignAndroidPackage` 产 debug 签名 APK。
- **构建三连关（NETSDK1082 → NETSDK1047 → NETSDK1150）**：① 删 OpenTelemetry.Instrumentation.AspNetCore（其 AspNetCore FrameworkReference 无 android 运行时包；OTel 埋点实际在 AeroCode.AI，零代码使用）；② .NET 9 SDK 默认把引用方 RID 传染给可执行被引用工程（IsRidAgnostic=false），android-arm64/x64 流入 net9.0 的 exe 工程致 NETSDK1047——App 与 Mcp 显式 `<IsRidAgnostic>true</IsRidAgnostic>` 恒按 portable 编译；③ 自包含 exe 不得引用非自包含 exe（NETSDK1150），两处引用方（App、Android 头）实际都把被引用 exe 当库消费，按 SDK 对测试项目的同款豁免设 `ValidateExecutableReferencesMatchSelfContained=false`。顺带删 Mcp 的裸 `<Reference Include="System.Runtime"/>`（MSB3245 噪音，SDK 工程本就自带）。
- **APK 验证**：aapt2 dump badging 确认 com.aerocode.app / versionName 1.0.0 / minSdk 26 / targetSdk 35 / 仅 INTERNET 权限 / label AeroCode；apksigner verify 通过（CN=Android Debug，SHA-256 2053dd38…d5c6）。XA0141（SkiaSharp/HarfBuzz 未按 16KB 页对齐）为上游对 Android 16 的前瞻提示，不阻塞。Release 签名（keytool + AndroidKeyStore 参数）全流程文档化于 docs/ANDROID_BUILD.md（含本节三连关的架构备注）。
- **Windows publish**：App 自包含 win-x64（131MB 目录）；portable aerocode-mcp.exe 实测无法复用同目录运行时（"You must install .NET"，app-local hostfxr 对框架依赖 apphost 不生效），改以自包含 win-x64 单独 publish 后合并，aerocode-mcp.exe 独立启动冒烟通过（stdiod 等待，超时杀死，无报错）。发行 zip 剔除 .pdb（内含本机构建路径）：328 文件 / 55,731,089 字节。
- **脱敏审计**（三层）：git 全历史 + 工作区按 token/key/pem/JWT/AWS/GitHub PAT 模式扫描，命中仅为秘密检测技能的检测正则与两处显式 FAKE 测试值（sk-1234567890* / hunter2 梗值，属该技能测试夹具）；本次会话 PAT 片段全仓零命中；跟踪的 md 文档零个人路径/凭据；发行物仅 deps.json/runtimeconfig.json 构建元数据。README 重写为真实 V3/PHASE1-4 状态（旧版还停留在 17 用例时代）。
- **发布**：公开仓库 Nurburgring-Zhang/AeroCode（REST API 建库，6 提交含 BASELINE→PHASE 4 全史）；token 一次性内嵌 URL 推送后立即 set-url 清除；Release v1.0.0（ID 372100734）双资产上传，**双向端到端验证**：从 release 下载回 APK 与 zip，SHA256 与本地逐一相同（APK 0ace8c8d…3db3 / zip 0baaac7c…cfa9，校验和写入 Release Notes）。
- **flaky 加固**：MaxUsdPerTurn 撕裂读回归测试的固定 400ms 窗口在重负载（并行构建）下会被 Task.Run 调度延迟整体吃掉→读端零轮→假失败；改自适应停止（采够 2000 样本或 2s 上限），断言语义不变。

### 收口数字

测试：550 总 / 535 过 / 15 网络跳过 / 0 失败（全量第 2、3 轮双绿；撕裂读专项 20/20 稳定）。产物：APK 19,468,555 B（debug 签名）、win-x64 zip 55,731,089 B（自包含 + aerocode-mcp，无 pdb）。

### 已知限制（如实标注，不粉饰）

- APK 为 debug 签名、**未经真机/模拟器实测**（本机无设备），验收以 aapt2 元数据为准；Release 签名流程已文档化待有发布需要时执行。
- Android 上 MCP stdio 子进程、剪贴板复制、Code Review 文件选择器受限：启动期/UI 如实 [DEGRADED] 降级提示，无静默假成功。
- XA0141 16KB 页对齐为上游 NuGet 问题，待 SkiaSharp/HarfBuzz 上游修复。
- aerocode-mcp 自包含合并使发行包体积增加约一倍运行时内容，换取零前置依赖。

### 教训

- bash 下 `-p:Prop=D:\path` 不加引号时反斜杠被当转义符吃掉（SDK 报"找不到目录"，属性值变 D:WORKSPACE…）——MSBuild 属性传参必须加引号。
- .NET 9 P2P 的 RID/SelfContained 传染与 NETSDK1150 校验是 exe 互引结构的硬约束，正解是 IsRidAgnostic + 消费侧豁免，而非逐引用 UndefineProperties 打补丁。
- 自包含 exe 的发布目录里，框架依赖 apphost 不会 app-local 复用运行时——附带 exe 必须各自自包含或文档化运行时前置。

## 2026-08-19 · PHASE 4 修订轮：双 AI 互审修复 + 发布资产替换

### 交付结论

- **双 AI 互审收口**：两位 Reviewer 独立复审 PHASE 4 交付，无 P0；P1/P2 全部修复并回归：
  ① OverlayService 重写为打开栈结构（HasOpenOverlays / TryCloseTop / CloseOverlay 返回 bool 并带 GetVisualDescendants 子树兜底；AttachHost 重挂先收尾旧覆盖层 Task——ShowAsync 的 Task 绝不永挂）；
  ② AvaloniaPermissionDialogPresenter 与 DialogService 的覆盖层路径在 await ShowAsync 之后幂等补结果（返回键关闭 → 诚实拒绝/取消），single-view 无宿主时 [DEGRADED] 降级跳过/按取消，不再误走 Window 路径；
  ③ Android 返回键：TopLevel.BackRequested 是 Avalonia 11.3+ API，11.2.2 不存在（程序集二进制 grep 零命中）→ 改 MainActivity.OnBackPressed 覆写（CA1422 显式豁免并注释理由：框架默认 OnBackInvokedCallback 仍委托到它，minSdk 26 需覆盖 API 26-32），逐层关覆盖层；
  ④ MainActivity 补 Name="com.aerocode.app.MainActivity"（与文档 am start 组件名一致）+ 全量 ConfigurationChanges（Activity 重建会撕裂 Avalonia 视图树）；
  ⑤ AppDataPaths.RootDirectoryOverride 改 set-once（防进程内重入改数据根）；
  ⑥ RegisterToolboxes 在 single-view 平台跳过 MCP stdio 注册（[DEGRADED] 显式记录，避免阻塞启动线程 ANR）；
  ⑦ 设置覆盖层重入守卫 + DataContext 换绑先解绑旧 VM；
  ⑧ App.csproj NETSDK1150 豁免注释改写（旧注释指向已证伪的 app-local hostfxr 路线）；AndroidTargetSdkVersion=35 显式钉住；sln 两处 `Build.0 = ?` 损坏行修复；.gitignore 补 *.apk/*.aab/*.zip。
- **首发 APK 结构缺陷发现与修正**：Debug 配置 SignAndroidPackage 默认不嵌入托管程序集（EmbedAssembliesIntoApk=False，快速部署语义）——旧 APK 473 条目中无任何程序集条目，真机安装必然启动失败，aapt2 元数据检查发现不了。以 `-p:EmbedAssembliesIntoApk=true` 重打并三重验证：包内 lib_<程序集>.dll.so 与 bin 产物字节级一致（含修复代码符号）、aapt2 badging（minSdk 26 / targetSdk 35 / 双 ABI / launchable-activity 与代码 Name 属性一致）、apksigner（同一 debug 证书 2053dd38…）。体积 19.4MB→126.1MB 是程序集按双 ABI 嵌入的诚实代价。
- **Release 资产替换**：DELETE 旧资产（ID 518964907 / 518965550）→ 上传重建产物 → Release Notes 更新 SHA256 与替换说明 → **双向端到端验证**（从 Release 回下两份资产，SHA256 与本地逐一相同）。
- **复验**：全方案 Release 构建 0 错；全量测试重跑 550 总 / 535 过 / 15 网络跳过 / 0 失败；aerocode-mcp.exe 合并产物独立启动（DB 初始化 + stdio transport 完整走通，EXIT=0）。
- **终审批修（第 8 提交，纯文档/注释，不改产物二进制）**：独立 Reviewer 终审（下载-哈希-解包-badging 四重核验资产、逐项核对 13 文件修复、脱敏抽查）结论无 P0，批修其 1 P1 + 2 P2：① README Android 构建命令补 `-p:EmbedAssembliesIntoApk=true`（否则照抄会打出装不上真机的包）；② README MOA 能力表"三策略"更正为实际注册的 Single/Router/Decompose/Ensemble/Pipeline 五策略；③ OverlayService.TryCloseTop 防御分支注释改写（旧注释声称该分支"让 Task 完成"与实现不符——按簿记不变式该分支不可达，注释如实说明）。

### 收口数字

新产物：APK 126,104,920 B（SHA256 e13f0ba4…ded48，debug 签名，程序集已嵌入）；win-x64 zip 55,732,120 B / 328 文件（SHA256 0c5bbd84…3598b，自包含 + aerocode-mcp，无 pdb）。仓库含终审批修提交共 8 提交（第 8 提交为纯文档/注释，资产二进制不变）。

### 已知限制（补遗）

- APK 仍为 debug 签名、**未经真机实测**（新包结构上可启动：程序集已嵌入；验收口径 = badging + 嵌入校验）。
- APK 权限为 INTERNET + 工具链自动添加的 DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION（自定义签名级，targetSdk 34+ 行为，不申请用户资源）。
- Release 配置默认 AOT 会产 100MB+ 包；本轮按 Debug 族复现基线包，Release 签名 + AOT 流程已文档化待发布需要时执行。

### 教训

- APK 验收不能止于 badging/签名：Debug 快速部署默认不嵌入程序集，该盲区让首发包带着"装不上真机"的缺陷通过了验收——"包内程序集条目检查"从此列为必验项。
- API 使用前先验证所引版本实际存在（grep 程序集二进制/查 XML 文档），不凭新版本 API 的记忆写代码（TopLevel.BackRequested 属 11.3+，11.2.2 没有）。
- 复现基线产物必须对齐配置族：Release 默认 AOT 把包从 19MB 涨到 109MB，配置差异靠体积对比才暴露。
