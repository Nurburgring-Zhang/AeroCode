# AeroAgent PHASE 6 交付报告 — 专家集群 · MOA 网关集成 · 经验学习与 RSI

日期：2026-08-21 · 仓库：`D:\WORKSPACE\AeroCoding\Projects\AeroCode`（net9.0）
前置：PHASE 5（autonomy kernel：任务分析/策略选择/澄清门/钢人/任务状态机/工程循环/真实网络研究）
流程：P6-T1/T2/T3 三路实现 → R1/R2 双 AI 对抗审查 → 全部 P1 + 全部 P2 修复收口 → 全量测试多轮绿。

---

## 1. 交付物总览

### T1 专家集群（src/AeroAgent.Autonomy/Cluster/）

- **ExpertPool**：持久化专家注册表。每个专家一份 JSON 档案（`{AutonomyRoot}/cluster/experts/{id}.json`），构造时真实从盘上水合；memory 追加即原子落盘（tmp+move）。记忆条目设上限（默认 1000，超限修剪最旧并 [DEGRADED] 留痕），防止跨任务线性膨胀。
- **ClusterScheduler**：分支并行调度。Primary 指派人 / Swarm 会战（卡死节点由空余专家增援）/ FanOut 多候选竞速三态；依赖图按 `Completion.Task` 真实等待；单节点超时不阻塞其余分支（遗弃执行任务挂 ObserveOrphan，无悬空 Task）；lease 在 finally 归还；运行记录 JSON 真实落盘。
- **ExpertExecutors**：两条生产执行路径——`AgentExpertExecutor`（每次 attempt 经 HarnessHost 拉真实子 agent：provider 循环 + role + 技能目录注入 + 记忆快照注入）与 `FacadeExpertExecutor`（经 Conversation 编排门面真实会话 + MOA 策略事件流聚合）。二者均自收敛异常为失败结果，不向调度器泄漏。

### T2 MOA 网关集成（src/AeroAgent.Moa/Gateway/ + scripts/gateway/）

- **MoaGatewayClient**：与 moa-gateway-pro v3.1.1 逐字段对齐的 HTTP 客户端（execute/login/presets/health；mock 双通道判定：X-MOA-Mock 头 + body.mock）。
- **GatewaySidecar**：uvicorn 子进程生命周期管理。状态机 Starting/Running/Degraded/Failed/Stopped；watchdog 自动重启（连续重启计数在成功恢复后清零）；探活成功把 Degraded 恢复为 Running；从 Degraded 重启先清场旧进程（杜绝孤儿）；密钥仅经环境变量注入子进程，日志不回显。
- **GatewayOrchestrationFacade**：网关可用走真实 MOA 编排；不可用如实降级回本地 MOA 策略并打 [DEGRADED] 标注（UI 徽标数据面已就绪，展示面属 P8-1）。
- **setup_gateway.ps1/.cmd**：一键 install/start/stop/status/info。路径全部可移植（env 变量 + PATH/目录自动发现）；密码用加密 RNG 生成（受限语言模式如实回退并留痕），密码文件 ACL 收紧为当前用户只读；`.gitignore` 覆盖 `.venv/`、`data/`、`.admin_password`、`gateway.pid`。

### T3 经验学习与 RSI（src/AeroAgent.Autonomy/Learning/）

- **ExperienceStore**：写/应用分离（Pending 语义，下次会话生效）；SQLite 真实落库；SemaphoreSlim 串行化（SQLite 单写者模型）。
- **学习钩子**：复盘缺口 → 经验注入提示的真实桥接（非静默丢弃）。
- **SkillCurator**：技能使用统计与沉淀建议。
- **RsiEngine**：L1 输出修正（复盘缺口 → 修正规则真实落库，按任务幂等）；L2 记忆积累（未沉淀规则 → methods 经验，Pending 生效）；L3 参数自调优（变异候选 → held-out 真实评估 → 过 gate 才应用 + 旧参数快照可回退，不过 gate 一律回退并逐候选留痕 rsi-log.md）；创造档须经 ISkillApproval 批准，默认 DenyAll。全部 DB 操作经 `_gate` 串行化。

---

## 2. 测试

全量套件（Release）：**914 总 / 895 过 / 0 失败 / 19 跳过**（跳过全部为网络/真实 LLM/真实网关门控用例），**连续多轮全绿**。PHASE 5 基线 748 → 本阶段净增 166 个测试条目：

| 簇 | 文件 | 条目数 |
|---|---|---|
| 集群 | ClusterSchedulerTests / ClusterPlanTests / ClusterExpertPoolTests / ClusterExpertExecutorTests / ClusterAgentExpertExecutorTests | 70 |
| 网关 | GatewayClientTests / GatewaySidecarTests / GatewayOrchestrationFacadeTests / GatewayRealE2ETests（门控） | 43 |
| 学习 | LearningExperienceStoreTests / LearningRsiTests / LearningSkillCuratorTests / LearningHookTests / LearningBridgeAndPromptTests | 53 |

真实网关 E2E（health/login/execute/401/presets 原始 HTTP 证据）存于 `p6/logs-T2b-gateway-e2e.txt`；门控用例在无网关环境下如实跳过，不伪造通过。

---

## 3. 双 AI 对抗审查（R2）收口

R2 结论 PASS_WITH_FINDINGS：2 P1 + 8 P2，**全部修复并回归**：

| 编号 | 问题 | 修复 |
|---|---|---|
| F-P1-1 | GatewaySidecar 从 Degraded 重启孤儿化旧进程 | StartAsync 拉起前清场旧 watchdog + Kill 存活旧进程；新增 `StartAsync_FromDegraded_KillsOldProcess_BeforeRelaunch` 测试 |
| F-T1 | Sidecar 测试轮询次数断言负载敏感 flaky（威胁轮轮绿门禁） | 断言放宽为 `>= 1` 并注释原因 |
| F-P2-1 | RsiEngine `_gate` 声明未用，DB 未串行化 | `WithGateAsync` 包裹 RecordCorrections/PromoteCorrections |
| F-P2-2 | ProbeAsync 成功不恢复 Degraded→Running | 探活成功即恢复并留痕；新增恢复测试 |
| F-P2-3 | ExpertPool 记忆无上限 | 上限（默认 1000）+ 追加/加载双侧修剪 + [DEGRADED] 日志 |
| F-P2-4 | 部署脚本硬编码构建机私有路径 | 默认值改 env 变量 + PATH/目录自动发现，找不到时明确报错 |
| F-P2-5 | Get-Random 生成密码 + 明文落盘 + 未 gitignore | 加密 RNG（CLM 如实回退）；密码文件 ACL 收紧（失败 [DEGRADED] 不阻断）；.gitignore 补齐四项 |
| F-P2-6 | watchdog 连续重启计数从不随恢复清零 | 成功恢复后 `consecutiveRestarts = 0` |
| F-P2-7 | AgentExpertExecutor 无直接执行测试 | 新增 6 例：真实 HarnessHost + 脚本化 provider，覆盖成功/空输出/取消/异常收敛/上下文隔离/记忆注入 |
| F-P2-8 | 轻微契约/措辞 + 死状态 | 删除 ClusterScheduler `_dependencyCompletions` 死状态；其余措辞项 T2 已自报并注释在案 |

### 收口轮自查新发现（非 R2 清单，一并修复）

- **Agent.RunAsync 取消语义被吞**：OCE catch 返回 `Cancelled=false`，AgentExpertExecutor 会把 "(cancelled)" 当成功产出。修复为 `Cancelled = true`（DualAiArena 只读 Text，不受影响）。
- **ClusterSchedulerTests.Gate1 墙钟断言同族 flaky**：以执行轨迹的确定性证据替代——串行执行时相邻 attempt 启动间隔必然 ≥ 最短分支时长 150ms，断言启动间隔而非总耗时。

---

## 4. 已知限制（如实）

- 网关 E2E 用例门控：无本地网关/凭据时跳过（`AERO_MOC_E2E=1` + 网关就绪才跑），不在 CI 里伪造。
- 网关 UI 徽标/X-MOA-Mock 展示面属 P8-1（数据面 StateChanged 事件与 [Mock] 标签已就绪）。
- 部署脚本的密码文件 ACL 收紧与加密 RNG 在 Windows 受限语言模式下会如实降级（[DEGRADED] 留痕，功能不断）。
- experience-log.md / rsi-log.md 为追加式无轮转（读取侧均有 Take(max) 限流，功能不受影响；R2 记录在案）。

## 5. 验收门禁对照

| 门禁 | 结论 |
|---|---|
| P6-T1：Autonomy.Cluster + ≥25 单测；3 专家并行 E2E；卡死→其余继续+会战 | 满足（70 条目；Gate1/2/3 独立可跑） |
| P6-T2：Moa.Gateway + 部署脚本 + ≥20 单测；真实 HTTP；断网回退标注 | 满足（43 条目；真实 HTTP 证据在案；回退 [DEGRADED] 标注被测试断言） |
| P6-T3：经验学习写/应用分离 + RSI 三层 + 安全档 | 满足（53 条目；L3 held-out 评估不过 gate 必回退被断言；创造档默认拒绝被断言） |
| 全量测试轮轮绿 | 满足（收口后连续多轮 0 失败） |
