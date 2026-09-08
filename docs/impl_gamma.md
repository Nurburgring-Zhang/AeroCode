# R4-γ 实现日志（能力扩展：附件 / 配置事件 / 备份保留）

日期：2026-09-07 · 基线：R3 收口 b54457b（1824 有效绿）→ 本次全量 **1852 通过 / 0 失败 / 24 跳过（共 1876）**

## 范围（三项）

| 项 | 来源 | 内容 |
| --- | --- | --- |
| γ-1 | 审查 S-LOW-2 | SettingsService corrupt 备份保留上限 3 份 |
| γ-2 | 能力扩展 | SettingsChanged 分层配置热重载事件 |
| γ-3 | 能力扩展 | 图片附件最小可用链路（选择/粘贴/记录/发送描述） |

## γ-1 corrupt 备份保留策略

`src/AeroCode.App/Configuration/SettingsService.cs`

- `MaxCorruptBackups = 3`（:486）。损坏 JSON 改名备份后调用 `PruneCorruptBackups`（:565→:581）：
  按文件名字典序降序（时间戳命名 ⇒ 字典序 = 时间序）保留最新 3 份，删除其余。
- 幂等：≤3 份时不做任何删除（:589）。
- 语义不变：备份只改名不覆盖原内容；环境故障（IOException）不产生备份。

## γ-2 SettingsChanged 事件

`src/AeroCode.App/Configuration/SettingsService.cs`

- `public event EventHandler? SettingsChanged`（:491）。
- 触发点：LoadAsync 成功路径（:531）与 SaveAsync 成功后（:614，含缺失文件的默认值首存）。
- **不触发**：corrupt 回退分支——默认值不是用户意图，订阅方不得据此当作配置变更。

## γ-3 图片附件（最小可用）

全链路六层：

1. **模型** `src/AeroAgent.Conversation/Models/MessageAttachment.cs`（新文件）：
   `record MessageAttachment(FileName, MimeType, SizeBytes, PreviewBytes?)`；
   `PreviewBytes` 标 `[JsonIgnore]`（预览字节绝不进 JSON/DB）；`DisplaySize` 三档（B/KB/MB）；
   `ToDescription()` → `[Attached image: {name} ({size}, {mime})]`。
2. **实体/Schema** `ChatMessage.AttachmentsJson`（Models/ChatMessage.cs:93）；
   `ConversationDbContext` 映射 TEXT（Data/ConversationDbContext.cs:139）+
   `EnsureSchemaAsync` 对存量库 `ALTER TABLE chat_messages ADD COLUMN "AttachmentsJson"`（:61，幂等补列）。
3. **门面** `ChatOrchestrationFacade.SendAsync(sessionId, text, attachments, ct)` 新重载
   （Orchestration/ChatOrchestrationFacade.cs:110-127）：附件描述前缀注入用户文本 +
   `[{FileName,MimeType,SizeBytes}]` 序列化落 `AttachmentsJson`；无附件路径行为不变。
4. **ViewModel** `ChatViewModel`：`PendingAttachments` 集合、`AttachFileAsync`（StorageProvider
   文件选择器，png/jpg/jpeg/gif/webp）、`AttachFromClipboard`、`RemoveAttachment`；
   发送时快照并清空待发列表；历史加载经 `BuildAttachmentSummary`（:1060，容错：
   非法 JSON/空数组/缺 FileName → null）恢复 `AttachmentSummary` 投影（:632-633）。
5. **View** `Views/ChatView.axaml`：助手/用户气泡底部附件摘要行（:117-118）、
   输入区上方待发附件条（:252-253）；`ChatView.axaml.cs` 剪贴板图片粘贴（:173）。
6. **诚实边界**：预览字节只取前 4KB 占位、**本版不渲染缩略图**；附件以文本描述注入模型上下文，
   不是多模态上传（provider 不支持时的如实降级路径）。

## 测试暴露的真实缺陷（已修复）

`FacadeAttachmentTests` 首跑失败（AttachmentsJson 回读为 null）——不是测试 DB 问题，
而是 `SessionService` 两处手工字段投影漏掉新列：

- `Detach(ChatMessage)`（Services/SessionService.cs:75）——所有消息回读经此投影，漏字段 ⇒ UI 永远看不到附件；
- `ForkSessionAsync` 消息复制（:407）——fork 会话丢附件元数据。

两处补 `AttachmentsJson = m.AttachmentsJson`。γ 测试子集随即 22/22 全绿。
（教训：该服务逐字段手写投影，每加一列必须同步两处——已在全量回归覆盖。）

## 测试

新增 `tests/AeroCode.Tests/AppTests/GammaFeatureTests.cs`（4 类 22 例）：

- `MessageAttachmentTests`（8）：DisplaySize 七档 Theory + ToDescription 格式 + PreviewBytes 不序列化。
- `SettingsGammaTests`（6）：备份保留 5→3 / 恰好 3 份不清理 / SettingsChanged 在成功加载、
  Save、缺失文件分支触发、corrupt 回退不触发。
- `FacadeAttachmentTests`（2）：带附件发送（描述前缀 + JSON 持久化 + 无 PreviewBytes）/
  无附件发送（文本不变、JSON 为 null）。
- `ChatViewModelAttachmentTests`（5）：BuildAttachmentSummary 反射直测
  （合法 JSON / 非法 JSON / 空数组 / null / 缺 FileName 跳过）+ 投影算法对照。

## 验证结果

- γ 子集：**22/22 通过**（2s）。
- 全量回归：**1852 通过 / 0 失败 / 24 跳过（1876）**，43s；高于 R3 基线 1824，无回归。
- 构建：`TreatWarningsAsErrors` 下零警告（Android 头项目无 SDK 不参与本机构建，同前波次口径）。

## 未覆盖（如实）

- Android 端未真机冒烟（View 层为 Avalonia 共享代码，随双端产物重建后覆盖；
  剪贴板粘贴依赖平台 Avalonia 剪贴板实现，桌面端已验证路径）。
- 双端产物重建、双盲审查、提交推送属 γ 之后的收口步骤，不在本日志范围。
