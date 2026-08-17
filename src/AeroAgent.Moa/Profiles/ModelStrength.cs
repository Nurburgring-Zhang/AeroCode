namespace AeroAgent.Moa.Profiles;

/// <summary>
/// 模型强项标签。字符串常量（而非 enum）以便用户扩展自定义标签。
/// 路由分类、子任务分配、画像匹配共用这套词汇。
/// </summary>
public static class ModelStrength
{
    public const string General = "general";
    public const string Code = "code";
    public const string Writing = "writing";
    public const string Analysis = "analysis";
    public const string Translation = "translation";
    public const string Math = "math";
    public const string Planning = "planning";
    public const string Review = "review";

    /// <summary>全部内建强项（UI 展示/分类提示词用）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        General, Code, Writing, Analysis, Translation, Math, Planning, Review,
    };

    /// <summary>规范化标签：小写去空白；未知原样返回（支持自定义标签）。</summary>
    public static string Normalize(string? strength) =>
        string.IsNullOrWhiteSpace(strength) ? General : strength.Trim().ToLowerInvariant();
}

/// <summary>速度档位（影响路由偏好：router/judge 偏好 Fast）。</summary>
public enum SpeedTier
{
    Medium = 0,
    Fast = 1,
    Slow = 2,
}
