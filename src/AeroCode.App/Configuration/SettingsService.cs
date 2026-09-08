using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AeroCode.AI.Configuration;
using AeroCode.App.Services;
using AeroCode.Mcp.Client;

namespace AeroCode.App.Configuration;

/// <summary>
/// 应用设置:从 %LOCALAPPDATA%\AeroCode\settings.json 加载/保存。
/// 包含 AI provider 配置 + UI 偏好。绝不硬编码 API key。
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("ai")]
    public AISettings Ai { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiSettings Ui { get; set; } = new();

    /// <summary>MCP server 连接配置（stdio 子进程）。空 = 未接入任何外部工具服务器。</summary>
    [JsonPropertyName("mcpServers")]
    public System.Collections.Generic.List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>工作区工具域设置（批次 A：工作区根 / git 工作流 / 启动档位）。</summary>
    [JsonPropertyName("workspace")]
    public WorkspaceSettings Workspace { get; set; } = new();

    /// <summary>子代理派发（批次 B）：字段对照 SubagentOptions，组合根映射注入。</summary>
    [JsonPropertyName("subagent")]
    public SubagentSettings Subagent { get; set; } = new();

    /// <summary>安全控制（批次 B）：守卫链/审批熔断/智能审批/急停哨兵。</summary>
    [JsonPropertyName("safety")]
    public SafetySettings Safety { get; set; } = new();

    /// <summary>事件钩子引擎开关（批次 B）。</summary>
    [JsonPropertyName("hooks")]
    public HooksSettings Hooks { get; set; } = new();

    /// <summary>自动化调度开关（批次 B）。</summary>
    [JsonPropertyName("scheduler")]
    public SchedulerSettings Scheduler { get; set; } = new();

    /// <summary>会话记忆（批次 B G2）：召回条数与自动沉淀开关。</summary>
    [JsonPropertyName("memory")]
    public MemorySettings Memory { get; set; } = new();

    /// <summary>上下文压缩（批次 B G2）：工具循环溢出检测阈值。</summary>
    [JsonPropertyName("compaction")]
    public CompactionSettings Compaction { get; set; } = new();

    /// <summary>mission 级 token 预算闸门（R1 缝合 #13）：默认关闭 = 现行为（无闸门）。</summary>
    [JsonPropertyName("budget")]
    public BudgetSettings Budget { get; set; } = new();

    /// <summary>循环守卫（R1 缝合 #13）：默认关闭 = 现行为（不注入锚定/偏离检测/升级链）。</summary>
    [JsonPropertyName("loopGuard")]
    public LoopGuardSettings LoopGuard { get; set; } = new();

    /// <summary>上下文策展（R1 缝合 #13）：默认关闭 = 现行为（WorkerRunner.Curator 保持 null）。</summary>
    [JsonPropertyName("curation")]
    public CurationSettings Curation { get; set; } = new();

    /// <summary>
    /// R2 缝合（#20）B1 成本排序选模设置节。全部默认 = α 内建默认；
    /// 四层判定模型选择路径默认不接入生产（ModelAssigner 契约 request=null = 现行为），
    /// 本节的峰值档/缓存断点开关是唯二进入生产请求面的字段（组合根接线）。
    /// </summary>
    [JsonPropertyName("costTiers")]
    public CostTierSettings CostTiers { get; set; } = new();

    /// <summary>R2 缝合（#20）B4 effort 峰值档设置节。默认关闭 = 现行为（Standard 档，请求不发射 effort 字段）。</summary>
    [JsonPropertyName("effort")]
    public EffortSettings Effort { get; set; } = new();

    /// <summary>R2 缝合（#20）B5 guardrail 设置节。默认 MarkOnly = 现行为（只标记不拦截）。</summary>
    [JsonPropertyName("guardrail")]
    public GuardrailSettings Guardrail { get; set; } = new();

    /// <summary>R2 缝合（#20）C2 critique 校验循环设置节。默认关闭 = 现行为（无完成判定 critique）。</summary>
    [JsonPropertyName("critique")]
    public CritiqueSettings Critique { get; set; } = new();

    /// <summary>R2 缝合（#20）B6 弃用监控设置节。默认关闭 = 现行为（绝不外呼）。</summary>
    [JsonPropertyName("deprecation")]
    public DeprecationSettings Deprecation { get; set; } = new();

    /// <summary>R3-δ 代管字段（α：Android 前台 service 开关）。默认 false = 现行为。契约钉死名与默认值。</summary>
    [JsonPropertyName("android")]
    public AndroidSettings Android { get; set; } = new();

    /// <summary>R3-δ 代管字段（γ：沙箱强制执行开关）。默认 false = 现行为。契约钉死名与默认值。</summary>
    [JsonPropertyName("sandbox")]
    public SandboxSettings Sandbox { get; set; } = new();

    /// <summary>ACS v2.3.0 纪律运行时设置节（分级/成本闸门/有界重试/交接/诚实台账）。默认值镜像 ACS thresholds。</summary>
    [JsonPropertyName("acs")]
    public AcsSettings Acs { get; set; } = new();
}

/// <summary>
/// ACS v2.3.0 纪律运行时设置节。默认值镜像 ACS spec/thresholds.json（单一真相源为内嵌资源，
/// 本节是用户可调投影；Enabled=false 时全部纪律组件不注入，现行为逐字节不变）。
/// </summary>
public sealed class AcsSettings
{
    /// <summary>总开关：false = ACS 纪律组件不注入（现行为）；true = 成本闸门/分级/台账生效。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>窄步闸：单步可验证产出上限（默认 1）。</summary>
    [JsonPropertyName("maxOutputsPerStep")]
    public int MaxOutputsPerStep { get; set; } = 1;

    /// <summary>窄步闸：单步预算分钟（默认 30）。</summary>
    [JsonPropertyName("maxBudgetMin")]
    public int MaxBudgetMin { get; set; } = 30;

    /// <summary>回灌禁令：单步引用上文字节上限（默认 20000）。</summary>
    [JsonPropertyName("maxContextBytes")]
    public int MaxContextBytes { get; set; } = 20000;

    /// <summary>回灌禁令：单步读文件数上限（默认 5）。</summary>
    [JsonPropertyName("maxFilesRead")]
    public int MaxFilesRead { get; set; } = 5;

    /// <summary>回灌禁令：跨步总结字符上限（默认 1000）。</summary>
    [JsonPropertyName("maxSummaryChars")]
    public int MaxSummaryChars { get; set; } = 1000;

    /// <summary>思考预算闸：思考占比警戒线（默认 0.40）。</summary>
    [JsonPropertyName("maxThinkRatio")]
    public double MaxThinkRatio { get; set; } = 0.40;

    /// <summary>空转闸：连续无新证据 strike 上限（two-strike，默认 2）。</summary>
    [JsonPropertyName("spinStrikes")]
    public int SpinStrikes { get; set; } = 2;

    /// <summary>有界重试：最大重试次数（默认 2）。</summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 2;

    /// <summary>压缩交接：字数硬上限（默认 1000）。</summary>
    [JsonPropertyName("handoffMaxCharsHard")]
    public int HandoffMaxCharsHard { get; set; } = 1000;

    /// <summary>压缩交接：字数目标（默认 400）。</summary>
    [JsonPropertyName("handoffTargetChars")]
    public int HandoffTargetChars { get; set; } = 400;

    /// <summary>诚实台账：必填字段最小字符数（默认 4）。</summary>
    [JsonPropertyName("honestyMinFieldChars")]
    public int HonestyMinFieldChars { get; set; } = 4;
}

/// <summary>B1 成本排序选模设置节（R2 缝合 #20；R2 修复 HIGH-1 起 enabled=true 时接入生产选模点）。</summary>
public sealed class CostTierSettings
{
    /// <summary>
    /// B1 四层成本排序总开关（R2 修复 HIGH-1）：默认 false = worker 选模走既有 Assign 打分路径（现行为，
    /// 与开关引入前逐字节一致）；true 时组合根把本节映射进 DecomposeStrategy，worker 选模改走
    /// ModelAssigner.Decide 四层判定（①长上下文 → ②缓存折扣 → ③batch → ④峰值档）。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>①长上下文阈值（token）；null = 内建默认 100_000。</summary>
    [JsonPropertyName("longContextTokenThreshold")]
    public int? LongContextTokenThreshold { get; set; }

    /// <summary>③batch 折扣倍率；null = 内建默认 0.5。</summary>
    [JsonPropertyName("batchDiscountMultiplier")]
    public double? BatchDiscountMultiplier { get; set; }

    /// <summary>④峰值档溢价倍率；null = 内建默认 1.0（仅标注口径，不扭曲排序）。</summary>
    [JsonPropertyName("peakPremiumMultiplier")]
    public double? PeakPremiumMultiplier { get; set; }

    /// <summary>
    /// ④峰值档开关（R2 修复 HIGH-1 起为 B1 四层判定的④层真实输入）：enabled=true 时作为
    /// PeakRequested 传入 ModelAssigner.Decide；峰值档“实际可用性”仍由 B4 EffortProfile 探测裁决。
    /// </summary>
    [JsonPropertyName("peakTierEnabled")]
    public bool PeakTierEnabled { get; set; }

    /// <summary>
    /// 缓存断点传递开关（缝合⑥）：默认 false = 请求逐字节基线；true 时把工具循环冻结前缀边界
    /// 作为 ChatRequest.CacheBreakpoints 发射（仅消费该字段的 provider 会产生缓存块，如 Anthropic cache_control）。
    /// </summary>
    [JsonPropertyName("cacheBreakpointsEnabled")]
    public bool CacheBreakpointsEnabled { get; set; }
}

/// <summary>B4 effort 峰值档设置节（R2 缝合 #20）。映射表钉死为研究口径（O=xhigh / A=extended-thinking / G=deep-think）。</summary>
public sealed class EffortSettings
{
    /// <summary>峰值推理档开关：默认 false = Standard 档（不注入 EffortProfile，请求与基线逐字节一致）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>B5 guardrail 设置节（R2 缝合 #20）。</summary>
public sealed class GuardrailSettings
{
    /// <summary>
    /// 工作模式："MarkOnly"（默认，只标记不拦截 = 现行为）| "Enforce"（阻断性发现升级为拦截）。
    /// 非法值回退 MarkOnly 并记 WARN。注：当前内建验证器仅输入段标记（FactAssertion），
    /// 工具段无验证器时 Enforce 不产生实际拦截（组合根日志如实标注）。
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "MarkOnly";
}

/// <summary>C2 critique 校验循环设置节（R2 缝合 #20）。</summary>
public sealed class CritiqueSettings
{
    /// <summary>false = 不注入 ICompletionVerifier（现行为）；true 且默认 provider 可用时装配 LLM critique 通道。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>critique 评估轮上限（CritiqueLoop 构造时硬钳制 [1,2]，配置无法突破）。</summary>
    [JsonPropertyName("maxCritiqueRounds")]
    public int MaxCritiqueRounds { get; set; } = 2;
}

/// <summary>B6 弃用监控设置节（R2 缝合 #20）。</summary>
public sealed class DeprecationSettings
{
    /// <summary>false = 绝不外呼（现行为）；true 才允许访问白名单内 URL。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>允许外呼的 changelog URL 白名单（精确匹配；空 = 即使启用也不外呼）。</summary>
    [JsonPropertyName("urlAllowlist")]
    public List<string> UrlAllowlist { get; set; } = new();

    /// <summary>
    /// R3-δ（契约钉死名与默认值）：Moa 网关执行路径消费弃用监控的门控。
    /// 默认 false = 现行为（执行路径完全不调 monitor）。
    /// R3 修复（S-MED-5）：外呼需 <see cref="Enabled"/> 与本字段双真——enabled=false
    /// 绝不外呼（总闸语义不被本开关旁路）；双真时组合根构造启用实例注入 ExpertsStrategy，
    /// 网关 execute 后做 MarkOnly 检查（命中 deprecated → WARN，绝不拦截）。
    /// </summary>
    [JsonPropertyName("monitor")]
    public bool Monitor { get; set; }
}

/// <summary>R3-δ 代管设置节（α 消费：Android 前台 service）。契约钉死节名 android / 字段名 foregroundService / 默认 false。</summary>
public sealed class AndroidSettings
{
    /// <summary>false = 现行为（不启前台 service）；true 由 α 的 Android 工程侧消费。</summary>
    [JsonPropertyName("foregroundService")]
    public bool ForegroundService { get; set; }
}

/// <summary>R3-δ 代管设置节（γ 消费：沙箱强制执行）。契约钉死节名 sandbox / 字段名 enforce / 默认 false。</summary>
public sealed class SandboxSettings
{
    /// <summary>false = 现行为（沙箱只标记不强制）；true 由 γ 的安全切片消费。</summary>
    [JsonPropertyName("enforce")]
    public bool Enforce { get; set; }
}

/// <summary>子代理设置节。MaxTurns/MaxCostUsd 由派发方（工具/任务）按需映射进 SubAgentSpec。</summary>
public sealed class SubagentSettings
{
    /// <summary>false 时派发诚实失败（SubAgentRunner 抛 InvalidOperationException）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>深度上限（层数含自身；被 SubAgentSpec.MaxDepth=4 硬上限钳制）。</summary>
    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 4;

    /// <summary>同时运行的子代理实例上限（≥1，超限排队）。</summary>
    [JsonPropertyName("maxParallel")]
    public int MaxParallel { get; set; } = 2;

    /// <summary>单次派发的默认工具轮数上限（>0）。</summary>
    [JsonPropertyName("maxTurns")]
    public int MaxTurns { get; set; } = 16;

    /// <summary>单次派发的默认成本上限（美元，≥0；0 = 不设计价上限）。</summary>
    [JsonPropertyName("maxCostUsd")]
    public double MaxCostUsd { get; set; } = 1.0;

    /// <summary>
    /// 并行开关（R1 C-GATE）：false = 降级单 agent（并行上限钳 1，派发仍可用）。
    /// 默认 true = 现行为（Moa 层保持并行可用）；翻转只经设置层显式配置发生。
    /// </summary>
    [JsonPropertyName("parallelEnabled")]
    public bool ParallelEnabled { get; set; } = true;
}

/// <summary>安全设置节。EstopFile 为空 = 不启用急停哨兵检查；AdvisorModel 为空 = 智能审批建议器不可用。</summary>
public sealed class SafetySettings
{
    /// <summary>doom-loop 阈值：同工具同参数第 N 次升级 Ask（≥2）。</summary>
    [JsonPropertyName("doomLoopThreshold")]
    public int DoomLoopThreshold { get; set; } = 3;

    /// <summary>急停哨兵文件路径；空 = 不启用（不构建 EstopGuard）。</summary>
    [JsonPropertyName("estopFile")]
    public string EstopFile { get; set; } = string.Empty;

    /// <summary>智能审批判定 risk=low 时自动放行（记录在案；false = 一律弹窗）。</summary>
    [JsonPropertyName("autoApproveLowRisk")]
    public bool AutoApproveLowRisk { get; set; }

    /// <summary>审批建议器判定模型（便宜档）；空 = advisor 不可用，审批行为不变。</summary>
    [JsonPropertyName("advisorModel")]
    public string AdvisorModel { get; set; } = string.Empty;

    /// <summary>审批熔断：连续批准次数阈值（≥1）。</summary>
    [JsonPropertyName("approvalBurstLimit")]
    public int ApprovalBurstLimit { get; set; } = 25;

    /// <summary>审批熔断：会话累计成本阈值（美元，>0）。</summary>
    [JsonPropertyName("approvalCostLimitUsd")]
    public double ApprovalCostLimitUsd { get; set; } = 5.0;

    /// <summary>
    /// R3-γ（契约钉死名与默认值）：advisor 自动采纳收紧开关。
    /// 默认 false = 现行为逐字节一致（现状无 args 修改/白名单概念，无条件自动采纳）；
    /// true 时自动采纳先过收紧门——args 被脱敏且被脱敏参数不全在白名单内 → 转人工审批弹窗
    ///（不静默丢弃、不静默放行）。开关经组合根映射进 DialogPermissionBroker 构造。
    /// </summary>
    [JsonPropertyName("autoAdoptTighten")]
    public bool AutoAdoptTighten { get; set; }

    /// <summary>
    /// R3-γ（契约钉死名与默认值）：允许「被脱敏后仍参与自动采纳判定」的参数名集合
    ///（Ordinal 精确匹配）。默认空 = 被脱敏即转人工。仅在 autoAdoptTighten=true 时消费。
    /// </summary>
    [JsonPropertyName("autoAdoptWhitelist")]
    public List<string> AutoAdoptWhitelist { get; set; } = new();

    /// <summary>
    /// R3 修复（F-MED-2，契约钉死名与默认值）：advisor 请求内容脱敏开关。
    /// 默认 false = R3 前行为逐字节一致（工具参数原样序列化进 advisor prompt，无开关
    /// 引入前的用户无行为变化）；true 时参数进 prompt 前过 canonical 词表脱敏
    ///（敏感原文不外送判定模型，被脱敏部分以 [REDACTED] 呈现）。
    /// 开关经组合根映射进 PermissionAdvisor 构造。
    /// </summary>
    [JsonPropertyName("advisorSanitizeArgs")]
    public bool AdvisorSanitizeArgs { get; set; }
}

/// <summary>钩子引擎设置节（hooks.json 缺失 = 空载，不是降级）。</summary>
public sealed class HooksSettings
{
    /// <summary>false = 不加载 hooks.json 也不订阅事件分发。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>调度服务设置节（jobs.json 缺失 = 空载，不是降级）。</summary>
public sealed class SchedulerSettings
{
    /// <summary>false = 不启动轮询 Timer（注册仍生效，供设置页查看/编辑）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>会话记忆设置节（批次 B G2）。</summary>
public sealed class MemorySettings
{
    /// <summary>语义召回 Top-K 条数（0 = 关闭笔记召回，仅注入 MEMORY.md/USER.md）。</summary>
    [JsonPropertyName("recallTopK")]
    public int RecallTopK { get; set; } = 5;

    /// <summary>对话轮结束后自动沉淀经验（真实轨迹/失败教训入 ExperienceStore）。</summary>
    [JsonPropertyName("autoConsolidate")]
    public bool AutoConsolidate { get; set; } = true;
}

/// <summary>上下文压缩设置节（批次 B G2）。ThresholdTokens ≤ 0 = 关闭溢出检测。</summary>
public sealed class CompactionSettings
{
    /// <summary>工具循环上下文 token 估算阈值（4 字符≈1 token 的既有口径）。</summary>
    [JsonPropertyName("thresholdTokens")]
    public int ThresholdTokens { get; set; } = 24000;

    /// <summary>压缩后保留的最近消息条数（Compactor 语义）。</summary>
    [JsonPropertyName("keepRecentMessages")]
    public int KeepRecentMessages { get; set; } = 10;
}

/// <summary>
/// mission 级 token 预算闸门设置节（R1 缝合 #13，契约 C-GATE）。
/// Enabled=false（默认）= 现行为：不构建 TokenBudgetGate，子代理派发不受 mission 级
/// token 上限约束；启用后派发前过闸门、逐轮实报真实 usage、Exhausted 降级单 agent。
/// </summary>
public sealed class BudgetSettings
{
    /// <summary>false = 不构建闸门（现行为）；true 且 limitTokens 为正时启用。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>mission 级 token 上限（真实 usage 逐轮实报累计；≤0 时闸门不启用并记 WARN）。</summary>
    [JsonPropertyName("limitTokens")]
    public long LimitTokens { get; set; } = 1_000_000;

    /// <summary>警告水位比例，须在 (0,1]；非法值回退 0.8 并记 WARN。</summary>
    [JsonPropertyName("warningRatio")]
    public double WarningRatio { get; set; } = 0.8;
}

/// <summary>
/// 循环守卫设置节（R1 缝合 #13，契约 C-LOOP）。
/// Enabled=false（默认）= 现行为：WorkerRunner 的 LoopGuardOptions/偏离检测器/升级策略
/// 均保持 null；启用后按字段构造三件套注入，升级凭据由 MissionController 受理供人审批。
/// </summary>
public sealed class LoopGuardSettings
{
    /// <summary>false = 不构造 LoopGuardOptions / 偏离检测器 / 升级策略（现行为）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>每轮首注入目标锚定段（LoopGuardOptions.GoalAnchoringEnabled 直传）。</summary>
    [JsonPropertyName("goalAnchoringEnabled")]
    public bool GoalAnchoringEnabled { get; set; } = true;

    /// <summary>连续 OffGoal strike 触发 checkpoint 恢复 + 升级的阈值（≥1）。</summary>
    [JsonPropertyName("maxStrikes")]
    public int MaxStrikes { get; set; } = 2;

    /// <summary>锚定段中最近 checkpoint 摘要的最大字符数。</summary>
    [JsonPropertyName("checkpointSummaryChars")]
    public int CheckpointSummaryChars { get; set; } = 200;

    /// <summary>自定义偏离关键词（大小写不敏感包含匹配）；空 = DefaultKeywordDetector 默认词表。</summary>
    [JsonPropertyName("offGoalKeywords")]
    public List<string> OffGoalKeywords { get; set; } = new();
}

/// <summary>
/// 上下文策展设置节（R1 缝合 #13，契约 C-CURATE）。
/// Enabled=false（默认）= 现行为：WorkerRunner.Curator 保持 null，工具循环不策展；
/// 启用后构造真实 ContextCurator 绑定到 WorkerRunner（阈值/保留窗口由本节映射）。
/// </summary>
public sealed class CurationSettings
{
    /// <summary>false = 不构造/绑定 ContextCurator（现行为）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>策展水位阈值（token 估算；≤0 = 策展器自关闭，行为同未装配）。</summary>
    [JsonPropertyName("watermarkThresholdTokens")]
    public int WatermarkThresholdTokens { get; set; } = 8000;

    /// <summary>触发压缩时保留的最近对话条数（≥1）。</summary>
    [JsonPropertyName("keepRecentMessages")]
    public int KeepRecentMessages { get; set; } = 8;

    /// <summary>外置状态块字符硬上限。</summary>
    [JsonPropertyName("maxStateBlockChars")]
    public int MaxStateBlockChars { get; set; } = 400;

    /// <summary>true = 摘要走默认 provider 的真实 LLM；provider 不可用时降级确定性模板（记 WARN）。</summary>
    [JsonPropertyName("useLlmSummary")]
    public bool UseLlmSummary { get; set; }
}

/// <summary>
/// 工作区设置节。Root 为空串 = 使用 Documents/AeroCode-workspace（首次惰性创建）；
/// 指定了 Root 但目录无法创建时组合根诚实降级（不注册 workspace/git 工具域），绝不伪造根路径。
/// </summary>
public sealed class WorkspaceSettings
{
    /// <summary>工作区根目录；空 = 用 Documents/AeroCode-workspace，首次惰性创建。</summary>
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    /// <summary>编辑后自动 git 提交（不在 git 仓时如实跳过，不伪造提交）。</summary>
    [JsonPropertyName("autoCommit")]
    public bool AutoCommit { get; set; }

    /// <summary>脏区保护：存在与本次编辑无关的未暂存改动时不自动提交，等用户决定。</summary>
    [JsonPropertyName("protectDirty")]
    public bool ProtectDirty { get; set; } = true;

    /// <summary>启动即 AcceptEdits 档（文件编辑免逐次确认；shell 与网络仍走原规则）。</summary>
    [JsonPropertyName("autoApproveEdits")]
    public bool AutoApproveEdits { get; set; }

    /// <summary>run_shell 默认超时秒数（超时杀整棵进程树；单条命令可用参数覆盖）。</summary>
    [JsonPropertyName("shellTimeoutSeconds")]
    public int ShellTimeoutSeconds { get; set; } = 60;
}

public sealed class AISettings
{
    [JsonPropertyName("defaultProviderId")]
    public string DefaultProviderId { get; set; } = "deepseek";

    [JsonPropertyName("defaultModel")]
    public string DefaultModel { get; set; } = "deepseek-v4-flash";

    [JsonPropertyName("providers")]
    public System.Collections.Generic.List<ProviderConfig> Providers { get; set; } = new();
}

public sealed class UiSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 14;

    [JsonPropertyName("memoryMaxChars")]
    public int MemoryMaxChars { get; set; } = 2200;        // Hermes MEMORY.md cap

    [JsonPropertyName("userProfileMaxChars")]
    public int UserProfileMaxChars { get; set; } = 1375;   // Hermes USER.md cap
}

/// <summary>
/// 读写 settings.json。API key 不直接存文件,只存 env var 名 + 用 DPAPI 加密实际值(可选)。
/// </summary>
public sealed class SettingsService
{
    private readonly AppDataPaths _paths;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>读取选项：写入侧是 camelCase，读取必须大小写不敏感，
    /// 否则 provider 字段（id/baseUrl/...）在重载时全部静默丢失。</summary>
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int MaxCorruptBackups = 3;

    public AppSettings Current { get; private set; } = new();

    /// <summary>R4-γ：设置成功加载或保存后触发（外部文件监视 / 热重载消费者订阅）。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// R3-δ 损坏 JSON 加固：最近一次 LoadAsync 的拒载异常（null = 文件缺失/加载成功/上次成功）。
    /// 仿 <c>JsonMoaOptionsStore.LastLoadError</c> 手法：拒载仍按 fail-safe 语义回退默认值
    /// （不阻塞启动），但拒载事实经本属性暴露——组合根据此显式 WARN，使「用户配置被拒载」
    /// 可观测而非静默（R1 已知「损坏 JSON 静默覆盖」缺陷的收口）。
    /// </summary>
    public Exception? LastLoadError { get; private set; }

    /// <summary>
    /// R3 修复（F-MED-1）：拒载时损坏文件备份的目标路径（null = 未拒载/备份未生成）。
    /// 组合根 WARN 只引用「拒载事实 + 备份文件名」，不引用 <see cref="LastLoadError"/> 的
    /// 异常消息全文——JsonException 消息可能回显 JSON 内容，直接落日志有泄敏风险。
    /// </summary>
    public string? LastCorruptBackupPath { get; private set; }

    public SettingsService(AppDataPaths paths)
    {
        _paths = paths;
        _paths.EnsureAll();
    }

    public async Task LoadAsync()
    {
        var path = _paths.SettingsFile;
        if (!File.Exists(path))
        {
            LastLoadError = null;
            LastCorruptBackupPath = null;
            Current = CreateDefaults();
            await SaveAsync();
            return;
        }
        try
        {
            var json = await File.ReadAllTextAsync(path);
            Current = JsonSerializer.Deserialize<AppSettings>(json, ReadOpts) ?? CreateDefaults();
            LastLoadError = null;
            LastCorruptBackupPath = null;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonException ex)
        {
            // R3-δ 损坏 JSON 加固（不再静默）：
            // 1) 不覆盖原文件——先把损坏文件改名备份为 *.corrupt-<UTC时间戳>，字节原样保留；
            // 2) 以默认值继续运行（fail-safe，不阻塞启动）；
            // 3) 拒载事实经 <see cref="LastLoadError"/> 暴露（组合根 WARN），可观测。
            // IOException/UnauthorizedAccessException 等环境故障仍大声上抛（见上方注释）。
            LastLoadError = ex;
            LastCorruptBackupPath = BackupCorruptFile(path);
            Current = CreateDefaults();
        }
    }

    /// <summary>
    /// R3-δ：把损坏的 settings.json 改名备份为 <c>settings.json.corrupt-&lt;UTC时间戳&gt;</c>。
    /// 只改名不复制内容（Move 保留字节）；同名冲突（同毫秒多次损坏，罕见）追加序号防互覆。
    /// 备份失败（文件被杀软/备份工具锁住等）不阻塞降级路径：原文件原地保留，
    /// 拒载事实已由 <see cref="LastLoadError"/> 承载；此时后续 Save 仍会写主文件（用户显式保存意图优先）。
    /// </summary>
    private string? BackupCorruptFile(string path)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var backup = $"{path}.corrupt-{stamp}";
            for (var n = 1; File.Exists(backup); n++)
            {
                backup = $"{path}.corrupt-{stamp}-{n}";
            }

            File.Move(path, backup);
            PruneCorruptBackups(path);
            return backup;
        }
        catch (Exception moveEx)
        {
            // 尽力而为：备份失败不改变默认值降级路径，也不吞掉拒载事实（LastLoadError 已置）。
            System.Diagnostics.Debug.WriteLine($"SettingsService: corrupt settings backup failed: {moveEx.Message}");
            return null;
        }
    }

    /// <summary>
    /// R4-γ（S-LOW-2）：保留最近 <see cref="MaxCorruptBackups"/> 份 corrupt 备份，删除更早的。
    /// 文件名内嵌 UTC 时间戳（<c>yyyyMMdd'T'HHmmssfff'Z'</c>），字典序即时间序，排序零成本。
    /// 尽力而为：清理失败不影响备份结果或降级路径。
    /// </summary>
    private static void PruneCorruptBackups(string settingsPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(settingsPath);
            if (dir is null) return;

            var all = Directory.GetFiles(dir, Path.GetFileName(settingsPath) + ".corrupt-*");
            if (all.Length <= MaxCorruptBackups) return;

            // 字典序降序 = 最新在前；保留前 MaxCorruptBackups 个，删除其余。
            var ordered = all.OrderByDescending(f => Path.GetFileName(f)).ToArray();
            for (var i = MaxCorruptBackups; i < ordered.Length; i++)
            {
                try { File.Delete(ordered[i]); }
                catch { /* 尽力而为 */ }
            }
        }
        catch
        {
            // 清理失败不改变备份结果或降级路径。
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        // 原子写（与 moa-options/permissions/profiles 三个存储同策略）：
        // 随机临时名 + Move 覆盖。直接 WriteAllText 写到一半进程崩溃/断电
        // 会留下半截文件，下次 Load 只能回退默认 → 用户 provider 配置静默全丢。
        var tmp = $"{_paths.SettingsFile}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        File.Move(tmp, _paths.SettingsFile, overwrite: true);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>获取 AIOptions,直接喂给 ProviderFactory。</summary>
    public AIOptions ToAiOptions()
    {
        var ai = Current.Ai;
        // 兜底:一个 provider 都未配置时，给出可直接使用的 DeepSeek 默认配置
        // （配上 DEEPSEEK_API_KEY 环境变量即可连通，不是占位假配置）。
        if (ai.Providers.Count == 0)
        {
            ai.Providers.Add(new ProviderConfig
            {
                Id = "deepseek", DisplayName = "DeepSeek V4",
                Kind = "OpenAICompatible", BaseUrl = "https://api.deepseek.com/v1",
                DefaultModel = ai.DefaultModel, ApiKeyEnvVar = "DEEPSEEK_API_KEY"
            });
        }
        return new AIOptions
        {
            DefaultProviderId = ai.DefaultProviderId,
            DefaultModel = ai.DefaultModel,
            // 深拷贝快照：运行时只应看见"最近一次保存时点的配置"。
            // 共享活引用会让设置页未保存的编辑（如 BaseUrl）泄漏进 provider 发出的请求，
            // 违背热重载契约（保存 → Reload 才改变运行时）。
            Providers = ai.Providers.Select(Copy).ToList()
        };
    }

    private static ProviderConfig Copy(ProviderConfig p) => new()
    {
        Id = p.Id,
        DisplayName = p.DisplayName,
        Kind = p.Kind,
        BaseUrl = p.BaseUrl,
        DefaultModel = p.DefaultModel,
        ApiKeyEnvVar = p.ApiKeyEnvVar,
        RequiresApiKey = p.RequiresApiKey,
        SupportsStreaming = p.SupportsStreaming,
        SupportsToolCalling = p.SupportsToolCalling,
        SupportsThinking = p.SupportsThinking,
        ThinkingEfforts = p.ThinkingEfforts,
        TimeoutSeconds = p.TimeoutSeconds,
        ExtraHeaders = p.ExtraHeaders is null
            ? null
            : new Dictionary<string, string>(p.ExtraHeaders, StringComparer.Ordinal),
        ExtraBody = p.ExtraBody is null
            ? null
            : new Dictionary<string, object>(p.ExtraBody, StringComparer.Ordinal),
        // B6 版本 pinning（R2 缝合 #20）：深拷贝必须带上 ApiVersionHeaders——
        // 此前 Copy 漏拷该字段，settings.json 配置的 pin 在 ToAiOptions 快照后被静默丢弃，
        // provider 侧（VendorVersionPin.From）永远看不到（注册了≠触达，F-H1 教训）。
        ApiVersionHeaders = p.ApiVersionHeaders is null
            ? null
            : new Dictionary<string, string>(p.ApiVersionHeaders, StringComparer.Ordinal),
    };

    private static AppSettings CreateDefaults()
    {
        var s = new AppSettings();
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek V4 (default)",
            Kind = "OpenAICompatible", BaseUrl = "https://api.deepseek.com/v1",
            DefaultModel = "deepseek-v4-flash", ApiKeyEnvVar = "DEEPSEEK_API_KEY"
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "minimax", DisplayName = "MiniMax M2 (minimaxi.com)",
            Kind = "OpenAICompatible", BaseUrl = "https://api.minimaxi.com/v1",
            DefaultModel = "MiniMax-M2", ApiKeyEnvVar = "MINIMAX_API_KEY",
            // reasoning_split=true: 把 thinking 分离到 reasoning_content 字段, content 是干净输出
            ExtraBody = new System.Collections.Generic.Dictionary<string, object>
            {
                ["reasoning_split"] = true
            }
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "qwen", DisplayName = "Qwen (DashScope)",
            Kind = "OpenAICompatible", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            DefaultModel = "qwen3-max", ApiKeyEnvVar = "DASHSCOPE_API_KEY"
        });
        s.Ai.Providers.Add(new ProviderConfig
        {
            Id = "ollama", DisplayName = "Ollama (local)",
            Kind = "OpenAICompatible", BaseUrl = "http://localhost:11434/v1",
            DefaultModel = "qwen2.5:7b", RequiresApiKey = false
        });
        return s;
    }
}
