# 附件历史驱逐规格（ATTACHMENT_EVICTION_SPEC）

状态：已实现（review L1 收口；附带收口 review L4）
日期：2026-09-13

## 1. 问题（review L1，设计级）

带附件的用户消息在持久化时把附件注入正文（分块注入，单轮上限
`AttachmentInjectionBudgetChars = 120_000` 字符）拼进 `Content`
（`注入正文 + "\n\n" + 用户原文`）。此后每一轮对话都会：

1. 从数据库加载**完整历史**（含所有旧注入）；
2. 把完整历史映射为模型上下文并整体重发。

后果：多附件长会话的模型上下文与每轮内存占用**无界增长**——
N 轮附件对话 ⇒ 每轮重发最多 N×120K 字符的旧附件正文，
token 成本爆炸、小上下文模型直接溢出。

## 2. 策略

驱逐点在 `HistoryMapper.ToProviderMessages`（唯一模型上下文边界，
Single 与全部 MOA 策略共用）：

- **保留窗口**：最近 `AttachmentRetentionUserTurns = 2` 个「实际进上下文
  的用户轮」（按发出顺序计；失败/取消而被跳过的用户轮不计数、不占窗口）
  保留完整注入正文——覆盖「刚发的文件下一轮接着问」的常见模式。
  **steer 插话不计窗口**（review MED-1）：插话由门面在本轮用户消息之后追加，
  若计数则排队 ≥2 条插话时会把当前轮刚发送的附件消息顶出窗口，
  模型在自己这轮就看不到刚上传的文件；steer 也不庇护更早的附件轮。
- **驱逐降级**：更早的带附件用户轮，其上下文内容替换为
  「元信息存根 + 原始用户文本」：
  `[历史附件正文已从上下文驱逐，仅保留元信息：a.md（10.0KB）、…]` + 原文。
  驱逐只丢附件正文，**绝不丢用户的话**。
- **锚点**：`chat_messages.UserText` 新列保存带附件消息的原始用户文本。
  `Content` 持久化后无法无损拆回注入与原文，锚点列是驱逐的拆分依据。
- **诚实兼容**：无附件消息不受影响；早期数据（`UserText` 为 null 的存量行）
  保持原样——不做破坏性猜测，驱逐只作用于具备锚点的新数据。
- **元数据损坏/ hostile 形态**（review MED-2）：`AttachmentsJson` 解析失败、
  数组元素非对象、`FileName` 非字符串等非法形态逐项防御（ValueKind 校验），
  一律降级为存根（合法项仍入清单），任何异常不得逃出驱逐路径。
- **编辑/重跑/分叉运行**（review MED-3）：历史里附件消息的 `Content` 含注入
  正文；消息操作（编辑/重跑/分叉运行）优先取 `UserText` 原文锚点，避免把
  陈旧注入正文当作新输入重发——重发将落库为**不可驱逐**的巨型用户消息，
  重新撑大上下文，等于绕开本策略。

## 3. 不变量

- I1 模型上下文中附件注入正文总量 ≤ 2 × 120K 字符（硬上界）。
- I2 数据库与 UI 展示零改动：驱逐只作用于每轮临时构造的模型上下文副本；
  持久化 `Content` 保持完整，fork/重跑/导出行为不变。
- I3 用户原文在任何路径下都不丢失。
- I4（review L4）分隔符计入注入预算：`BuildInjection(budget - separator.Length)`。
  按 BuildInjection 既有计量语义（注入文本正文按预算计量；块尾换行 ≤n-1 字符
  与二进制附件的一行元信息设计上不计预算），持久化后的附件部分
  （注入 + 分隔符）≤ 预算 + 不计账部分；单文本附件场景严格 ≤120K（用例实证）。

## 4. 变更面

| 文件 | 变更 |
| --- | --- |
| `AeroAgent.Conversation/Models/ChatMessage.cs` | 新增 `UserText` 列属性 |
| `AeroAgent.Conversation/Data/ConversationDbContext.cs` | 列映射 + 存量库幂等补列（PRAGMA 检测 + ALTER TABLE） |
| `AeroAgent.Conversation/Services/SessionService.cs` | Detach 与 fork 复制携带 `UserText`；自动标题取原文锚点（LOW-3） |
| `AeroAgent.Conversation/Orchestration/ChatOrchestrationFacade.cs` | 持久化锚点；L4 分隔符计入预算 |
| `AeroAgent.Conversation/Orchestration/HistoryMapper.cs` | 保留窗口 + 驱逐降级（替换条目，AiChatMessage 为 init-only）；steer 不计窗口（MED-1）；hostile 元数据防御（MED-2） |
| `AeroCode.App/ViewModels/ChatViewModel.cs` | `MessageItemViewModel.UserText` 投影；编辑/重跑/分叉运行优先取锚点（MED-3） |

## 5. 验证（15 个新用例 + 1 个扩展用例）

- `HistoryMapperTests`（8）：窗口内保留/窗口外驱逐、无锚点保持原样、
  非附件轮不受影响、失败轮不占窗口、损坏 JSON 降级、steer 不占窗口
  （MED-1）、steer 不庇护更早轮、hostile 元素形态不抛异常（MED-2）。
- `FacadeAttachmentTests`（3）：锚点持久化（带附件=原文/无附件=null）、
  三轮附件端到端（第三轮模型上下文驱逐第一轮而 DB 保真）、
  20 万字符附件的预算上界。
- `SchemaMigrationTests`（1 新 + 1 扩展）：存量库补列 + EF 读写往返；
  全新库幂等列清单纳入 AttachmentsJson/UserText。
- `SessionServiceTests`（1）：附件首消息标题取原文锚点（LOW-3）。
- `ChatViewModelTests`（2）：编辑命令优先取锚点/无锚点回退 Content（MED-3）。
