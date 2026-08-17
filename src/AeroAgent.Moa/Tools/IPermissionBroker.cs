using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

/// <summary>
/// 交互式授权代理：策略判定为 Ask 时，由它向用户要一个决定（App 层实现为
/// 真实 XAML 授权对话框，等待用户点击允许/拒绝；无界面场景可实现为默认拒绝）。
/// 不允许静默放行——拿不到用户决定时必须返回 Deny。
/// </summary>
public interface IPermissionBroker
{
    /// <summary>
    /// 为一次工具调用征求用户授权。
    /// </summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="args">物化后的参数（供对话框展示"将要执行什么"），可为 null。</param>
    /// <param name="ct">取消令牌（对话轮取消时不应继续弹窗）。</param>
    /// <returns>只允许返回 Allow 或 Deny；返回 Ask 视为 Deny。</returns>
    ValueTask<PermissionDecision> ResolveAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct);
}
