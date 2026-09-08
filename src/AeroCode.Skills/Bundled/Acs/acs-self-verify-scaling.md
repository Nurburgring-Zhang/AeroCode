---
name: self-verify-scaling
description: 自验证扩展（LLM-as-a-Verifier 降维实现）。当任务为 T3 高风险/不可逆/架构级、需要多方案竞标选优、需要给代码或文档打分排序、需要双 AI 对抗互审、需要交付前双盲审查、需要判断「是否在朝正确方向推进」时必用。提供分级 N 候选生成、1-20 细粒度评分（对抗平局）、评价标准拆分、重复评估、pivot 近似排序把 O(N²) 降到 O(Nk)、验证分数做进度追踪与方向偏离检测、Builder/Verifier 强制分离、交付门双盲审查与分歧仲裁。核心纪律：验证者独立于执行者，绝不采信自报完成。
---

# Self-Verify Scaling：把「自我检查」做成可扩展的排序机制

来源事实：同一模型生成 5 条候选轨迹后由自身验证排序，Terminal-Bench 2.1 成功率 **79% → 88%**，总成本仍比对照方案低约 **11 倍**。关键不在「多生成」，而在**验证能不能把好的挑出来**。

`[降维实现]` 原论文的评分机制依赖模型 logit 概率分布，agent 终端拿不到 logits。本技能降维为「显式 1-20 分 rubric + 评价标准拆分 + 重复评估」。**不宣称复现论文精度**，只保证机制方向一致且可执行、可留证。
降维的补偿见第 6.5 节：拿不到 logits 时，**重复评分的分歧度是唯一可得的置信度代理**，小分差必须重采样再定案。

## 1. 分级触发（不许无脑开 N 候选，否则自己就是成本灾难）

| 级 | 候选 N | 重复评估 R | 做法 |
| --- | --- | --- | --- |
| T0 | 0 | 0 | 不启用 |
| T1 | 1 | 1 | 单方案 + rubric 自检清单（不打分排序） |
| T2 | 1 | 2 | 单方案 + **独立 Verifier** 打分，低于门槛线返工 |
| T3 | 3~5 | 3 | 多候选竞标 + pivot 排序 + 独立 Verifier 签字 |

**成本闸门**：候选生成必须是**方案级/思路级的轻量描述**（≤300 字每个），不是把整个实现做 N 遍。只有排名第一的候选才进入完整实现。违反此条，自验证的收益会被生成成本吃光。

## 2. 评分粒度：强制 1-20 分

原因：离散粗档评分在实测中产生高达 **27% 的平局**，平局意味着排序失效；提高到 1-20 粒度后，100 次比较中 77 次选对且**零平局**。

- 禁止「好/中/差」「A/B/C」「通过/不通过」作为候选比较依据。
- 每个分数必须附**一句证据**（具体缺陷或具体优点），无证据的分数无效。
- 出现平局 → 必须继续细分（追加一条区分性标准），不许抛硬币、不许「都不错」。

## 3. 三维协同扩展（缺一维即为降级执行）

来源事实：评分粒度、重复评估、评价标准拆分三者应**协同扩展**（scale in tandem）。

1. **粒度**：1-20 分（见上）。
2. **标准拆分**：默认 5 项，各自独立打分再加权，禁止一个笼统总分。默认五项见 [reference-rubric.md](reference-rubric.md)。
3. **重复评估**：同一候选按 R 次独立评分取中位数（不取均值，抗离群）；R 次中若极差 > 5 分，说明标准描述不清 → 先修 rubric 再重评。

## 4. pivot 近似排序（O(N²) → O(Nk)）

全量两两比较是 O(N²)，禁止使用。做法：

```
1. 随机选 k=2 个候选作为 pivot（k 固定为 2）
2. 其余每个候选只与这 2 个 pivot 比较打分  → 比较次数 ≈ N×k
3. 按相对分数得到近似排名
4. 仅对 Top-2 做一次直接对比定冠军
```

比较次数从 N(N-1)/2 降到 ≈ 2N+1。N=5 时由 10 次降到 11 次以内且结论更稳（因为有统一基准）。

## 5. Builder / Verifier 强制分离

- `owner != verifier`，**硬约束**，由 `gate_state_validate.py` 校验。
- Verifier **只看产出与 acceptance，不看 Builder 的推理过程**（避免被说服）。
- Verifier 的第一动作是**跑确定性验证**（测试/类型检查/schema/退出码），只有确定性手段覆盖不到的部分才用主观评分。
- `claimedDone` 永不作为准出依据。Builder 说完成 = 待验证。
- 双 AI 对抗审核必须是**不同视角**：Builder 视角求「能用」，Verifier 视角求「怎么坏」。同一视角签两次名 = 造假。

## 5.5 双盲审查（v1.1，T3 交付门终审）

Anthropic 图工程第 6 步的降维落地：**两名互相看不见对方推理的独立审查者**，在交付门（G5）做最终放行判定。与 G3 互审的分工：G3 管过程（发现→修复→复验闭环），双盲管终审（approve / reject + 分歧仲裁）。

写入 `gates.G5.blind_reviews`，由 `gate_checklist.py` 机检（阈值见 `spec/thresholds.json` 的 `double_blind` 节，人数下限默认 2）：

```json
"blind_reviews": [
  {"reviewer": "reviewer-D", "verdict": "approve", "findings": ["……"]},
  {"reviewer": "reviewer-E", "verdict": "approve", "findings": []}
]
```

四条硬规矩：

1. **独立**：审查者名单从 `steps[].builder` 收集，**审查者不得是任何建造者，也不得重复出现**——同一个人审两轮不是双盲，建造者自审是自评的另一种马甲。
2. **必须表态**：`verdict ∈ {approve, reject}`，不许「原则上同意」。
3. **沉默不是结论**：`findings` 字段必须存在，无发现就写空数组——缺字段 = 无法核查 = 视同未做。
4. **分歧必须仲裁**：两名审查者 verdict 冲突时，必须写 `arbitration{resolution, reason}` 留痕。分歧不是噪音是信号——它说明验收标准有歧义，不仲裁，规则手册的歧义永远不暴露。

## 6. 方向偏离检测（验证分数当仪表盘）

来源事实：验证分数与任务实际进展正相关，可用于实时判断是否在朝正确方向推进。

- 每轮记录一次总分到 `progress_trend` 数组。
- **连续 2 次不升**（含持平）→ 视为 `evidence_delta = 0`，触发 `loop-engineering` 空转闸 → 停止并换路。
- 分数**下降** → 立刻回滚到上一个高分状态，不许「继续改改看」。
- 趋势数据写入 `verify-record.json`，由 `gate_verify_rank.py` 校验单调性。

## 6.5 自一致性重采样（智能触发，不无脑全量采样）

拿不到 logits，就只能靠「同一份 rubric 重复评分的分歧度」当置信度代理。平局已经被第 2 节拦住，这里管的是**没平局但差得比噪声还小**那一段。

触发条件（均取自 `spec/thresholds.json` 的 `self_consistency`，不得在正文重写一份）：

| 条件 | 处置 |
| --- | --- |
| 分级为 **T3** 且冠亚加权分差 **< 1.5** | **必须**重采样后再定案，否则 BLOCK |
| 分差 ≥ 1.5 | 不需重采样；仍触发只得一条告警（你在主动多花 token） |
| T0/T1/T2 | 不强制 —— 分级本身就是省 token 的关键 |

重采样的做法与留证：

1. 只对**冠亚两个候选**的 `resampled` 标准加采样（选权重最高、分歧最大的那几项），不是全部重跑。
2. `extra_samples` 取 2~4；**少了不够去噪，多了只是烧 token**。
3. `values` 长度必须真的变成 `repeats + extra_samples` —— **只写 `triggered: true` 而样本数没变是口号，一律 BLOCK**。
4. `gap` 必须与按 `weighted` 重算的冠亚分差一致（误差 ≤0.01），**禁止手改**。
5. 重采样后若排名变了，**以重采样后的为准**，并在 `reason` 里写清改判了什么。

```json
"self_consistency": {
  "triggered": true, "gap": 1.30, "extra_samples": 2, "resampled": ["correctness"],
  "reason": "冠亚仅差 1.30，correctness 权重最高且是唯一分歧点，先加采样再定案"
}
```

## 7. 证据文件（必须落盘，不许只在对话里说）

`verify-record.json` 最小字段：

```json
{
  "task_id": "t-001",
  "tier": "T3",
  "criteria": ["correctness", "completeness", "risk", "cost", "maintainability"],
  "weights": [0.35, 0.25, 0.20, 0.10, 0.10],
  "repeats": 3,
  "candidates": [
    {"id": "c1", "summary": "≤300字方案描述",
     "scores": [{"criterion": "correctness", "values": [17, 16, 17], "median": 17, "evidence": "覆盖边界 X，但未处理 Y"}],
     "weighted": 16.4}
  ],
  "pivots": ["c2", "c4"],
  "ranking": ["c1", "c3", "c2"],
  "winner": "c1",
  "winner_reason": "correctness 与 risk 双项领先，cost 仅低 1 分",
  "verifier": "verifier-B",
  "builder": "builder-A",
  "progress_trend": [12.1, 14.8, 16.4]
}
```

缺 `evidence` 的分数、`builder == verifier`、`ranking` 与分数矛盾、`progress_trend` 连续两次不升、T3 小分差未重采样 → `gate_verify_rank.py` 一律 BLOCK。

## 8. 门槛线

| 级 | 加权总分门槛（满分 20） | 未达标动作 |
| --- | --- | --- |
| T1 | ≥14 | 修到达标 |
| T2 | ≥16 | 返工，最多 2 轮后升级（two-strike） |
| T3 | ≥17 且 correctness ≥17 且 risk ≥16 | 换方案或上报，禁止放宽门槛 |

**禁止事后调低门槛或调整权重来让结果达标**，这属于造假。权重必须在评分**之前**写入 `verify-record.json`。

## 9. 硬门禁

```
python -X utf8 <SUITE>/scripts/gate_verify_rank.py --record .acs/verify-record.json --tier T3
```

退出码 0=PASS，1=BLOCK，2=USAGE_ERROR（视为未验证=未完成）。
