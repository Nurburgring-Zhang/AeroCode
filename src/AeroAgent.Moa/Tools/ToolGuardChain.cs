// Copyright (c) AeroCode
// ToolGuardChain — 工具前置守卫复合链（批次 B G3 契约）。builder-β 实现各守卫。
// 语义与 ToolRouter.preCheck 一致：返回 null=无意见（走正常策略）；返回非 null=采用
// （守卫只能更审慎，ToolRouter 已保证 Allow 不会越过策略的 Deny/Ask）。
// 审计修复（Reviewer-S 1a）：链语义从"首个非 Allow 短路"改为全链扫描取最审慎裁决。
// why：短路会让后置守卫的 Deny 被前置守卫的 Ask 掩蔽——doom-loop 第 3 次重复升 Ask 曾
// 掩蔽 SensitiveFileGuard 对 .env 的 Deny，急停期 CommandClassifier 的 Ask 曾掩蔽
// EstopGuard 的 Deny；Ask 经弹窗即可被用户放行（或 advisor 自动采纳），两条不变量
// （敏感文件零泄露、全链路急停）均可被击穿。现按审慎度合成：Deny(2) > Ask(1) >
// null/Allow(0)，与装配顺序无关。不变量：任何守卫的 Deny 永远胜出，绝不被降格为
// 可放行的 Ask。全链总是执行（守卫均为无副作用纯裁决，DoomLoop/Estop 的状态推进
// 因此也与顺序无关）。
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

/// <summary>单个前置守卫。实现必须无副作用、线程安全（MOA 并行 worker 并发 Check）。</summary>
public interface IToolGuard
{
    /// <summary>守卫名（审计与日志）。</summary>
    string Name { get; }

    /// <summary>裁决；null = 不发表意见。</summary>
    PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args);
}

/// <summary>
/// 守卫链：全链扫描取最审慎裁决（Deny > Ask > null/Allow），与装配顺序无关——
/// 任何守卫的 Deny 永远胜出，不会被先到的 Ask 短路掩蔽。全部守卫弃权或放行时
/// 返回 null（交还策略）。全部守卫总是被执行（均无副作用）。
/// </summary>
public sealed class ToolGuardChain
{
    private readonly IReadOnlyList<IToolGuard> _guards;

    public ToolGuardChain(IReadOnlyList<IToolGuard> guards)
    {
        _guards = guards ?? throw new ArgumentNullException(nameof(guards));
    }

    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        PermissionDecision? verdict = null;
        foreach (var guard in _guards)
        {
            var d = guard.Check(toolName, args);
            if (d == PermissionDecision.Deny)
            {
                verdict = PermissionDecision.Deny; // 最高审慎度；继续扫描以保持全链状态推进一致
            }
            else if (d == PermissionDecision.Ask && verdict is null)
            {
                verdict = PermissionDecision.Ask; // Deny 已在手的绝不降格回 Ask
            }
        }

        return verdict;
    }
}
