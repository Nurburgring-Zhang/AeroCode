// Copyright (c) AeroCode
// ArtifactRegistry — ACS v2.3.0 工件登记（transcript 不是数据库，产出物必须登记为带溯源的工件）。
// 证据基线 E24：supersedes 链是最小版本谱系（ADR 同构）；
// produced_by 指向真实节点，supersedes 谱系不断链不成环。
namespace AeroCode.Harness.Acs;

/// <summary>登记工件。</summary>
public sealed record AcsArtifact(
    string Id,
    string Name,
    string ProducedBy,
    string? Supersedes,
    DateTime TimestampUtc);

/// <summary>谱系校验结果。</summary>
public sealed record AcsLineageResult(bool Valid, IReadOnlyList<string> Problems);

/// <summary>
/// 工件登记与谱系校验：produced_by 必须指向已登记节点，supersedes 不断链不成环。
/// </summary>
public sealed class ArtifactRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AcsArtifact> _artifacts = new(StringComparer.Ordinal);

    /// <summary>当前全部工件（只读快照）。</summary>
    public IReadOnlyList<AcsArtifact> Artifacts
    {
        get { lock (_lock) return _artifacts.Values.ToArray(); }
    }

    /// <summary>
    /// 登记工件。produced_by 必须是已登记节点（或声明的根节点标记）；
    /// supersedes 必须指向已登记工件（不断链）。重复 id = 拒绝。
    /// </summary>
    public (bool Accepted, string? Error) Register(AcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_lock)
        {
            if (_artifacts.ContainsKey(artifact.Id))
            {
                return (false, $"工件 id 重复：{artifact.Id}");
            }

            if (string.IsNullOrWhiteSpace(artifact.ProducedBy))
            {
                return (false, $"工件 {artifact.Id} 缺 produced_by（溯源必填）");
            }

            // produced_by 必须已登记（除根节点约定标记 "root"）
            if (!string.Equals(artifact.ProducedBy, "root", StringComparison.Ordinal) &&
                !_artifacts.ContainsKey(artifact.ProducedBy))
            {
                return (false, $"工件 {artifact.Id} 的 produced_by '{artifact.ProducedBy}' 未登记（溯源断链）");
            }

            // supersedes 必须已登记（不断链）
            if (artifact.Supersedes is not null && !_artifacts.ContainsKey(artifact.Supersedes))
            {
                return (false, $"工件 {artifact.Id} 的 supersedes '{artifact.Supersedes}' 未登记（谱系断链）");
            }

            // 成环检测：沿 supersedes 链走不能回到自身
            if (artifact.Supersedes is not null && WouldCreateCycle(artifact))
            {
                return (false, $"工件 {artifact.Id} 的 supersedes 链成环");
            }

            _artifacts[artifact.Id] = artifact;
            return (true, null);
        }
    }

    /// <summary>校验当前谱系完整性（无断链无成环）。</summary>
    public AcsLineageResult ValidateLineage()
    {
        lock (_lock)
        {
            var problems = new List<string>();
            foreach (var a in _artifacts.Values)
            {
                if (a.Supersedes is not null && !_artifacts.ContainsKey(a.Supersedes))
                {
                    problems.Add($"工件 {a.Id} 的 supersedes '{a.Supersedes}' 断链");
                }

                if (a.Supersedes is not null && WouldCreateCycle(a))
                {
                    problems.Add($"工件 {a.Id} 的 supersedes 链成环");
                }
            }

            return new AcsLineageResult(problems.Count == 0, problems);
        }
    }

    /// <summary>清空登记（新任务开始）。</summary>
    public void Clear()
    {
        lock (_lock) _artifacts.Clear();
    }

    private bool WouldCreateCycle(AcsArtifact newArtifact)
    {
        // 从 supersedes 目标沿链回溯，若能回到 newArtifact.Id 则成环
        var visited = new HashSet<string>(StringComparer.Ordinal) { newArtifact.Id };
        var current = newArtifact.Supersedes;
        while (current is not null)
        {
            if (!visited.Add(current))
            {
                return true; // 回到已访问节点 = 环
            }

            current = _artifacts.TryGetValue(current, out var prev) ? prev.Supersedes : null;
        }

        return false;
    }
}
