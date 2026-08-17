using AeroCode.AI.Models;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// 面向 worker 的一个工具域。一个工具箱 = 一个来源域（内建笔记、Skill、
/// 一台 MCP server……），对外暴露一组 <see cref="ToolDefinition"/> 并按名执行调用。
/// 实现必须真实执行（真实 DB / 真实进程 / 真实 HTTP），禁止 mock/stub；
/// 域内失败不抛异常，以 <see cref="ToolInvokeResult.Fail"/> 如实返回。
/// </summary>
public interface IWorkerToolbox
{
    /// <summary>域名（日志与审计展示用；工具名唯一性由 <see cref="ToolboxRegistry"/> 全局保证）。</summary>
    string Domain { get; }

    /// <summary>本域暴露的工具定义（名称/描述/参数 JSON Schema）。</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>
    /// 执行一次工具调用。
    /// </summary>
    /// <param name="toolName">工具名（必须属于本域 <see cref="Definitions"/>）。</param>
    /// <param name="argumentsJson">模型给出的参数 JSON（原样透传，由域自行解析与校验）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ToolInvokeResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken ct);
}
