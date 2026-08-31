// Copyright (c) AeroCode
// PlanToolbox — write_plan 工具域（IWorkerToolbox 实现）。
// 权限裁决链上：Default/AcceptEdits/Bypass 档由规则表显式 Deny（CreateDefault），
// Plan 档由 PermissionModeTransform.PlanReadOnlyTools 白名单放行——工具自身只管真实落盘。
using System.Text.Json;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Models;
using AeroCode.Harness.PlanMode;

namespace AeroAgent.Moa.Tools.Workspace;

/// <summary>计划文件写入工具域（仅在 Plan 档被策略放行）。</summary>
public sealed class PlanToolbox : IWorkerToolbox
{
    private readonly PlanWorkflow _workflow;

    public PlanToolbox(PlanWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    public string Domain => "plan";

    public IReadOnlyList<ToolDefinition> Definitions { get; } = new[]
    {
        new ToolDefinition
        {
            Name = "write_plan",
            Description = "Write the full plan document (PLAN.md). Only usable in plan mode; approving the plan switches back to build mode.",
            ParametersJsonSchema = """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""",
        },
    };

    public Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        if (toolName != "write_plan")
        {
            return Task.FromResult(ToolInvokeResult.Fail($"Unknown plan tool '{toolName}'"));
        }

        string? content;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            content = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolInvokeResult.Fail($"Invalid arguments JSON: {ex.Message}"));
        }

        if (content is null)
        {
            return Task.FromResult(ToolInvokeResult.Fail("write_plan requires 'content' (string)"));
        }

        try
        {
            _workflow.WritePlan(content);
            return Task.FromResult(ToolInvokeResult.Ok(
                $"Plan written ({content.Length:N0} chars). Awaiting user approval."));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ToolInvokeResult.Fail(ex.Message));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolInvokeResult.Fail($"IO error: {ex.Message}"));
        }
    }
}
