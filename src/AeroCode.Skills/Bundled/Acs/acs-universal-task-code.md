---
name: universal-task-code
description: 通用任务执行主入口（Agent Core Suite 能力增强层，叠加于终端原生能力之上而非替代它）。接到任何非闲聊任务时必用：先双向钢人论证，再按 T0-T3 分级触发 P0-P6 七道门流水线与 G0-G6 质量门禁，配合四道成本闸门（窄步/禁回灌/空转/思考预算）与有界重试控制 token 与时间，每步收尾写 ≤1000 字压缩交接到单文件 .acs/handoff.md 并做 builder≠verifier 的双 AI 自对抗审核（STEP_handoff / STEP_self_review 逐步机检），按需加载 loop-engineering、graph-engineering、self-verify-scaling、token-thrift 四个子技能。适用于软件工程、研究调研、文档写作、数据分析、设计、运维等全部任务形态的启动、推进、验证与交付；三重零容忍（零虚假、零模拟实现、零降级）。SKIP 边界：纯闲聊、单句寒暄、一次性事实问答、单文件只读查看属 T0，不触发本流水线，只需遵守诚实纪律；已在本技能流程内的后续步骤不重复加载入口。
---

# 通用任务执行主入口（Agent Core Suite v2.3.0）

## 第零步：看清定位——增强，不是替代

本套件叠加在你**原有能力之上，只做加法**：

- **不禁用、不接管、不改写**你终端原生的工具、检索能力、任务清单、记忆系统、权限与审批机制。
- **原生优先**：原生已有等价能力（任务清单/计划、检索子代理、记忆）就用原生的；`templates/` 仅在原生缺位时兜底，**禁止另起并行体系**。
- 本套件只补三样：① 分级触发的执行 SOP ② 可机检的准出退出码 ③ 成本闸门的量化阈值。
- **冲突裁决顺序**：主人显式指令 > 终端原生安全/权限/审批约束 > 本套件硬门禁 > 本套件软约束。

效力：下文纪律在本套件的职责边界内为硬纪律，不得跳门、不得降格，除非主人明确批准并记录在案；**但它管的是「标准与证据」，不是「必须走哪个具体工具」**——等价手段达成均可，只要证据可机检。

**两类约束必须分清，禁止混称**：
- **软约束**（本技能与规则文本，Context 层注入）：靠自觉执行，可能漂移。
- **硬约束**（`scripts/` 门禁脚本，Harness 层）：真实运行、有退出码与证据文件，是唯一可证明的准出依据。
- 只要能跑硬门禁就必须跑，不许用「我已检查」替代脚本输出。

## 第一步：分级（T0-T3），先定级再动手

| 级 | 判定（任一命中即升级） | 流水线 | 候选 N | 重复评估 R |
| --- | --- | --- | --- | --- |
| T0 | 闲聊 / 纯问答 / 单文件只读 | 仅诚实纪律 | 0 | 0 |
| T1 | 单文件、可逆、≤15min、无外部副作用 | P0(压缩)→P3→G3→G4 | 1（rubric 自检） | 1 |
| T2 | 多文件 / 有持久状态 / 需测试 / 涉及构建 | P0-P6 全量 | 1（Builder≠Verifier） | 2 |
| T3 | 不可逆（删除/迁移/部署/推送）或 架构级 或 安全相关 或 ≥5 文件跨模块 | P0-P6 全量 + 候选竞标 + 双盲终审 + 工件登记 | 3~5 | 3 |

定级结论与理由写入 `task-state.json` 的 `tier` / `tier_reason`。**升级容易、降级需理由**：拿不准时取高一级。

## 第二步：七道门流水线（P0-P6）

| 门 | 动作 | 准出速查 |
| --- | --- | --- |
| P0 钢人门 | 问题重述 → 正向钢人 → 反向钢人 → 分歧定位 → 只问 1~3 个关键问题 → 得到回答后给判断/理由/下一步 | G0 |
| P1 认知门 | 深挖背景/过程/目标/外部/风险五类信息，输出《任务认知摘要》 | G1 |
| P2 规划门 | 四件套：项目规划、详细设计与标准、每阶段目标明细、边界约束；并明确 harness/loop/graph 的启用方式 | G2 |
| P3 执行门 | 按任务 DAG 派发（可并行优先）+ Builder/Verifier 分离互审 + 卡点攻坚（深搜→多方案→会战→如实上报） | G3 |
| P4 验证门 | 逐条核验验收标准并附证据；真实运行测试；与认知摘要比对校准方向 | G4 |
| P5 交付门 | 自我对抗复盘 + 多轮对抗审核（≥2）+ 多轮上线测试（≥2）+ 交付报告四要素 | G5 |
| P6 进化门 | 经验沉淀 + 守则自进化 + RSI 闭环记录（问题→根因→改进→验证→沉淀位置） | G6 |

G0-G6 完整准出条件见 [reference-gates.md](reference-gates.md)。十八条守则原文见 [reference-creed.md](reference-creed.md)。

## 第三步：四道成本闸门（全程生效，T1 起强制）

| 闸门 | 硬阈值 | 违反后动作 |
| --- | --- | --- |
| 窄步闸 | 单步 1 个可验证产出；预算 15~30min；完成标准必须是命令/文件/断言 | 拆分该步再开工 |
| 回灌禁令 | 跨步只传 ≤1000 字压缩总结（`loop.max_summary_chars`）+ 状态文件指针，禁止全量上文回灌；单步交接载体目标 ≤400 字（`handoff.target_chars`）、硬上限 1000 字（`handoff.max_chars_hard`） | 改读 `task-state.json` + 摘要 |
| 空转闸 | two-strike：连续 2 步无新证据（文件/测试/事实均无变化） | 立即停止并升级，禁止同法重试 |
| 思考预算闸 | 思考占比 ≤0.40 | 转为写最小可执行验证，停止空想 |
| 有界重试（v1.1） | `retries` ≤ `spec.retry.max_retries`，且重试必须写 `retry_reason` 与 `delta_from_last`（这次与上次差在哪） | 超限即换路；说不清变化就是同法重试 |

依据：某 27B 模型实测 9 个 Agent 任务烧掉 13,995,350 token / 6h17m，其中输入 token 占 98%、单题 11.5M token，根因正是单步过重、完成标准不清、上下文反复回灌与空转（详见 loop-engineering）。FinBot 实战另证：生成→报错→原样再生成是永动机，重试必须有上限、有留痕、有变化。

## 第三步·补：每步收尾双动作（v2.1 逐步机检）

回灌禁令与双 AI 对抗审核原先只在门/分级级判定，跨步无强制载体、错误累积到 G3/G5 才暴露、返工成本按步数放大。v2.1 把两者下沉到**每一个 step 收尾**，由 `gate_checklist.py` 逐步机检（阈值单一真相源在 `spec/thresholds.json` 的 `handoff` / `per_step_review` 节，Python 与 Node 双实现同口径）：

- **STEP_handoff —— 每步压缩交接**：写目标 400 字、硬上限 1000 字的总结到**单文件** `.acs/handoff.md`，含①当前目标锚点（防跑偏）②已完成项③下一步三要素，无损且附偏离自检（`drift_checked=true`）；`steps[].handoff` 是其结构化镜像。下一步只读这个载体 + 状态指针，不回灌历史。渲染/校验用 `scripts/acs_compress_handoff.py`（机械渲染幂等、无镜像则 exit 2 绝不伪造）；**语义压缩仍是 agent 原生职责，脚本不假装代劳**。
- **STEP_self_review —— 每步双 AI 自对抗审核 + 自查自检自监督**：`builder≠verifier` 分离署名（自签=自评不算数），落 `steps[].review`（reviewer / verdict∈{pass,pass_with_fixes,fail} / issues_found / issues_closed / self_check）；发现问题必须**等量闭环**并写复验记录 `recheck`（命令 + 真实输出）；`verdict=fail` 禁止进入下一步；T3 每步 ≥2 轮。

`hooks/acs-stop.sh`（post-turn）+ `hooks/acs-prompt.sh`（UserPromptSubmit）是 best-effort 加速器，恒 exit 0、缺状态即静默放行、保留终端既有 hook 条目（保强只增不删）；stdout 注入与非零阻断未实测（known_gap），**强制点始终回落到 `gate_checklist.py`，不依赖 hook**。跨终端可迁移：映射见各产品 `spec/<product>-global.json` 的 `native_routing.handoff`。

## 第四步：按需加载子技能（渐进披露，禁止一次全装）

| 触发场景 | 加载 |
| --- | --- |
| 需要定义停止条件/收敛/重试/空转判定 | `loop-engineering` |
| 需要拆分并行子任务、定义节点契约、边门/汇聚语义、工件登记、派发子 agent | `graph-engineering` |
| T3 任务、方案竞标、审核打分、双盲终审、方向偏离检测 | `self-verify-scaling` |
| 上下文接近上限、需要压缩总结/状态外置/续跑 | `token-thrift` |

## 第五步：跑硬门禁（有脚本就必须跑）

```
python -X utf8 <SUITE>/scripts/run_gates.py --state .acs/task-state.json --root . --tier T2
```
单门可独立调用：`gate_reality_scan.py`（真实性扫描）、`gate_state_validate.py`（状态契约）、`gate_loop_guard.py`（四闸门）、`gate_verify_rank.py`（候选排序）、`gate_checklist.py`（G0-G6 清单 + STEP_handoff / STEP_self_review 逐步机检）、`gate_honesty_ledger.py`（诚实台账）、`gate_rule_consistency.py`（规则体系自洽）、`gate_skill_spec.py`（技能规范）、`gate_capability_registry.py`（能力治理）。退出码：0=PASS，1=BLOCK，2=USAGE_ERROR（视为未验证=未完成）。

在本终端中还必须叠加原生能力（映射随终端而变，纪律不变；单一真相源是 `spec/adapters.json`）：

- **Qoder**（本套件唯一 `verified=empirical` 终端）：需求缺口用 `AskUserQuestion`，进度用 `TaskCreate / TaskUpdate / TaskList`，并行节点和独立验证用 `Agent`，专业流程用 `Skill`，长期经验用 auto-memory 双 scope，懒加载 MCP 用 `mcp_list / mcp_get / mcp_call`，定时任务用 `qoder_cron`，多会话编排用 `create_chat_session / wait_chat_sessions`。
- **QoderWork**：需求缺口用 `AskUserQuestion`，进度用 `TaskCreate / TaskUpdate / TaskList`，并行节点和独立验证用 `Agent`，专业流程用 `Skill`，长期经验用 `memory / memory_search / memory_get`，产品自身状态用 QoderWork Connector `qw_query / qw_action`，懒加载 MCP 用 `qw_mcp_list / qw_mcp_get / qw_mcp_call`，定时任务用 `qoder_cron`，文件交付用 `present_files`。
- **千问办公（QwenWorkCN）**：需求缺口用 `AskUserQuestion`，进度用 `TodoWrite`，并行节点和独立验证用 `Agent`，专业流程用 `Skill`，长期经验用 Memory，产品自身状态用 `QwenWork Connector`，文件交付用 `qwenwork_file_present_files`。
- **未登记终端**：走 `generic` 兜底档（`enforcement_level=soft_only`），只保证纪律文本与门禁脚本可读可跑，**不得声称已在该终端实跑验证**。

全局融合任务或启动自检运行共用多产品引擎：

```
python -X utf8 <SUITE>/scripts/acs_global_verify.py --product qoderwork    # 或 --product qwenworkcn
```

该探针只证明可配置的全局 SOUL/AGENTS、六个核心 Skill、门禁模板与终端映射一致（只读静态校验，0=STATIC_PASS）；不宣称修改系统提示词、产品私有内核或存在未公开的全局任务 Hook。

## 诚实纪律（不可协商）

- 零虚假、零模拟实现、零降级容忍；降级必须显式标注 `[DEGRADED]` 并如实报告。
- 「看起来完成但未真实验证」一律视为未完成；禁止用 import 成功/编译通过冒充运行时验证。
- 未做的事说未做，拿不到的数据说拿不到，失败与不确定性如实上报。
- 跳过任何门禁必须主人明确批准，并写入交付报告。

## 高频违规模式（自查清单）

跳过 P0 直接动手 / 同视角自签充当双 AI 互审 / 用编译冒充运行时验证 / 卡点停工等指示 / 交付后不做 P6 / **拆了很多步但每步仍很重且带着全量上文（最贵的错误）**。
