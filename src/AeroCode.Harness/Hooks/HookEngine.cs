// Copyright (c) AeroCode
// HookEngine — 用户可配事件钩子（批次 B G4 契约钉死）。builder-γ 实现加载与执行。
// 对标 claude-code hooks / goose hooks.json：EventBus 事件 → 匹配 → 执行命令（真实进程）。
// 失败语义：单钩子失败不阻塞主流程，结果 Publish HookExecutedEvent（诚实留痕）。
using System.Text.Json;

namespace AeroCode.Harness.Hooks;

/// <summary>一条钩子配置（hooks.json 数组元素）。</summary>
public sealed class HookDef
{
    public string Id { get; init; } = string.Empty;
    /// <summary>订阅的事件名（如 "ToolCallEvent"/"ToolResultEvent"/"SessionEndEvent"）。</summary>
    public string Event { get; init; } = string.Empty;
    /// <summary>可选匹配：事件 JSON 含此子串才触发（轻量过滤，不引入完整表达式）。</summary>
    public string? Match { get; init; }
    /// <summary>要执行的命令（经 shell 真实执行；事件 JSON 以 stdin 传入）。</summary>
    public string Command { get; init; } = string.Empty;
    public int TimeoutSec { get; init; } = 30;
    /// <summary>false = 加载后被禁用（保留配置）。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// 钩子引擎。实现约束：
/// 1. hooks.json 位于 AppData/AeroCode/hooks.json；坏 JSON/缺字段 → 拒载全部并 log 警告（fail-safe，不半载）；
/// 2. 事件 JSON 以 stdin 传给命令；stdout/stderr 各 50KB 截断；
/// 3. 超时杀进程树；退出码非零=失败（HookExecutedEvent.Success=false）；
/// 4. 命令执行不受工具权限层约束（它不是模型发起的调用），但 must 不递归触发自身（HookExecutedEvent 不进钩子分发）。
/// </summary>
public interface IHookEngine
{
    /// <summary>已加载的钩子（快照）。</summary>
    IReadOnlyList<HookDef> Hooks { get; }

    /// <summary>从磁盘加载配置（启动时与设置页刷新时调用）。返回加载条数；失败抛 InvalidDataException。</summary>
    int LoadFrom(string path);

    /// <summary>分发一个事件（EventBus 订阅端调用；事件名+事件 JSON）。异步派发不等待。</summary>
    void Dispatch(string eventName, string eventJson);
}
