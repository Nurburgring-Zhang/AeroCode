# ANALYSIS 08 — Avernet (inclusionAI) 深度拆解

> **来源**: [inclusionAI/Avernet](https://github.com/inclusionAI/Avernet)  
> **本地路径**: `D:/minimax/代码/AeroCodeV3_externals/Avernet` (7485 files, 93.2 MB)  
> **许可**: Apache 2.0  
> **生产状态**: **蚂蚁集团 12 个业务群部署**, 90%+ 任务完成率 (2026-07)  
> **官网**: avernet (Ant Group / inclusionAI)  

---

## 1. 定位与差异化

**Avernet** = **"Open-source infrastructure layer for building and operating persistent, coordinated, multi-agent systems at organizational scale"**

**中文**: "开源基础设施层, 用于构建和运营组织级的、持久化、协同的多 Agent 系统。"

**口号**: "Where agents live, connect, coordinate, execute, and evolve together."

**为什么这是 8 个项目里最特别的一个**:
- **唯一有真实生产验证的** (蚂蚁 12 个 BG, 90%+ 完成率)
- **唯一面向"组织级"** (其他都是个人/团队)
- **唯一明确"多 agent 协同 + 持久化"** (其他都是单 agent + session)
- **唯一有完整 OpenClaw 兼容层** (迁移友好)

**与其他项目的差异**:
- **Hermes**: 单 agent + 学习闭环
- **OpenCode**: 单 agent IDE
- **Reasonix**: 单 agent + DeepSeek
- **DSH**: 单 agent + Cordis 插件
- **Avernet**: **多 agent 协同 + 组织级 + 持久化**

---

## 2. 核心架构

### 2.1 能力矩阵 (Capabilities & Status)

来自 README 官方能力表:

**Trusted core** (信任核心):
- ✅ Identity - 可用
- ✅ Auth - 可用
- ⚠️ Permissions - 部分公开
- ⏳ Security - 计划中
- 🚧 Audit - 进行中
- 🚧 Lifecycle - 进行中

**Execution infrastructure** (执行基础设施):
- ✅ Heterogeneous runtimes - 可用
- ✅ Bot services - 可用
- ⚠️ Containers - 部分公开
- ⏳ Clusters - 计划中

### 2.2 源代码结构

```
Avernet/
├── src/
│   ├── baas/                # BaaS (Bot-as-a-Service) 后端
│   ├── backend/             # 后端服务
│   ├── bcs/                 # Bot Coordination Service
│   ├── bcsfuse/             # Bot Coordination Service Fuse (文件)
│   ├── engine/              # 核心引擎
│   ├── frontend/            # 前端
│   └── gateway/             # 网关
├── scripts/
│   ├── 4bots_merchant_operations_profile/    # 4 商家 Bot 配置
│   ├── 5bots_profile/                        # 5 角色 Bot 配置 (CEO/CS/Eng/PM/Verif)
│   ├── 6bots_world_cup_creator_profile/      # 6 Bot 配置 (世界杯内容创作)
│   ├── 8bots_micro_merchant_profile/         # 8 小店 Bot (Bakery/Coffee/...)
│   ├── ci/                                   # CI 脚本
│   ├── compat/                               # 兼容层
│   │   └── openclaw/                         # OpenClaw 兼容
│   ├── lib/                                  # 共享库
│   ├── modules/                              # 模块
│   └── rule_bots_profile/                    # 规则 Bot 配置
├── docs/
│   ├── arch/                                 # 架构文档
│   ├── superpowers/                          # 超级能力
│   │   ├── plans/
│   │   └── specs/
│   ├── bot-integration.md                    # Bot 集成指南
│   ├── bot-provider-integration.md           # Bot Provider 集成
│   ├── dependencies.md
│   ├── docker.md
│   ├── openclaw-bcn-local.md                 # OpenClaw 本地兼容
│   ├── quick-start.md
│   ├── skills-pool-p3-rollout-runbook.md     # Skill Pool P3 上线
│   ├── small-shop-dream-team-live-demo-tutorial.md  # 小店梦之队直播教程
│   └── waic-live-demo-tutorial.md            # WAIC 直播教程
├── docker/
├── spec/
├── .env.example
├── AGENTS.md
├── CLAUDE.md
├── docker-compose.yml
├── Dockerfile.ocb
├── pyproject.toml
└── uv.lock
```

### 2.3 多 Bot Profile 系统 (核心创新)

**4 种 Bot Profile 模板**:

1. **4 bots_merchant_operations_profile** (商家运营)
   - merchant-operations (商家运营)
   - platform-data (平台数据)
   - platform-marketing (平台营销)
   - platform-supply-chain (平台供应链)

2. **5 bots_profile** (5 角色)
   - ceo (CEO)
   - customer-service (客服)
   - engineering (工程)
   - product-manager (产品经理)
   - verification (验证)

3. **6 bots_world_cup_creator_profile** (世界杯内容创作)
   - content-editor
   - growth-operator
   - match-data-researcher
   - operations-director
   - short-video-director
   - tactical-analyst

4. **8 bots_micro_merchant_profile** (8 小店)
   - bakery-shop (烘焙店)
   - coffee-shop (咖啡店)
   - convenience-store (便利店)
   - delivery-runner (配送员)
   - event-planner (活动策划)
   - event-rental (活动租赁)
   - flower-shop (花店)
   - onsite-decorator (现场布置)

**核心机制**: 每个 Profile = 一组 Bot + 协同规则 + 任务模板 + 工作流。

**这是 AeroCode V3.0 "Multi-Bot Profile" 系统的直接参考**。

### 2.4 Bot-as-a-Service (BaaS)

`src/baas/` - 完整的 Bot 服务化:
- Bot 注册
- Bot 发现
- Bot 调用 (RPC / 事件)
- Bot 监控
- Bot 升级

**移植到 AeroCode V3.0**:
- 简化版: Skill 即 Bot
- Skill Registry = Bot Registry
- Skill 调用 = Bot 调用

### 2.5 OpenClaw 兼容层 (`scripts/compat/openclaw/`)

`openclaw-bcn-local.md` 文档说明如何本地运行 OpenClaw 兼容模式。

**为什么这重要**:
- OpenClaw 是市场上已有的 agent 平台
- Avernet 提供**迁移路径** —— 用户可从 OpenClaw 平滑迁移
- 类似 Hermes 的 `hermes claw migrate`

**移植到 AeroCode V3.0**:
- 暂不做 (P2)
- 未来: 如果其他 agent 平台流行, 提供兼容层

### 2.6 Skills Pool P3 Rollout (Skill 池化)

`docs/skills-pool-p3-rollout-runbook.md` - Skill 池化 P3 上线 runbook。

**核心思想**:
- Skill 不再是单 bot 私有
- 多个 bot 共享一个 Skill Pool
- Skill 版本化管理
- 灰度发布 (P3 阶段)

**移植到 AeroCode V3.0**:
- 我们的 Skill Hub 概念
- 全局 Skill (vs 项目级 Skill)

### 2.7 Superpowers 系统 (`docs/superpowers/`)

`docs/superpowers/plans/` 和 `docs/superpowers/specs/`

**含义**: 给 Agent 的"超级能力" —— 这是 Avernet 的差异化能力
- 计划 (plans)
- 规格 (specs)
- 可能是 "任务规划 + 长期记忆" 的高级能力

**移植到 AeroCode V3.0**:
- 简化: 与 DSH Goal 系统 + Hermes 记忆 整合
- 不直接移植, 但概念借鉴

### 2.8 部署与运行

`docker-compose.yml` - Docker Compose 部署
`Dockerfile.ocb` - OCB (OpenClaw Bridge) Docker
`pyproject.toml` + `uv.lock` - Python 项目 (用 uv 管理)
`.python-version` - Python 版本指定

**移植到 AeroCode V3.0**:
- 我们是 C# / Avalonia, 不直接移植
- 但 Docker 部署模式可借鉴

---

## 3. 核心能力映射 (AeroCode V3.0 移植)

### 3.1 Multi-Bot Profile → `AeroCode.Harness/Profiles/` (P1)

**移植**:
- [ ] Profile 实体 (Name, BotList, CoordinationRules, TaskTemplate)
- [ ] ProfileService (Load, Save, Activate, Deactivate)
- [ ] 几个默认 Profile (4-bot / 5-bot / 6-bot / 8-bot 模式, 简化版)
- [ ] Profile UI (可视化配置 bot 列表)

### 3.2 BaaS (Bot-as-a-Service) → 整合到 `AeroCode.Skills/` (P1)

**移植**:
- [ ] Skill = Bot 概念统一
- [ ] Skill Registry (发现 / 注册 / 调用)
- [ ] Skill Versioning (类似 P3 Rollout)

### 3.3 协调机制 (Coordination) → `AeroCode.Harness/Coordination.cs` (P2)

**移植**:
- [ ] 简化版: 多 Skill 协作 (一个 Skill 调用另一个)
- [ ] 不做完整 BaaS
- P2 (短期不需要, 长期可加)

### 3.4 Bot Integration Guide → 文档 (P0)

**移植**:
- [ ] 写一份 `BOT_INTEGRATION.md` 给开发者
- [ ] 写一份 `PROFILE_GUIDE.md` 给最终用户
- [ ] 类似 Avernet 的 bot-integration.md

### 3.5 Skills Pool → `AeroCode.Skills/HUB.md` (P1)

**移植**:
- [ ] 全局 Skill Hub (vs 项目级)
- [ ] 全局版本管理
- [ ] 灰度发布 (P2)

---

## 4. Avernet vs 其他 7 个项目对比

| 维度 | Avernet | Hermes | OpenCode | Reasonix | DSH | MattP | Google | CodeFlow |
|---|---|---|---|---|---|---|---|---|
| **规模** | 组织级 | 单 agent | 单 agent IDE | 单 agent | 单 agent 框架 | skill 集 | 方法论 | 单 agent |
| **多 agent** | **✅ 核心** | ⚠️ (delegate) | ⚠️ (OmO) | ❌ | ⚠️ (subagent) | ❌ | ❌ | ❌ |
| **持久化** | **✅ 核心** | ✅ 4 层 | ✅ SQLite | ❌ | ✅ Storage | ❌ | ❌ | ❌ |
| **生产验证** | **蚂蚁 12 BG** | 个人 | 个人 | 个人 | 内部 preview | 个人 | Google 内部 | 个人 |
| **协议** | Apache 2.0 | MIT | MIT | MIT | MIT | MIT | CC-By 3.0 | MIT |
| **平台 gateway** | Bot services | 7 平台 | Slack | ❌ | 通用 | ❌ | ❌ | ❌ |
| **Bot Profile** | **✅ 4 模板** | ❌ | ❌ | ❌ | Preset | ❌ | ❌ | ❌ |
| **OpenClaw 兼容** | ✅ | ✅ (migrate) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

**互补关系**:
- Avernet 提供 **"组织级 + 多 Bot Profile + Bot 协调 + 持久化 + OpenClaw 兼容"**
- Hermes 提供 **"单 agent 学习闭环 + 4 层记忆"**
- 两者合起来: 单 agent 智能 + 多 agent 协同 = 完整方案

**对 AeroCode V3.0**:
- 单 agent 智能: 学习 Hermes + Reasonix
- 多 agent 协同: 借鉴 Avernet Profile 模式 (简化)
- 不做完整 Avernet 复刻 (规模太大, V4+ 再说)

---

## 5. 给 V3.0 实施的具体建议

**Stage 7: Profile 系统 (Avernet 模式, 简化版)**:

**Step 1**: 单一 Agent 多 Skill 协作
- Skill A 调用 Skill B
- Skill 之间数据传递

**Step 2**: Profile 实体
- Profile 是一组 Skill + System Prompt + Model 路由
- 用户可定义多个 Profile
- 一键切换

**Step 3**: 几个默认 Profile
- "笔记管理 Profile" (核心: Summarizer, AutoTagger, SemanticSearcher)
- "代码审查 Profile" (核心: CodeReview, ComplexityChecker, NamingChecker)
- "Bug 诊断 Profile" (核心: DiagnoseBugs, GrillWithDocs)
- "项目规划 Profile" (核心: ToSpec, ToTickets, DomainModeling)

**Step 4**: UI 集成
- Profile 选择器 (类似 DSH Preset)
- 当前 Profile 显示在 AI 助手 Tab 顶部

---

## 6. 一句话总结

> Avernet 是 **"蚂蚁集团 12 业务群验证的组织级多 Agent 基础设施"** —— 4 种 Bot Profile 模板、Bot-as-a-Service、OpenClaw 兼容。我们借鉴其 Profile 模式（简化版），让 AeroCode V3.0 支持"一组 Skill 协同工作"，而非单 Skill 单独作战。
