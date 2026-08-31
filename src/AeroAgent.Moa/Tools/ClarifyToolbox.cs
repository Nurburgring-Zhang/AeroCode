// Copyright (c) AeroCode
// ClarifyToolbox — question 工具域（批次 B G1）：结构化追问。
// Clarification 域真实实现在 AeroAgent.Autonomy（ClarificationGate），而 Autonomy 引用
// Moa（Mission/MOA 执行链），Moa 不能反向引用——因此按本工程既有的依赖倒置模式
// （ISessionService 同款）定义端口 <see cref="IClarificationPort"/>：组合根（App，B2 接线）
// 用 Autonomy 的 ClarificationGate 适配。弹窗呈现经 <see cref="IClarificationPresenter"/>
// 端口（G5 挂真实 UI；未接入时诚实降级并在输出中显式标注）。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroAgent.Moa.Tools;

/// <summary>一次澄清评估的结构化结果（端口层投影，不泄漏 Autonomy 类型）。</summary>
/// <param name="AmbiguityScore">综合歧义度（0=完全明确，1=高度模糊）。</param>
/// <param name="RequiresClarification">是否需要澄清。</param>
/// <param name="Questions">针对性澄清问题（0..3 条）。</param>
/// <param name="Source">产出来源（Heuristic / Llm，可读字符串）。</param>
public sealed record ClarifyEvaluation(
    double AmbiguityScore,
    bool RequiresClarification,
    IReadOnlyList<string> Questions,
    string Source);

/// <summary>
/// 澄清域端口：把问题文本交给真实 Clarification 门评估（生产实现 = 组合根适配
/// Autonomy.ClarificationGate；测试可注入真实门的无 LLM 确定性路径）。
/// </summary>
public interface IClarificationPort
{
    Task<ClarifyEvaluation> EvaluateAsync(string question, CancellationToken ct);
}

/// <summary>
/// 澄清弹窗呈现端口（G5 挂真实 UI）。返回用户的回答文本；null = 弹窗被关闭/未回应。
/// </summary>
public interface IClarificationPresenter
{
    ValueTask<string?> PresentAsync(string question, CancellationToken ct);
}

/// <summary>
/// question 工具域：把模型的追问转发到真实 Clarification 域做结构化评估，
/// 需要澄清时经呈现端口弹窗向用户征求回答。
/// 诚实语义：评估失败=Fail；弹窗超时/被关闭=Fail（不伪造用户回答）；
/// 无呈现端口=结构化问题照常返回，但输出显式标注 [DEGRADED]（UI 未接入）。
/// </summary>
public sealed class ClarifyToolbox : IWorkerToolbox
{
    /// <summary>等待用户回答的默认超时。</summary>
    public const int DefaultAnswerTimeoutSeconds = 120;

    private readonly IClarificationPort _port;
    private readonly IClarificationPresenter? _presenter;
    private readonly TimeSpan _answerTimeout;
    private readonly ILogger<ClarifyToolbox> _logger;
    private readonly IReadOnlyList<ToolDefinition> _definitions;

    public ClarifyToolbox(
        IClarificationPort port,
        IClarificationPresenter? presenter = null,
        int answerTimeoutSeconds = DefaultAnswerTimeoutSeconds,
        ILogger<ClarifyToolbox>? logger = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _presenter = presenter;
        _answerTimeout = TimeSpan.FromSeconds(Math.Clamp(answerTimeoutSeconds, 1, 3600));
        _logger = logger ?? NullLogger<ClarifyToolbox>.Instance;
        _definitions = new List<ToolDefinition>
        {
            new()
            {
                Name = "question",
                Description = "Ask the user a structured clarifying question (evaluated by the real clarification gate; " +
                              "may surface a dialog and wait for the user's answer). " +
                              "Args: {\"question\": string (required)}.",
                ParametersJsonSchema = """{"type":"object","properties":{"question":{"type":"string"}},"required":["question"]}""",
            },
        };
    }

    /// <inheritdoc/>
    public string Domain => "clarify";

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <inheritdoc/>
    public async Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);
            if (toolName != "question")
            {
                return ToolInvokeResult.Fail($"Unknown clarify tool '{toolName}'");
            }

            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object ||
                !args.TryGetProperty("question", out var qEl) ||
                qEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(qEl.GetString()))
            {
                return ToolInvokeResult.Fail("question requires a non-empty 'question' string argument");
            }

            var question = qEl.GetString()!;

            // ---- 转发真实 Clarification 域评估 ----
            var evaluation = await _port.EvaluateAsync(question, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.AppendLine($"Clarification gate: ambiguity={evaluation.AmbiguityScore:F2}, source={evaluation.Source}.");
            if (!evaluation.RequiresClarification || evaluation.Questions.Count == 0)
            {
                sb.AppendLine("No clarification required — the task is sufficiently unambiguous.");
                return ToolInvokeResult.Ok(sb.ToString().TrimEnd());
            }

            sb.AppendLine($"Structured questions ({evaluation.Questions.Count}):");
            for (var i = 0; i < evaluation.Questions.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {evaluation.Questions[i]}");
            }

            if (_presenter is null)
            {
                // 诚实降级（显式标注）：弹窗 UI 尚未接入（G5 接线），结构化问题照常返回。
                sb.AppendLine("[DEGRADED] Clarification dialog not wired (no IClarificationPresenter); " +
                              "returning structured questions only, no user answer collected.");
                return ToolInvokeResult.Ok(sb.ToString().TrimEnd());
            }

            // ---- 弹窗征求用户回答；超时/关闭 = 诚实失败 ----
            var topQuestion = evaluation.Questions[0];
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_answerTimeout);
            try
            {
                var answer = await _presenter.PresentAsync(topQuestion, timeoutCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return ToolInvokeResult.Fail(
                        "Clarification dismissed: user closed the dialog without answering (honest failure, no fabricated answer).");
                }

                sb.AppendLine($"User answer to \"{topQuestion}\": {answer}");
                return ToolInvokeResult.Ok(sb.ToString().TrimEnd());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 仅呈现超时（外层 ct 未取消）：诚实失败，不静默继续。
                return ToolInvokeResult.Fail(
                    $"Clarification timed out after {_answerTimeout.TotalSeconds:F0}s waiting for the user's answer (honest failure).");
            }
        }
        catch (JsonException ex)
        {
            return ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ToolInvokeResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw; // 会话级取消如实上抛（WorkerRunner 语义）
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DEGRADED] clarification gate failed: {Error}", ex.Message);
            return ToolInvokeResult.Fail($"clarification failed: {ex.Message}");
        }
    }
}
