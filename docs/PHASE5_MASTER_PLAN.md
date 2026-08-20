# AeroCode PHASE 5-8 自主化总计划（AUDIT_GAP_REPORT + MASTER_PLAN）

> 日期：2026-08-19 ｜ 状态：经 5 路并行审计（A/B/C/D/E 报告见审计落盘）确认后制定
> 标准：零虚假容忍——本文件只写可验证的承诺，每项验收门禁给出可执行命令或可检查产物。

---

## 第一部分：诚实差距报告（审计结论）

### 1.1 已真实达成（有证据）

| 项 | 证据 |
|---|---|
| Windows 端可构建可测试 | Release 构建 9 项目 0 警告；550 用例 535 过/0 败/15 网络门控跳过（audit/test_win.txt） |
| Android 端真实 APK | Debug-Signed 126MB（08-19）+ Release-Signed 109MB（08-18），双 ABI、AOT、v1 签名验证通过（audit/B_android.md） |
| MOA 进程内编排 | 5 策略真调模型、成本核算、预算控制、画像自学习（src/AeroAgent.Moa 3021 行） |
| 工具循环/权限/持久化/技能/任务图 | WorkerRunner 8 轮循环、DialogPermissionBroker 真弹窗、SQLite 会话、SkillHub 8 技能、TaskGraph DAG 并行 |

### 1.2 未达成（逐条，零掩饰）

| # | 要求 | 现状 | 差距定性 |
|---|---|---|---|
| G1 | 主动任务分析→自动选策略 | 策略人工下拉选定，默认 Single | 缺自主元控制器 |
| G2 | 主动调用 skill/harness/loop/graph | SkillHub 真实；LoopRunner/HarnessHost.CreateAgent/PatchEngine/PluginLoader/SkillCreator/SkillPatcher **全 0 调用死代码** | 原语未接线 |
| G3 | 深度全网检索+下载部署使用 | 无任何搜索引擎 API；WebResearch/Browser/Roslyn/Embedding 技能**未注册**，agent 调不到；无下载部署 | 名不副实 |
| G4 | 需求澄清 | 全仓零代码 | 完全缺失 |
| G5 | 多子 agents 专家集群/异步/调配攻坚 | worker 是一次性模型调用，无持久子 agent、无空余调配、无卡点不亦停工语义 | 部分 |
| G6 | 双 AI 互审互检互监督（运行时） | 仅 Pipeline 单轮评审接力；无对抗迭代、无三态结论、无证据强制 | 部分 |
| G7 | 卡点自动检索/多方案尝试/偏离纠正 | 仅 Polly 重试+模型回退 | 部分 |
| G8 | 任务复盘/全盘自检/补全优化 | 全仓零代码 | 完全缺失 |
| G9 | 双向钢人论证协议 | 全仓零代码 | 完全缺失 |
| G10 | 经验总结与学习 | RecordInvocation 未接主链路；MEMORY.md 不注入对话 | 部分 |
| G11 | RSI 递归自我改进 | 仅未接线的 SkillPatcher 追加注释（且 Serialize 有 bug） | 仅文档设想 |
| G12 | MOA gateway 集成 | 与 moa-gateway-pro v3.1.1 **零集成**（无 /v1/moa/execute 调用、无 X-MOA-Mock、无配额/沙箱/critic） | 平行实现 |
| G13 | 双端互通互控 | 零代码 | 完全缺失 |
| G14 | Android 运行时验证/移动适配/发布签名 | 从未模拟器或真机验证；debug 证书；布局未移动适配 | 未闭环 |

---

## 第二部分：开发总计划表

### 架构决策

1. 新增项目 `src/AeroAgent.Autonomy`（元控制器/钢人/澄清/复盘/经验/RSI），依赖方向单向：Autonomy → (Conversation, Moa, Skills, Harness, AI, Core)，禁止反向引用。
2. 新增项目 `src/AeroCode.Relay`（双端互通：配对/同步/远控协议 + WebSocket 传输），双端 App 共同引用。
3. MOA 网关走**真集成**：新增 `MoaGatewayClient`（/v1/moa/execute + mock 标注透传 + references/critics 落库）+ 网关 sidecar 生命周期管理；本地 5 策略保留为离线回退，网关不可达时显式 `[DEGRADED]` 标注，绝不静默冒充。
4. 每个 Phase 结束执行：`dotnet build`（Win + Android 串行，禁止并行构建——本期审计实证并发会触发 MSB3061 文件锁连锁 AOT 崩溃）→ `dotnet test` → **双 AI 对抗审查**（两个独立 Reviewer 互查对方结论，三态判定+证据强制，失败阻断合入）→ git 提交。
5. 历史教训内建：AXAML StaticResource 键一致性静态测试；double? 预算字段持锁读写；工具失败路径成本入账；settings.json 原子写；git 提交用 `-c user.name/email` 一次性覆盖不写 config。

### PHASE 5 —— 自主内核（3 并行 Builder）

| 任务 | 内容 | 产物 | 验收门禁 |
|---|---|---|---|
| P5-T1 元控制器 | AeroAgent.Autonomy：`MissionController`（任务接收→TaskAnalyzer 类型/复杂度/能力需求分析→自动策略选择→澄清门→规划→执行→校验→复盘→经验写入全链路状态机）；`SteelmanProtocol`（执行前双向钢人：重述问题/正方最强/反方最强/分歧与关键变量/只问一个关键问题；支持 interactive 与 auto-approve 两模式）；`ClarificationGate`；`RetrospectiveEngine`（完成后逐阶段自检+缺口清单+补全建议）；MEMORY.md/经验注入 system prompt | Autonomy 项目 + ≥40 单测 | 构建过、测试过、E2E：给定任务文本→产出含 steelman/plan/strategy 的 MissionRecord 落库 |
| P5-T2 Harness 接线 | 把 LoopRunner 接成真实 Plan→Build→Verify→Review→Fix 环（终止条件/最大轮数/留痕/快照回滚）；HarnessHost.CreateAgent 接为子 agent 工厂；`QualityGate`（合同式验收标准前置+独立评估器+三态结论+证据强制+失败阻断，信号源排序：执行>测试>静态>LLM 评审）；`DualAiArena`（Builder/Reviewer 多轮对抗+钢人前置字段+Judge 裁决+收敛条件） | Harness 扩展 + ≥30 单测 | LoopRunner 真实跑通一个修复环 E2E；DualAiArena 两轮对抗产出三态裁决；死代码清单清零（grep 0 调用项全部接线或移除） |
| P5-T3 真实全网检索 | WebResearch/Browser/Roslyn/Embedding 四技能注册进 SkillHub；`SearchProvider` 抽象 + DuckDuckGo HTML 真实实现（免 key）+ Bing/Tavily 可选 key 接入；`AcquireDeploySkill`：git clone/zip 下载→解压→内容索引→注入为参考上下文（全程留痕+大小/深度上限+危险后缀拦截）；卡点钩子 `BlockadeResolver`（loop 失败→自动触发检索→多方案生成→逐个尝试） | Skills 扩展 + ≥25 单测（网络用例 gated） | list_skills 可见全部技能；DDG 真实查询返回真实结果（网络 gated 测试）；AcquireDeploy 对真实小仓库 E2E |

### PHASE 6 —— 集群与记忆（3 并行 Builder）

| 任务 | 内容 | 产物 | 验收门禁 |
|---|---|---|---|
| P6-T1 专家集群 | `ExpertPool`：持久子 agent（独立上下文/角色/记忆）；`ClusterScheduler` 异步并行调度（任务图分支独立推进，单卡点不亦停工）；空余专家自动调配攻坚（卡点节点获得多专家会战）；Orca 式扇出竞赛（同节点 N 候选→Judge 合并赢家） | Autonomy.Cluster + ≥25 单测 | 3 专家并行异步 E2E；注入单节点卡死→其余分支继续+空余专家会战 |
| P6-T2 MOA 网关真集成 | 本机部署 moa-gateway-pro v3.1.1（pip+env+init-data+serve:8910，脚本化）；`MoaGatewayClient`（execute/references/critics/mock 透传/健康探活）；`GatewaySidecar` 生命周期（App 内启停+watchdog）；UI 网关状态徽标 + X-MOA-Mock 展示；离线回退显式 [DEGRADED] | Moa.Gateway + 部署脚本 + ≥20 单测 | 真实 HTTP 打通 /v1/moa/execute（无 key 时显式 mock 标注透传到 UI）；断网回退标注可见 |
| P6-T3 经验学习 + RSI | `ExperienceStore`（事实/轨迹/方法三分，SQLite+落盘 md，写入与生效分离——下次会话生效）；`SkillCurator`（频率统计/降级/归档/备份回滚）；`RsiEngine` 五层路线落地 L1-L3：输出修正/记忆积累/提示词与策略参数自调优（变异→held-out 验证集门禁→快照可回退→全程留痕）；组合档常开、创造档（SkillCreator 生成新技能）需审批；修复 SkillPatcher.Serialize bug 并接线 | Autonomy.Learning + ≥30 单测 | 任务完成→经验自动落盘→下次会话 system prompt 可见；RSI 一轮自调优 E2E：候选过 gate 生效/不过 gate 回退 |

### PHASE 7 —— 双端互通互控与 Android 闭环（2 并行 Builder）

| 任务 | 内容 | 产物 | 验收门禁 |
|---|---|---|---|
| P7-T1 Relay 互通互控 | AeroCode.Relay：配对（局域网 WebSocket + 配对码/二维码数据）、会话与任务双向同步、远程指令（发起任务/停止/批准权限请求/钢人问题应答）、状态推送；Windows 端可 host、Android 端指挥（Orca 式"桌面干活手机指挥"）；端到端加密（配对派生密钥） | Relay 项目 + 双端 UI + ≥25 单测 | 双实例 E2E：A 发任务→B 收到并执行→进度回传→B 远程批准权限 |
| P7-T2 Android parity+发布 | 触屏布局适配（7-Tab 移动化）；SAF 文件选择器替身；剪贴板适配；MCP 降级改 HTTP 传输可选；release keystore 生成+签名链路脚本化；构建全绿后 APK 产物结构复核 | Android 更新 + 签名 APK | Release APK 重签产出；布局资源通过 AXAML 一致性测试；aapt 校验包结构 |

### PHASE 8 —— 集成、对抗审核、复盘交付

| 步骤 | 内容 | 门禁 |
|---|---|---|
| P8-1 集成 | DI 组合根统一接线（App.axaml.cs 单点）、新增 Tab（Mission/Cluster/Gateway/Experience）、Android head 同步 | Win+Android 串行构建全绿；550+ 新增全部测试过 |
| P8-2 多轮双 AI 对抗审核 | R1 功能符合性（对照本文件 1.2 全部 G 项逐条取证）；R2 安全与诚实性（mock/stub grep 零命中、无静默降级、密钥不落盘）；R3 对抗复审（独立 Reviewer 复审 R1/R2 结论，三态+证据）；每轮发现即修即复验 | 三轮全部 PASS，P0/P1 清零 |
| P8-3 上线测试 | 全量测试多轮（≥3 轮）+ E2E 主链路（任务→钢人→澄清→规划→集群执行→网关 MOA→互审→复盘→经验回流）+ Android APK 结构复核 | 轮轮绿 |
| P8-4 复盘交付 | RetrospectiveEngine 自审 + 人工视角终审：逐 G 项对照证据、交付文档、遗留清单（如真机验证需物理设备，如实标注） | 交付报告落盘 |

### 边界约束

- 禁止 mock/stub/占位实现进生产代码；降级必须 `[DEGRADED]` 显式标注。
- 构建纪律：Win 与 Android 构建**串行**；所有长命令输出落盘 txt 再核实。
- 单文件不超 50KB，组件职责单一；新增测试与实现同提交。
- 每 Phase 提交前：`grep -ri "stub\|mock\|placeholder\|TODO\|FIXME" src/` 生产代码零命中（测试工程豁免但须为合法双替身）。

---

## 第三部分：PHASE 5 完成状态（2026-08-20 登记）

| 项 | 状态 | 证据 |
|---|---|---|
| P5-T1 自主内核 | ✅ 完成 | src/AeroAgent.Autonomy（19 生产文件）；状态机 E2E 落库测试；Autonomy 测试 67 例 |
| P5-T2 Harness 接线 | ✅ 完成 | EngineeringLoop/QualityGate/DualAiArena/BlockadeResolver；死代码 LoopRunner/CreateAgent/PatchEngine/PluginLoader 全部接线；BuiltInRepairs 移除（语义缺陷）；Harness 新增测试 45 例 |
| P5-T3 真实全网检索 | ✅ 完成 | Research 契约+3 provider+AcquireDeploy+13 技能注册+websearch 模式；SkillPatcher G11 修复；Skills 新增测试 79 例 |
| 双 AI 对抗审查 | ✅ 闭环 | R1/R2 独立审查 PASS_WITH_FINDINGS；6 项 P1 全部修复并补回归测试 |
| 全量测试 | ✅ 748 例 730 过/0 败/18 跳过 | 跳过全部为网络/环境门控（AEROCODE_RUN_NETWORK_TESTS 约定） |
| Release 构建 | ✅ Win+Android 串行 | logs-release-build3.txt |

遗留（如实）：构建机 IP 被 DDG 反爬质询拦截（配 BING/TAVILY key 即恢复）；PlanToGraphAsync 待 P6 编排器接线；App 组合根接线属 P8-1。

