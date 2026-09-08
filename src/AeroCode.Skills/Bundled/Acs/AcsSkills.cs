// Copyright (c) AeroCode
// ACS 技能束 — Agent Core Suite v2.3.0 六个工程技能的 AeroCode 封装。
// 每个技能以内嵌 SKILL.md 为系统提示词（忠实移植 ACS 原文），
// ExecuteAsync 返回技能正文供 agent 注入上下文（技能本身是纪律 SOP，不产生副作用）。
using System.Reflection;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Acs;

/// <summary>ACS 技能基类：从内嵌资源加载 SKILL.md 作为系统提示词。</summary>
public abstract class AcsSkillBase : ISkill
{
    /// <summary>内嵌资源名后缀（如 "acs-universal-task-code.md"）。</summary>
    protected abstract string ResourceName { get; }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public string Category => "acs";

    /// <inheritdoc />
    public string Author => "Agent Core Suite v2.3.0";

    /// <inheritdoc />
    public string Version => "2.3.0";

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Tags { get; }

    private string? _cachedPrompt;

    /// <inheritdoc />
    public string GetSystemPrompt()
    {
        if (_cachedPrompt is not null) return _cachedPrompt;
        var asm = typeof(AcsSkillBase).Assembly;
        var fullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase));
        if (fullName is null)
        {
            _cachedPrompt = $"[ACS skill resource not found: {ResourceName}]";
            return _cachedPrompt;
        }

        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream is null)
        {
            _cachedPrompt = $"[ACS skill resource stream null: {ResourceName}]";
            return _cachedPrompt;
        }

        using var reader = new StreamReader(stream);
        _cachedPrompt = reader.ReadToEnd();
        return _cachedPrompt;
    }

    /// <inheritdoc />
    public bool IsAvailable() => true;

    /// <inheritdoc />
    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        // ACS 技能是纪律 SOP：执行 = 返回技能正文供 agent 注入上下文，无副作用。
        return Task.FromResult(new SkillResult
        {
            Text = GetSystemPrompt(),
            Success = true,
            NextActions = new[] { "将本技能纪律应用于当前任务执行" },
        });
    }
}

/// <summary>ACS 通用任务执行主入口（T0-T3 分级 + 七道门 + 成本闸门）。</summary>
public sealed class UniversalTaskCodeSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-universal-task-code.md";

    /// <inheritdoc />
    public override string Id => "acs/universal-task-code";

    /// <inheritdoc />
    public override string Name => "ACS 通用任务执行";

    /// <inheritdoc />
    public override string Description => "T0-T3 分级 + P0-P6 七道门 + 成本闸门 + 有界重试的任务执行 SOP。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "task", "sop", "gates" };
}

/// <summary>ACS Loop 层工程（何时继续、何时停止）。</summary>
public sealed class LoopEngineeringSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-loop-engineering.md";

    /// <inheritdoc />
    public override string Id => "acs/loop-engineering";

    /// <inheritdoc />
    public override string Name => "ACS Loop 工程";

    /// <inheritdoc />
    public override string Description => "停止协议 + 四道成本闸门 + 有界重试 + 安全阀的循环控制。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "loop", "cost-gate" };
}

/// <summary>ACS Graph 层工程（任务 DAG、节点契约、边门、汇聚语义）。</summary>
public sealed class GraphEngineeringSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-graph-engineering.md";

    /// <inheritdoc />
    public override string Id => "acs/graph-engineering";

    /// <inheritdoc />
    public override string Name => "ACS Graph 工程";

    /// <inheritdoc />
    public override string Description => "任务 DAG 建模、节点契约、fan-out/fan-in、边门、模型分层。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "graph", "dag" };
}

/// <summary>ACS 自验证扩展（N 候选 + 独立验证器排序）。</summary>
public sealed class SelfVerifyScalingSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-self-verify-scaling.md";

    /// <inheritdoc />
    public override string Id => "acs/self-verify-scaling";

    /// <inheritdoc />
    public override string Name => "ACS 自验证扩展";

    /// <inheritdoc />
    public override string Description => "N 候选 + 1-20 细粒度评分 + pivot 排序 + Builder/Verifier 分离。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "verify", "ranking" };
}

/// <summary>ACS Context 层省 token 工程。</summary>
public sealed class TokenThriftSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-token-thrift.md";

    /// <inheritdoc />
    public override string Id => "acs/token-thrift";

    /// <inheritdoc />
    public override string Name => "ACS 省 token 工程";

    /// <inheritdoc />
    public override string Description => "渐进披露、状态外置、稳定前缀、压缩交接的上下文节流。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "context", "token" };
}

/// <summary>ACS 终端原生能力路由。</summary>
public sealed class QoderNativeIntegrationSkill : AcsSkillBase
{
    /// <inheritdoc />
    protected override string ResourceName => "acs-qoder-native-integration.md";

    /// <inheritdoc />
    public override string Id => "acs/qoder-native-integration";

    /// <inheritdoc />
    public override string Name => "ACS 终端原生集成";

    /// <inheritdoc />
    public override string Description => "终端原生机制路由（子代理/MCP/记忆/多会话/计划模式）与实证纪律。";

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => new[] { "acs", "native", "integration" };
}
