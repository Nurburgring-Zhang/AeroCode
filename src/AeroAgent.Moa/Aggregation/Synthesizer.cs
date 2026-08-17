using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Accounting;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Strategies;
using AiChatMessage = AeroCode.AI.Models.ChatMessage;

namespace AeroAgent.Moa.Aggregation;

/// <summary>子任务结果条目（送聚合模型前的结构化输入）。</summary>
public sealed record SubtaskResult(string Title, bool Succeeded, string? Error, string Content);

/// <summary>
/// 聚合器：把多个子任务/候选答案合成为最终答复。
/// 诚实原则：失败的子任务如实标注进入合成输入，合成提示词要求聚合模型
/// 明确说明缺口，绝不假装所有子任务都成功。
/// </summary>
public sealed class Synthesizer
{
    /// <summary>候选标签字母表（集成策略的消息标签与提示词共用，单一来源）。</summary>
    public const string CandidateLabels = "ABCDEFGH";

    private readonly WorkerRunner _runner;

    public Synthesizer(WorkerRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>合成分工子任务结果（Decompose 用）。失败子任务如实列出。</summary>
    public static string BuildDecomposePrompt(string goal, IReadOnlyList<SubtaskResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("用户的原始目标：");
        sb.AppendLine(goal);
        sb.AppendLine();
        sb.AppendLine("以下是各子任务的执行结果（由不同模型分工完成）：");
        var index = 0;
        foreach (var r in results)
        {
            index++;
            sb.AppendLine();
            sb.AppendLine($"### 子任务 {index}：{r.Title}");
            if (r.Succeeded)
            {
                sb.AppendLine(r.Content.Length > 0 ? r.Content : "（该子任务未产出内容）");
            }
            else
            {
                sb.AppendLine($"【失败】{r.Error ?? "未知错误"}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            "请把以上结果合成为一份完整、连贯的最终答复。要求：\n" +
            "1. 直接给出最终答复正文，不要复述\"子任务\"结构；\n" +
            "2. 若有子任务失败，必须在答复中如实说明哪部分缺失及原因，不得虚构其结果；\n" +
            "3. 若所有子任务都失败，如实报告无法完成并给出原因。");
        return sb.ToString();
    }

    /// <summary>合成集成候选答案（Ensemble 用）。</summary>
    public static string BuildEnsemblePrompt(string question, IReadOnlyList<SubtaskResult> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("用户问题：");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("以下是多个模型各自独立给出的候选答案：");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.AppendLine();
            sb.AppendLine($"### 候选 {c.Title}");
            if (c.Succeeded)
            {
                sb.AppendLine(c.Content.Length > 0 ? c.Content : "（未产出内容）");
            }
            else
            {
                sb.AppendLine($"【该候选失败】{c.Error ?? "未知错误"}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            "你是裁决者。请综合以上候选给出最终答案。要求：\n" +
            "1. 候选一致时直接给出结论；存在分歧时指出分歧点并给出你认为最正确的答案及理由；\n" +
            "2. 不得虚构候选中没有的事实依据；\n" +
            "3. 若所有候选都失败，如实报告。");
        return sb.ToString();
    }

    /// <summary>执行合成调用（流式，作为面向用户的最终答复）。</summary>
    public Task<WorkerOutcome> SynthesizeAsync(
        OrchestrationContext ctx,
        ModelAssignment assignment,
        StrategyRole role,
        string? parentMessageId,
        string label,
        string prompt,
        bool isFinal,
        TurnBudget? budget,
        ChannelWriter<ChatEvent>? sink,
        CancellationToken ct)
    {
        var messages = new List<AiChatMessage> { new() { Role = "user", Content = prompt } };
        return _runner.RunAsync(
            ctx, assignment, role, parentMessageId, label,
            messages, stream: true, isFinal, sink, budget, ct);
    }
}
