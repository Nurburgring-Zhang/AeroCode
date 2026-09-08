# G0-G6 质量门禁完整准出条件（harness 层 · Agent Core Suite v1.2.0）

与 P0-P6 一一对应。任一门未过即阻断进入下一阶段；微小任务可压缩单门耗时，但不得跳门。跳门必须主人明确批准并写入交付报告。

标注约定：**[硬]** = 有 `scripts/` 脚本可机检，必须跑并留证据；**[软]** = 靠自觉执行，须在 `task-state.json` 留记录。

原先有 7 条只靠自觉、会漂移，现已搬成 `task-state.json` 里的结构化字段并由 `gate_checklist.py` 机检（Node 侧 `acs_gates.mjs checklist` 结论一致）。**缺字段 = 无法核查 = 视同未做，直接 BLOCK**：

| 机检键 | 字段位置 | 分级要求 |
| --- | --- | --- |
| `G0_steelman` | `gates.G0.steelman` + `gates.G0.key_questions` | T2+ |
| `G1_cognition` | `gates.G1.cognition` | T2+ |
| `G2_plan` | `gates.G2.plan` | T2+ |
| `G3_review_closure` | `gates.G3.reviews` | T2+ |
| `G4_acceptance_actual` | `steps[].acceptance[].actual` | T1+ |
| `G5_report` | `gates.G5.report` | T2+ |
| `G5_blind_reviews` | `gates.G5.blind_reviews` | T3 |
| `G6_rsi` | `gates.G6.rsi` | T3 |

v1.1 新增的 T3 状态契约项（边门、汇聚语义、工件谱系、契约版本、模型分层）不占门号，由 `gate_state_validate.py` 随 G2-G4 的状态文件一并机检，字段形状见 `templates/task-state.schema.json`，阈值见 `spec/thresholds.json` 的同名节（单一真相源，本文不重写）。

字段只承载可核查的骨架（条数、依据、签字人、实测值、闭环链），不规定行文。**深度思考仍由原生能力完成，本层只拦「整段跳过」与「写空话」。** 字段形状见 `templates/task-state.schema.json`，填法见 `templates/task-state.example.json`。

## G0 钢人门

- [硬] 已完成问题重述 + 正向钢人（≥3 条，附依据）+ 反向钢人（≥3 条，附依据与反例）+ 分歧定位（含最可能改变结论的关键变量）→ `gates.G0.steelman{pro[],con[],divergence,key_variable}`，每条须 `{claim, basis}` 成对
- [硬] 关键问题 ≤3 个且已获主人回答 → `gates.G0.key_questions[{q,answer}]`；任务显然明确时可压缩，但必须写 `questions_compressed_reason`
- [硬] `task-state.json` 中 `tier` / `tier_reason` 已填写且定级理由可核（`gate_checklist.py`）
- [软] 已输出明确判断、理由、下一步行动

## G1 认知门

- [软] 背景 / 过程 / 目标 / 外部 / 风险 五类信息深挖均有证据（读过的文件、检索记录、主人确认）
- [硬] 《任务认知摘要》覆盖目标、边界、依赖、影响范围、验收标准、风险清单 → `gates.G1.cognition{goal,boundary,dependencies,impact,acceptance,risks}` 六项齐全且非空话
- [硬] 需求澄清完成、无悬空假设；未确认假设必须显式列出并标注风险 → `gates.G1.cognition.assumptions[{text,risk}]`
- **[硬] 信息缺口必须显式声明**：抓取失败、无权限、无法获取的数据一律记入 `task-state.json.known_gaps`，禁止用推测填充（`gate_checklist.py` 校验字段存在）

## G2 规划门（缺项即阻断开工）

- [硬] 完整详细的项目规划（阶段 / 里程碑 / 依赖顺序 / 并行度）→ `gates.G2.plan.phases`；依赖顺序与并行度由 `graph.edges` / `graph.parallel_groups` 承载
- [硬] 详细设计与标准定义（架构 / 数据 / API 契约；验收标准逐条可检查）→ `gates.G2.plan.design_standards`
- [硬] 每阶段目标明细 → `gates.G2.plan.stage_goals`
- [硬] 边界约束（做什么 / 不做什么 / 边界外需求处置方式）→ `gates.G2.plan.boundary{in_scope,out_scope,out_of_scope_policy}`；无排除项也要写空数组
- [硬] 已明确 harness / loop / graph engineering 的启用方式，并在 `task-state.json.enabled_engineering` 中登记
- [硬] 任务 DAG 已定义：节点含 `inputs/outputs/acceptance/deterministic`，`deterministic=true` 的节点禁止调用模型
- [硬] **T3 图必须至少挂一条边门**：`graph.edges[].gate` 存在且 `kind=cmd` 的门必须写 `expect_exit`（没有期望退出码的命令验证无法判成败）；`barriers[].policy` 四选一（`all_success` / `min_success`+`min_count` / `timeout_degrade` / `critical_fail_stop`），`min_success` 的 `min_count` 必须满足 1 ≤ min_count ≤ 等待分支数 → `gate_state_validate.py`

## G3 执行门（每步每阶段）

- [硬] **真实性扫描零命中**：`gate_reality_scan.py` 对交付物检索 stub / mock / fake / placeholder / TODO / FIXME / 模拟实现 / 占位 / NotImplementedError；生产路径零命中，测试目录内受控替身必须有边界说明
- [硬] **四道成本闸门通过**：`gate_loop_guard.py` 校验窄步闸、回灌禁令、空转闸（two-strike）、思考预算闸
- [硬] **token 计量来源必须交代**：T2+ 写 `cost_metering.source`（拿不到真账就写 `unavailable`，沉默不算声明）；声称可审计就必须给 `tokens_actual` + 可复查 `evidence_ref`；不可审计则禁填 `tokens_actual`（留在 `*_proxy`）→ `gate_loop_guard.py`
- [硬] **有界重试**：同一步重试必须登记 `retries`（≤ `spec.retry.max_retries`），且 `retries > 0` 时必须写 `retry_reason`（为什么重试）与 `delta_from_last`（这次和上次差在哪）——同法重试就是空转的另一种形态 → `gate_loop_guard.py`
- [硬] **T3 调模型节点必须声明档位**：`calls_model=true` 的节点必须写 `model_tier ∈ {cheap, standard, heavy}`（计量字段，让「贵模型干杂活」的浪费可见，不拦你用强模型）→ `gate_state_validate.py`
- [硬] 双 AI 互审记录完整：Builder / Verifier **双身份分离**交叉签字，问题闭环（发现→修复→复验）→ `gates.G3.reviews[{round,builder,verifier,findings,fixed,recheck}]`；`gate_checklist.py` 校验签字人不同、且 `findings` 非空时 `fixed` 逐项对应、`recheck` 非空非空话
- [软] 每阶段诚实审核记录：是否真实实现、有无模板式或低质量实现，结论与证据
- [软] 卡点处理记录：深搜来源 + 尝试过的方案 + 结果证据；禁止静默绕过

## G4 验证门（每步每阶段）

- [硬] 该阶段验收标准逐条核验并附证据（命令输出 / 测试结果 / 数据）→ 每个 `steps[].acceptance[]` 必须回填 `actual`；缺 `actual` 或写空话即 BLOCK（未验证 = 未完成）
- [硬] **验收必须绑定声明的产出物**（T1+）：`acceptance[].value` 必须直接打到该步 `outputs` 声明的文件型产出物上（子串可核验）；只验「没改坏」不验「真的交付了」的命令就是交白卷盲区（SWE Refactor Bench 教训：行为测试全绿但产出物根本没写）→ `gate_state_validate.py`，阈值见 `spec.acceptance_binding`
- [硬] 测试真实运行通过（编译 / 单测 / 集成 / E2E 按标准）；**禁止用 import 冒烟或编译通过冒充运行时验证**
- [硬] 方向校准：与《任务认知摘要》比对无偏离；启用自验证时评分序列不得连续两次不升（`gate_verify_rank.py` 输出 `progress_trend`）
- [硬] **T3 小分差必须自一致性重采样**：冠亚加权分差 < `spec.self_consistency.trigger_score_gap` 时必须先重采样再定案，且 `values` 长度真的变成 `repeats + extra_samples`（只写 `triggered` 不加样本一律 BLOCK）→ `gate_verify_rank.py`
- [软] 出现偏离或未达预期必须修正后重新验证，直到通过

## G5 交付门

- [硬] **双盲审查（T3）**：≥2 名互相独立的审查者（`gates.G5.blind_reviews[{reviewer,verdict,findings}]`），审查者不得是任何建造者、不得重复出现；`verdict ∈ {approve, reject}`；无发现必须写空数组（沉默不是结论）；**verdict 冲突必须仲裁留痕**（`arbitration{resolution,reason}`）——分歧不是噪音是信号，说明验收标准有歧义 → `gate_checklist.py`，阈值见 `spec.double_blind`
- [硬] **工件登记（T3）**：交付物必须登记为带溯源的工件（`artifacts[{id,produced_by,supersedes}]`）：`produced_by` 必须指向真实 graph 节点，`supersedes` 版本谱系不得断链（指向不存在的工件）或成环。transcript 不是数据库，不能溯源到节点、查不到前代版本的产出不叫交付物 → `gate_state_validate.py`，阈值见 `spec.artifacts`
- [软] 完成复盘：自我对抗式逐步核查，缺口清单全部闭环
- [软] 多轮双 AI 对抗审核（≥2 轮）：视角一（功能 / 逻辑 / 边界）× 视角二（安全 / 异常 / 性能 / 兼容）交叉签字，HIGH 级问题逐项处置（修复或豁免理由）
- [硬] 多轮上线测试（≥2 轮）：真实启动链路（迁移 → 启动 → 核心接口 → 前端构建 / 真实渲染）+ 健康检查 + 异常路径 + 回归；每轮留命令输出
- [硬] 交付物写入后必须**用真实文件系统读回校验**（防写入未落盘故障态），比对行数与关键串
- [硬] 交付报告四要素齐全：成果清单 / 验证证据 / 已知风险 / 后续建议 → `gates.G5.report{deliverables,evidence,risks,next_steps}`
- [软] 最终产物完整达到设计要求与标准，无半成品、无「后续补充」

## G6 进化门

- [软] 经验已沉淀到长期记忆 / 技能 / 守则体系（可检索、可复用、可追溯）
- [硬] RSI 闭环记录完整：问题 → 根因 → 改进动作 → 验证结果 → 沉淀位置 → `gates.G6.rsi{problem,root_cause,action,verification,stored_at}`（T3 必检）
- [软] 发现守则、门禁、流程自身缺陷时已提出并落实改进（改进方法本身也能被改进）

## 诚实纪律（全程生效）

- 所有过程诚实回答、说真话：进度、证据、失败、不确定性如实报告
- 零虚假容忍、零模拟实现容忍、零降级容忍；降级必须显式标注 `[DEGRADED]` 并报告
- 「看起来完成但未真实验证」的产出一律视为未完成
- 能力边界必须诚实：拿不到的数据（如计费 token、模型 logits）说拿不到，用代理指标时字段名必须显式标注
- 跳过任何门禁必须主人明确批准并在交付报告中记录
