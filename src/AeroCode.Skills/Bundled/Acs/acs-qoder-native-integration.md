---
name: qoder-native-integration
description: Qoder 原生深度融合。当在 Qoder 终端上执行任务并需要调用 Qoder 原生机制（子代理集群、MCP 惰性加载、双 scope 记忆、多会话编排、定时任务、计划模式）时必用；提供 qoder profile 原生能力路由与五条实证纪律。SKIP：非 Qoder 终端、纯闲聊，不触发。
---

# Qoder Native Integration：Qoder 适配层入口

`[软约束]` 本文是纪律；`[硬约束]` 是 scripts/ 门禁的退出码。两者冲突时以脚本为准。

## 1. 定位：单一真相源

- 本技能是 **Qoder 适配层入口**：把套件各守则路由到 Qoder 原生机制，并把本机判例沉淀的失败模式变成纪律。
- 能力映射的**唯一真相源**是 `spec/adapters.json` 的 `capability_adapters.profiles.qoder`；本文件**不重复定义映射细节**（守 E27：不维护内容相同的重复文件）。
- 下表只是速查抄录；如有出入，以 `spec/adapters.json` 为准。
- 全部映射经 2026-09-05 真实 Qoder 会话实证（E32/E33/E34）；未实证机制只写说明、不登记为能力。

## 2. 原生能力路由表（逐行抄录自 spec/adapters.json qoder profile）

| 能力 | Qoder 原生映射 |
| --- | --- |
| clarify | AskUserQuestion |
| progress | TaskCreate/TaskUpdate/TaskList/TaskGet |
| parallel | Agent(subagent_type: Explore\|Plan\|general-purpose) |
| skill_load | Skill |
| memory | auto-memory（user scope=~/.qoder/memory 跨项目；project scope 仅本项目；写记忆=写记忆文件+更新同目录 MEMORY.md 索引两步） |
| file_delivery | Write/Edit 落盘于工作区并在回复中给出绝对路径 |
| shell | Bash |
| file_ops | Read/Write/Edit/Glob/Grep |
| mcp | mcp_list→mcp_get→mcp_call 三件套（MCP 惰性加载：工具不进工具清单，禁止直连 qualified name） |
| planning | EnterPlanMode/ExitPlanMode |
| scheduling | qoder_cron |
| multi_session | list_chat_sessions/create_chat_session/send_message_to_chat_session/wait_chat_sessions |

落点速查：技能目录 `~/.qoder/skills`；规则落点 `<workspace>/.qoder/rules/agent-core-suite.md`。

## 3. 五条实证纪律（每条注明来源编号）

| # | 纪律 | 内容 | 来源 |
| --- | --- | --- | --- |
| 1 | 子代理生存性 | 审计类子代理必须**前台**派发；后台子代理连续两轮无产出（two-strike）立即转前台，禁止第三次同法 | E28 |
| 2 | 并发防护 | 派发子代理/开新会话前，先查同工作区有无运行中会话（list_chat_sessions），防止双写同一仓库 | E30 |
| 3 | 波次编排 | 多 builder 并行必须文件所有权互斥 + 接口钉死 + 汇聚走 barrier；merge 兜底 | E29 |
| 4 | MCP 三件套 | mcp_list 发现 → mcp_get 取 schema → mcp_call 调用；禁止猜测 qualified name 直调 | E32 |
| 5 | 记忆路由 | 跨项目经验入 user scope（~/.qoder/memory），本项目经验入 project scope；每条记忆=记忆文件+MEMORY.md 索引两步，缺一即漂移 | E33 |

## 4. 诚实边界

- Qoder 运行时（子代理引擎/记忆索引/MCP 服务器/cron 调度器）**物理不可打包**：缺位时走 spec/adapters.json 的 adapters fallback，并在交付报告声明强制力等级（full/partial/soft_only），禁止把软约束说成硬门禁。
- Qoder 用户级 hooks 事件集**未实证**：只写说明、不登记为能力、不假装覆盖；L3 外部强制点仅覆盖获授权仓库的 Git Hook/CI。
- v2.1 post-turn 压缩 hook **尽力而为**：若未来公开 post-turn/Stop 事件可挂接自动写 `.acs/handoff.md`；当前未实证，真正的保证是便携机检——`gate_checklist.py` 的 handoff_compression 门强制当前 task/latest-step 绑定、三个精确非空段落、证据/完成标准与 Unicode 码点上限，任何终端都生效。
- v2.1 原生技能**引用不打包**：本机 ~/.qoder/skills 与 ~/.qwenworkcn/skills 的百余平台技能由 skill_load=Skill 原生路由，不复制进包（依赖终端运行时/内部 MCP/内网鉴权，物理不可打包）。
- 非 Qoder 终端：本技能整体 SKIP，按 adapters generic profile 与各 capability fallback 执行。
