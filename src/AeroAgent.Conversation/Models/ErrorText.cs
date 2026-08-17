namespace AeroAgent.Conversation.Models;

/// <summary>
/// 错误文本规范化。provider 异常消息可能携带整页 HTML/超长堆栈，
/// 落库与展示前统一截断——保留前 <see cref="MaxLength"/> 字符足以定位问题，
/// 避免单条消息行膨胀拖累 SQLite 读取与 UI 渲染。
/// </summary>
public static class ErrorText
{
    public const int MaxLength = 2000;

    public static string? Truncate(string? error)
    {
        if (error is null)
        {
            return null;
        }

        return error.Length <= MaxLength
            ? error
            : string.Concat(error.AsSpan(0, MaxLength), "…（已截断）");
    }
}
