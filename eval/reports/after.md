# AeroCode Eval · 三指标基线报告（fixture v0）

- 生成时间 (UTC): 2026-09-05T07:23:55Z
- 运行模式: **dry-run** — 未检测到 AEROCODE_EVAL_GATEWAY_KEY（网关凭据只从 AEROCODE_EVAL_* 环境变量读取），本次为管线 dry-run，所有指标值以 PENDING 占位，报告结构完整。
- 输出策略: fail-closed（只允许写 eval/reports/ 或显式授权目录）；本次报告路径: D:\WORKSPACE\AeroCoding\Projects\AeroCode\eval\reports\after.md
- 网关凭据来源: 环境变量 AEROCODE_EVAL_GATEWAY_KEY（只读入内存，不落盘、不进报告）

## 数据集清单

| dataset_id | metric（口径标识） | version | 任务数 |
|---|---|---|---|
| checkpoint_pass_rate_v0 | checkpoint_pass_rate | v0 | 3 |
| multi_turn_hallucination_v0 | multi_turn_hallucination_rate | v0 | 3 |
| unit_cost_completion_v0 | unit_cost_completion | v0 | 2 |

## 1. 多轮幻觉率 (multi_turn_hallucination_rate)

- 口径: 多轮交互口径（非单轮 QA）：每条 fixture 为一段多轮对话序列，对话内证据只出现在前序轮次；对模型最终轮答案判幻觉。指标值 = 判定幻觉的任务数 / 总任务数。
- 判据: 多轮交互口径（非单轮 QA）：每条任务是一段多轮对话序列，证据只出现在前序轮次；对模型最终轮答案判幻觉。判据=最终轮答案与对话内证据一致：命中任一 forbidden_claims（对话内不存在/相悖的断言）判 HALLUCINATED；required_grounding 为对话内证据锚点词，缺失仅记 ungrounded 备注。指标值 = 判定幻觉任务数 / 总任务数。v0 判题为确定性关键词启发式。
- 值: **PENDING** — dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。

| id | 判据 | 状态 | 结果/说明 |
|---|---|---|---|
| mt-001 | 最终答案必须只引用前序轮次出现过的端口 8910；断言 8080/8911/9090 等其他端口即幻觉。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |
| mt-002 | 最终答案必须引用前序轮次给出的基线数字 1445 passed / 23 skipped；断言其他通过数（如 1500/1396）即幻觉。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |
| mt-003 | 总结必须包含对话内证据：目标框架 .NET 9 与禁止新增 NuGet 包；不得出现 .NET 8/Python/Node.js 等对话外技术栈断言。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |

## 2. 困难题 checkpoint 通过率 (checkpoint_pass_rate)

- 口径: CritPt 式口径：困难任务分解为有序 checkpoint，模型一次作答后逐项核验；指标值 = 通过的 checkpoint 数 / checkpoint 总数（跨数据集聚合）。
- 判据: CritPt 式口径：困难任务分解为有序 checkpoint，模型一次作答后逐项核验；每个 checkpoint 的 detect_any 关键词在最终答案中命中任一即该项通过。指标值 = 通过的 checkpoint 数 / checkpoint 总数（跨数据集聚合）。v0 判题为确定性关键词启发式。
- 值: **PENDING** — dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。

| id | 判据 | 状态 | 结果/说明 |
|---|---|---|---|
| cp-001 | 四个 checkpoint：完整方法实现 / 分治递归 / 复杂度说明 / 空数组与单元素边界；detect_any 命中任一即通过该项。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |
| cp-002 | 四个 checkpoint：重试触发条件 / 退避延迟计算 / 最大重试次数 / 重试风暴防护；detect_any 命中任一即通过该项。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |
| cp-003 | 四个 checkpoint：User/Task 实体 / 分配关系 / 删除行为 / 唯一性或索引约束；detect_any 命中任一即通过该项。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |

## 3. 单位成本完成率 (unit_cost_completion)

- 口径: 每任务 token 成本 / 完成质量分：质量分 = required_keywords 命中比例（v0 关键词启发式，0..1），成本 = 网关 execute 的 total_cost 与 tokens；指标值 = Σcost / Σquality。
- 判据: 每任务 token 成本 / 完成质量分口径：质量分 = required_keywords 命中比例（v0 确定性关键词启发式，0..1）；成本 = 网关 execute 返回的 total_cost（USD）与 references tokens；指标值 = Σcost / Σquality（质量分为 0 的任务记未完成并单列）。真实成本依赖网关，dry-run 为 PENDING。
- 值: **PENDING** — dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。

| id | 判据 | 状态 | 结果/说明 |
|---|---|---|---|
| cost-001 | 质量分=required_keywords 命中比例；答案应触及「模型」与「聚合」两个核心概念（v0 关键词启发式）。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |
| cost-002 | 质量分=required_keywords 命中比例（0..1）；成本=网关 execute 的 total_cost 与 tokens；指标=Σcost/Σquality。 | PENDING | dry-run：未配置网关（AEROCODE_EVAL_GATEWAY_KEY 缺失），未执行真实采集。 |

## 附录

- 评判器声明: v0 全部为确定性关键词启发式判据（冻结于 eval/datasets/*.json 的 criteria 字段），不含 LLM 评审。
- 安全声明: 本报告与运行日志经 SensitiveScrubber 统一脱敏（sk-* 形态密钥、Bearer 令牌、AEROCODE_EVAL_* / MOA_GATEWAY_KEY 凭据值 → [REDACTED]）；网关凭据只从环境变量读取。
- 采集模式说明: 无网关凭据 → dry-run（PENDING）；网关探活失败 → PENDING + 脱敏原因；探活成功 → 经 /v1/moa/execute 真实采集。
