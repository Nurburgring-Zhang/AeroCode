// Copyright (c) AeroCode
// AgentDefinitionLoader — agents 目录 *.md 声明式自定义 agent（批次 B G4，builder-γ）。
// 无 SKILL.md 依赖：不引用 Skills 的 SkillFrontmatter/Skill 模型，只复用仓库既有 YAML 解析栈
// （YamlDotNet — 与 Skills/SkillParser 同一依赖，frontmatter 显式 Alias 映射，同款切分方式）。
// 校验语义：缺必填字段 / 非法 YAML / 非法 maxTurns → 该文件拒绝并警告（目录内其余文件继续）；
// 重名（frontmatter name 冲突）→ 后者拒绝并警告（先到先得，文件名序确定性）。
using System.Text;
using YamlDotNet.Serialization;

namespace AeroCode.Harness.Agents;

/// <summary>一个声明式自定义 agent（agents/*.md frontmatter → 专家簇画像映射载体）。</summary>
/// <param name="Name">全局唯一名（frontmatter name，重名拒绝）。</param>
/// <param name="Description">用途描述（必填）。</param>
/// <param name="Model">偏好模型标识（必填；MOA 画像映射用）。</param>
/// <param name="Tools">允许工具名列表（可空 = 继承全量）。</param>
/// <param name="MaxTurns">最大轮数（&gt;=1；缺省 24）。</param>
/// <param name="SourcePath">来源 .md 绝对路径（诊断用）。</param>
public sealed record AgentDefinition(
    string Name,
    string Description,
    string Model,
    IReadOnlyList<string> Tools,
    int MaxTurns,
    string SourcePath);

/// <summary>目录加载结果：成功定义 + 全部拒绝原因/警告（诚实逐条，不静默丢弃）。</summary>
public sealed record AgentLoadResult(IReadOnlyList<AgentDefinition> Agents, IReadOnlyList<string> Warnings)
{
    public static readonly AgentLoadResult Empty = new(Array.Empty<AgentDefinition>(), Array.Empty<string>());
}

/// <summary>
/// 从 agents 目录加载声明式 agent 定义。每个 *.md 文件 = 一个 agent：
/// YAML frontmatter{name,description,model,tools[],maxTurns} + 可选正文（未来作系统提示补充）。
/// fail-safe 边界：单文件坏 → 拒该文件并警告；目录不存在 → 抛 DirectoryNotFoundException（显式配置错误）。
/// </summary>
public sealed class AgentDefinitionLoader
{
    /// <summary>缺省最大轮数（frontmatter 未写 maxTurns 时）。</summary>
    public const int DefaultMaxTurns = 24;

    private const string FrontmatterDelimiter = "---";

    // 与 Skills/SkillParser 同款策略：显式 Alias 映射（不赌命名约定），忽略未知字段。
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Action<string>? _warn;

    public AgentDefinitionLoader(Action<string>? warn = null)
    {
        _warn = warn;
    }

    /// <summary>加载目录下全部 *.md（文件名序，保证重名裁决确定性）。</summary>
    public AgentLoadResult LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"agents directory does not exist: {directory}");
        }

        var warnings = new List<string>();
        var agents = new List<AgentDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var definition = TryLoadFile(file, warnings);
            if (definition is null)
            {
                continue;
            }

            if (!seen.Add(definition.Name))
            {
                var conflict = $"agent '{definition.Name}' duplicate name rejected (kept earlier definition): {file}";
                warnings.Add(conflict);
                _warn?.Invoke(conflict);
                continue;
            }

            agents.Add(definition);
        }

        return new AgentLoadResult(agents, warnings);
    }

    private AgentDefinition? TryLoadFile(string file, List<string> warnings)
    {
        void Warn(string message)
        {
            warnings.Add(message);
            _warn?.Invoke(message);
        }

        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            Warn($"agent file unreadable: {file} — {ex.Message}");
            return null;
        }

        var parts = SplitFrontmatter(content);
        if (parts is null)
        {
            Warn($"agent file rejected: missing YAML frontmatter (expected opening --- on first line): {file}");
            return null;
        }

        AgentFrontmatter? fm;
        try
        {
            fm = Yaml.Deserialize<AgentFrontmatter>(parts.Value.Frontmatter);
        }
        catch (Exception ex)
        {
            Warn($"agent file rejected: invalid YAML frontmatter: {file} — {ex.Message}");
            return null;
        }

        if (fm is null)
        {
            Warn($"agent file rejected: frontmatter deserialized to null: {file}");
            return null;
        }

        // 必填字段：name / description / model（缺任一 → 拒绝并警告）
        if (string.IsNullOrWhiteSpace(fm.Name))
        {
            Warn($"agent file rejected: frontmatter missing required field 'name': {file}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(fm.Description))
        {
            Warn($"agent file rejected: frontmatter missing required field 'description': {file}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(fm.Model))
        {
            Warn($"agent file rejected: frontmatter missing required field 'model': {file}");
            return null;
        }

        // 可选字段：tools（缺省空=继承全量）；maxTurns（缺省 24；显式给出必须 >=1）
        if (fm.MaxTurns.HasValue && fm.MaxTurns.Value < 1)
        {
            Warn($"agent file rejected: frontmatter 'maxTurns' must be >= 1, got {fm.MaxTurns.Value}: {file}");
            return null;
        }

        var tools = (fm.Tools ?? new List<string>())
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();

        return new AgentDefinition(
            fm.Name.Trim(),
            fm.Description.Trim(),
            fm.Model.Trim(),
            tools,
            fm.MaxTurns ?? DefaultMaxTurns,
            file);
    }

    /// <summary>与 Skills/SkillParser 同款切分：规范化换行 → 首行 --- … 闭 --- 。</summary>
    private static (string Frontmatter, string Body)? SplitFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');

        if (lines.Length < 3 || lines[0].Trim() != FrontmatterDelimiter)
        {
            return null;
        }

        var endIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == FrontmatterDelimiter)
            {
                endIdx = i;
                break;
            }
        }

        if (endIdx < 0)
        {
            return null;
        }

        var fm = string.Join('\n', lines.Skip(1).Take(endIdx - 1));
        var body = string.Join('\n', lines.Skip(endIdx + 1));
        return (fm, body);
    }

    private sealed class AgentFrontmatter
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "description")]
        public string? Description { get; set; }

        [YamlMember(Alias = "model")]
        public string? Model { get; set; }

        [YamlMember(Alias = "tools")]
        public List<string>? Tools { get; set; }

        [YamlMember(Alias = "maxTurns")]
        public int? MaxTurns { get; set; }
    }
}
