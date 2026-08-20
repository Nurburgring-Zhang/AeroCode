using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Autonomy.Llm;

/// <summary>
/// 一次自主内核 LLM 补全的结果。Content 为模型原始文本输出（调用方负责解析）。
/// </summary>
public sealed record LlmCompletion(
    string Content,
    string ProviderId,
    string ModelId,
    int PromptTokens,
    int CompletionTokens);

/// <summary>
/// 自主内核统一的 LLM 补全入口。把"选 provider → 组消息 → 真实调用 → 收容异常"
/// 收敛到一处，供 TaskAnalyzer / Steelman / Clarification / Planner / Verifier 复用。
///
/// 诚实降级约定：没有已配置 provider、或调用抛异常时，返回 <c>null</c> 并记录
/// <c>[DEGRADED]</c> 警告——调用方退回确定性启发式，绝不静默冒充 LLM 输出。
/// 本类只依赖 <see cref="IProviderRegistry"/> 抽象（生产为 ProviderFactory，测试可注入双替身）。
/// </summary>
public sealed class AutonomyLlmClient
{
    private readonly IProviderRegistry? _registry;
    private readonly ILogger<AutonomyLlmClient> _logger;

    /// <param name="registry">provider 注册表；null = 明确无 LLM（全程启发式 + [DEGRADED]）。</param>
    /// <param name="logger">日志；null 时用空日志。</param>
    public AutonomyLlmClient(IProviderRegistry? registry, ILogger<AutonomyLlmClient>? logger = null)
    {
        _registry = registry;
        _logger = logger ?? NullLogger<AutonomyLlmClient>.Instance;
    }

    /// <summary>是否存在至少一个已配置且可解析的 provider。</summary>
    public bool IsAvailable
    {
        get
        {
            if (_registry is null)
            {
                return false;
            }

            foreach (var id in _registry.ListConfiguredIds())
            {
                if (_registry.TryGetConfig(id, out var cfg) && !string.IsNullOrWhiteSpace(cfg.DefaultModel))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 执行一次非流式补全。system/user 双消息结构，低温采样以保证结构稳定。
    /// 无可用 provider 或调用失败时返回 null（调用方启发式兜底）。
    /// </summary>
    /// <param name="systemPrompt">系统提示（角色与输出格式约束）。</param>
    /// <param name="userPrompt">用户内容（任务文本等）。</param>
    /// <param name="temperature">采样温度（默认 0.2，结构化输出偏低）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<LlmCompletion?> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.2,
        CancellationToken ct = default)
    {
        if (_registry is null)
        {
            _logger.LogWarning("[DEGRADED] 未注入 provider 注册表，LLM 补全不可用，退回启发式。");
            return null;
        }

        var providerId = _registry.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(providerId) || !_registry.TryGetConfig(providerId, out var config))
        {
            _logger.LogWarning("[DEGRADED] 无已配置的默认 provider，LLM 补全不可用，退回启发式。");
            return null;
        }

        var model = config.DefaultModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("[DEGRADED] provider '{Provider}' 未配置默认模型，退回启发式。", providerId);
            return null;
        }

        IAiProvider provider;
        try
        {
            provider = _registry.Get(providerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] 解析 provider '{Provider}' 失败：{Error}，退回启发式。", providerId, ex.Message);
            return null;
        }

        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Temperature = temperature,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt },
            },
        };

        try
        {
            var response = await provider.ChatAsync(request, ct);
            var usage = response.Usage;
            return new LlmCompletion(
                response.Content ?? string.Empty,
                providerId,
                string.IsNullOrWhiteSpace(response.Model) ? model : response.Model,
                usage?.PromptTokens ?? 0,
                usage?.CompletionTokens ?? 0);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消如实上抛，不算降级。
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] LLM 补全调用失败：{Error}，退回启发式。", ex.Message);
            return null;
        }
    }
}
