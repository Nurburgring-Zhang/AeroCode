// Copyright (c) AeroCode V3.0
// SkillCreator — auto-creates a SKILL.md after complex successful task (Hermes learning loop).
using AeroCode.Skills.Loader;
using AeroCode.Skills.Models;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.AutoCreate;

/// <summary>
/// Trigger conditions (Hermes hard rules):
///   1. Task complex (>= 5 tool calls) AND succeeded
///   2. Task had errors / dead ends AND a viable path was found
///   3. User corrected the agent
///   4. Non-obvious workflow discovered
/// </summary>
public sealed class SkillCreator
{
    private readonly SkillRegistry _registry;
    private readonly string _userSkillsRoot;
    private const int MinToolCallsForAutoCreate = 5;
    private const double MinSuccessRate = 0.6;

    public SkillCreator(SkillRegistry registry, string userSkillsRoot)
    {
        _registry = registry;
        _userSkillsRoot = userSkillsRoot;
    }

    /// <summary>
    /// Decide whether to auto-create a skill from a completed task.
    /// Returns null if no auto-create should happen.
    /// </summary>
    public Skill? TryCreate(AutoCreateCandidate candidate)
    {
        if (candidate.ToolCallCount < MinToolCallsForAutoCreate)
            return null;

        if (!candidate.Succeeded)
            return null;

        // 安全闸门：SuggestedId 来自模型输出，直接拼路径会被 ../ 穿越出技能根目录。
        // 清洗为 [a-z0-9-]（层级 / 转为 -），清洗后为空则拒绝创建。
        var safeId = SanitizeId(candidate.SuggestedId);
        if (string.IsNullOrEmpty(safeId))
            return null;

        // If a similar skill already exists, do not create a duplicate.
        if (_registry.Get(safeId) is not null)
            return null;

        var skill = new Skill
        {
            Id = safeId,
            Name = candidate.SuggestedName,
            Description = TrimDescription(candidate.SuggestedDescription),
            Version = "0.1.0",
            Author = "AeroCode (auto-created)",
            License = "MIT",
            Tags = candidate.Tags,
            Body = candidate.SuggestedBody,
            // Write under <userSkillsRoot>/skills/<id>/SKILL.md so that DeriveId
            // (which looks for a "skills" ancestor) returns the right hierarchical id.
            SourcePath = Path.Combine(_userSkillsRoot, "skills", safeId, "SKILL.md"),
            Category = "user",
            AutoCreated = true,
            LastModifiedUtc = DateTime.UtcNow,
        };

        // 纵深防御：落盘前再验证目标路径仍在技能根目录内。
        var rootFull = Path.GetFullPath(Path.Combine(_userSkillsRoot, "skills"));
        var destFull = Path.GetFullPath(skill.SourcePath);
        if (!destFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var path = Path.GetDirectoryName(skill.SourcePath);
            if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            File.WriteAllText(skill.SourcePath, Serialize(skill));
        }
        catch
        {
            // 落盘失败（权限/路径/磁盘等任何原因）诚实返回 null，不注册半落盘状态。
            return null;
        }

        _registry.Register(new AutoCreatedSkillAdapter(skill));
        return skill;
    }

    /// <summary>单段清洗后的最大长度（超出部分截断）。</summary>
    public const int MaxSegmentLength = 64;

    /// <summary>层级 Id 的最大段数（超出整体拒绝，防止模型输出构造病态深路径）。</summary>
    public const int MaxSegments = 4;

    /// <summary>
    /// Windows 保留设备名（任意大小写，位于路径任意层级均非法，如 CON、NUL、COM1）。
    /// 注意 "con.txt" 这类带扩展名形式在 Windows 上同样是保留名，但本清洗器
    /// 会把 '.' 映射为 '-'（"con.txt" → "con-txt"），清洗后段内不再含 '.'，
    /// 因此只需对清洗后的整段做精确匹配即可覆盖。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 清洗技能 Id（按路径段，fail-closed 安全闸门）：
    /// 以 / 与 \ 分段，逐段清洗为 [a-z0-9-]（折叠连字符、去首尾），
    /// 清洗后为空的段（含 "." / ".." / 空段）整体丢弃，合法层级 Id（如
    /// "test/auto-skill"）原样保留，"../evil" 之类的穿越片段被归约为 "evil"。
    /// 三条硬拒绝（返回空串 → TryCreate 拒绝创建）：
    ///   1) 任一段清洗后命中 Windows 保留设备名（CON/NUL/COM1...， OrdinalIgnoreCase）；
    ///   2) 有效段数超过 <see cref="MaxSegments"/>（4）；
    ///   3) 清洗后整体为空。
    /// 单段超过 <see cref="MaxSegmentLength"/>（64）字符时确定性截断（截断后再去尾部连字符）。
    /// 即便本函数被绕过，落盘前的根目录 StartsWith 校验仍是最后一道防线。
    /// </summary>
    public static string SanitizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var segments = raw.Trim().Split('/', '\\');
        var safe = new System.Collections.Generic.List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var sb = new System.Text.StringBuilder(segment.Length);
            foreach (var ch in segment.Trim().ToLowerInvariant())
            {
                // 只保留 ASCII 字母数字：Unicode 同形字符（如西里尔 е）被映射为 '-'，
                // 杜绝视觉欺骗性的 Id 混淆。
                sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
            }
            var cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (cleaned.Length == 0) continue;

            // Windows 保留设备名：路径任意层级出现即整体拒绝（fail-closed），
            // 不做静默改写——模型输出含设备名本身就是可疑信号。
            if (ReservedDeviceNames.Contains(cleaned)) return string.Empty;

            if (cleaned.Length > MaxSegmentLength)
                cleaned = cleaned.Substring(0, MaxSegmentLength).TrimEnd('-');

            safe.Add(cleaned);
        }

        if (safe.Count == 0 || safe.Count > MaxSegments) return string.Empty;
        return string.Join("/", safe);
    }

    private static string TrimDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return string.Empty;
        desc = desc.Trim();
        if (!desc.EndsWith('.')) desc += ".";
        if (desc.Length > 60) desc = desc.Substring(0, 57) + "...";
        return desc;
    }

    private static string Serialize(Skill skill)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {skill.Name}");
        sb.AppendLine($"description: {skill.Description}");
        sb.AppendLine($"version: {skill.Version}");
        sb.AppendLine($"author: {skill.Author}");
        sb.AppendLine($"license: {skill.License}");
        if (skill.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", skill.Tags)}]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(skill.Body);
        return sb.ToString();
    }
}

/// <summary>Input to SkillCreator.TryCreate().</summary>
public sealed class AutoCreateCandidate
{
    public string SuggestedId { get; init; } = string.Empty;
    public string SuggestedName { get; init; } = string.Empty;
    public string SuggestedDescription { get; init; } = string.Empty;
    public string SuggestedBody { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public int ToolCallCount { get; init; }
    public bool Succeeded { get; init; }
    public double SuccessRate { get; init; } = 1.0;
}

/// <summary>Adapter that exposes a Skill (parsed from disk) as an ISkill.</summary>
internal sealed class AutoCreatedSkillAdapter : ISkill
{
    private readonly Skill _skill;
    public AutoCreatedSkillAdapter(Skill skill) { _skill = skill; }
    public string Id => _skill.Id;
    public string Name => _skill.Name;
    public string Description => _skill.Description;
    public string Category => _skill.Category;
    public string Author => _skill.Author;
    public string Version => _skill.Version;
    public IReadOnlyList<string> Tags => _skill.Tags;
    public string GetSystemPrompt() => $"# {_skill.Name}\n\n{_skill.Body}";
    public bool IsAvailable() => true;

    public Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        return Task.FromResult(new SkillResult
        {
            Text = $"Auto-created skill '{_skill.Id}' is declarative. Body:\n{_skill.Body}",
            Success = true,
        });
    }
}
