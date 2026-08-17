namespace AeroAgent.Moa.Tools;

/// <summary>一次工具执行的结果。Output 是交还给模型的文本（真实输出或真实错误说明）。</summary>
/// <param name="Success">true = 真实执行成功。</param>
/// <param name="Output">交还模型的正文（成功=真实输出；失败/拒绝=诚实原因）。</param>
/// <param name="Error">失败/拒绝时的原因（成功为 null）。</param>
/// <param name="Denied">true = 被授权策略拒绝（区别于执行失败）。</param>
public sealed record ToolInvokeResult(bool Success, string Output, string? Error = null, bool Denied = false)
{
    /// <summary>真实执行成功。</summary>
    public static ToolInvokeResult Ok(string output) => new(true, output);

    /// <summary>执行失败（未知工具/域内报错/参数非法），原因如实交还模型。</summary>
    public static ToolInvokeResult Fail(string error) => new(false, error, error);

    /// <summary>被授权策略拒绝，原因如实交还模型。</summary>
    public static ToolInvokeResult Deny(string reason) => new(false, reason, reason, Denied: true);
}
