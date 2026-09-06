using System.Text.Json;

namespace AeroCode.Eval;

/// <summary>fixture 加载/校验失败（fail-fast，聚合全部问题后抛出）。</summary>
public sealed class EvalFixtureException : Exception
{
    public EvalFixtureException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// fixture 加载器：读取 eval/datasets/*.json（版本冻结 v0），校验每条任务必备的
/// 四类字段 id / metric(口径) / criteria(判据) / version(版本戳)，缺失即抛 <see cref="EvalFixtureException"/>。
/// </summary>
public static class FixtureLoader
{
    /// <summary>当前冻结版本戳。</summary>
    public const string ExpectedVersion = "v0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<EvalFixtureDataset> LoadAll(string datasetsDir)
    {
        if (!Directory.Exists(datasetsDir))
        {
            throw new EvalFixtureException($"fixture 目录不存在: {datasetsDir}");
        }

        var files = Directory
            .EnumerateFiles(datasetsDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new EvalFixtureException($"fixture 目录中没有 .json 数据集: {datasetsDir}");
        }

        var problems = new List<string>();
        var datasets = new List<EvalFixtureDataset>();
        foreach (var file in files)
        {
            var dataset = LoadFile(file, problems);
            if (dataset is not null)
            {
                datasets.Add(dataset);
            }
        }

        var seenDatasetIds = new HashSet<string>(StringComparer.Ordinal);
        var seenMetrics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataset in datasets)
        {
            if (!seenDatasetIds.Add(dataset.DatasetId))
            {
                problems.Add($"dataset_id 重复: {dataset.DatasetId}");
            }

            if (!seenMetrics.Add(dataset.Metric))
            {
                problems.Add($"metric 口径在多个数据集中重复: {dataset.Metric}");
            }
        }

        if (problems.Count > 0)
        {
            throw new EvalFixtureException("fixture 校验失败（fail-fast，全部问题如下）:\n- " + string.Join("\n- ", problems));
        }

        return datasets;
    }

    public static EvalFixtureDataset? LoadFile(string path, ICollection<string> problems)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            problems.Add($"{path}: 读取失败 {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        EvalFixtureDataset? dataset;
        try
        {
            dataset = JsonSerializer.Deserialize<EvalFixtureDataset>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            problems.Add($"{path}: JSON 解析失败: {ex.Message}");
            return null;
        }

        if (dataset is null)
        {
            problems.Add($"{path}: JSON 为空或不是对象");
            return null;
        }

        if (string.IsNullOrWhiteSpace(dataset.DatasetId))
        {
            problems.Add($"{path}: 顶层 dataset_id 缺失");
        }

        if (string.IsNullOrWhiteSpace(dataset.Metric))
        {
            problems.Add($"{path}: 顶层 metric(口径) 缺失");
        }

        if (string.IsNullOrWhiteSpace(dataset.Criteria))
        {
            problems.Add($"{path}: 顶层 criteria(判据) 缺失");
        }

        ValidateVersion(path, dataset.Version, "顶层 version", problems);
        if (dataset.Tasks.Count == 0)
        {
            problems.Add($"{path}: tasks 为空（每份数据集至少 1 条任务）");
        }

        var seenTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in dataset.Tasks)
        {
            var label = $"task[{task.Id}]";
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                label = "task[id 缺失]";
                problems.Add($"{path}: {label}");
            }
            else if (!seenTaskIds.Add(task.Id))
            {
                problems.Add($"{path}: 任务 id 重复: {task.Id}");
            }

            if (string.IsNullOrWhiteSpace(task.Metric))
            {
                problems.Add($"{path}: {label}: metric(口径) 缺失");
            }
            else if (!string.IsNullOrWhiteSpace(dataset.Metric) &&
                     !string.Equals(task.Metric, dataset.Metric, StringComparison.Ordinal))
            {
                problems.Add($"{path}: {label}: metric(口径) {task.Metric} 与数据集口径 {dataset.Metric} 不一致");
            }

            if (string.IsNullOrWhiteSpace(task.Criteria))
            {
                problems.Add($"{path}: {label}: criteria(判据) 缺失");
            }

            ValidateVersion(path, task.Version, $"{label} version", problems);
            if (task.Payload.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{path}: {label}: payload 缺失或不是对象");
            }
        }

        return problems.Count == 0 ? dataset : null;
    }

    private static void ValidateVersion(string path, string version, string fieldLabel, ICollection<string> problems)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            problems.Add($"{path}: {fieldLabel}(版本戳) 缺失");
        }
        else if (!string.Equals(version, ExpectedVersion, StringComparison.Ordinal))
        {
            problems.Add($"{path}: {fieldLabel} = {version}，本装载器只接受冻结版本 {ExpectedVersion}");
        }
    }
}
