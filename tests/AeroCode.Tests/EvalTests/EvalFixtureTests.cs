using System.Text.Json;
using AeroCode.Eval;
using Xunit;

namespace AeroCode.Tests.EvalTests;

/// <summary>
/// fixture 解析契约：加载 eval/datasets 的版本冻结 JSON，校验每条任务的
/// id / 口径 / 判据 / 版本戳四类字段齐全且版本为 v0；非法 fixture fail-fast。
/// </summary>
public sealed class EvalFixtureTests
{
    [SkippableFact]
    public void LoadAll_Datasets_FourRequiredFieldsPerTaskAndFrozenV0()
    {
        var root = EvalTestHost.FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var datasets = FixtureLoader.LoadAll(Path.Combine(root!, "eval", "datasets"));

        Assert.True(datasets.Count >= 3, $"至少 3 份数据集，实际 {datasets.Count}");
        foreach (var dataset in datasets)
        {
            Assert.False(string.IsNullOrWhiteSpace(dataset.DatasetId), "顶层 dataset_id 缺失");
            Assert.False(string.IsNullOrWhiteSpace(dataset.Metric), "顶层 metric(口径) 缺失");
            Assert.False(string.IsNullOrWhiteSpace(dataset.Criteria), "顶层 criteria(判据) 缺失");
            Assert.Equal("v0", dataset.Version);
            Assert.True(dataset.Tasks.Count >= 2, $"{dataset.DatasetId} 至少 2 条样例任务");
            foreach (var task in dataset.Tasks)
            {
                Assert.False(string.IsNullOrWhiteSpace(task.Id), $"{dataset.DatasetId} 存在 id 缺失的任务");
                Assert.False(string.IsNullOrWhiteSpace(task.Metric), $"{task.Id} 口径缺失");
                Assert.Equal(dataset.Metric, task.Metric);
                Assert.False(string.IsNullOrWhiteSpace(task.Criteria), $"{task.Id} 判据缺失");
                Assert.Equal("v0", task.Version);
                Assert.Equal(JsonValueKind.Object, task.Payload.ValueKind);
            }
        }

        var metricKeys = datasets.Select(d => d.Metric).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("multi_turn_hallucination_rate", metricKeys);
        Assert.Contains("checkpoint_pass_rate", metricKeys);
        Assert.Contains("unit_cost_completion", metricKeys);
    }

    [Fact]
    public void LoadAll_TaskMissingCriteria_FailsFastWithTaskId()
    {
        var dir = EvalTestHost.CreateTempDir();
        try
        {
            var json = """
                {
                  "dataset_id": "broken_v0",
                  "metric": "multi_turn_hallucination_rate",
                  "version": "v0",
                  "criteria": "口径判据说明",
                  "tasks": [
                    { "id": "bad-001", "metric": "multi_turn_hallucination_rate", "version": "v0",
                      "payload": { "turns": [ { "role": "user", "content": "a" }, { "role": "user", "content": "b" } ] } }
                  ]
                }
                """;
            var path = Path.Combine(dir, "broken_v0.json");
            File.WriteAllText(path, json);

            var problems = new List<string>();
            var dataset = FixtureLoader.LoadFile(path, problems);

            Assert.Null(dataset);
            Assert.Contains(problems, p => p.Contains("bad-001") && p.Contains("criteria"));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void LoadAll_WrongVersionStamp_Rejected()
    {
        var dir = EvalTestHost.CreateTempDir();
        try
        {
            var json = """
                {
                  "dataset_id": "future_v1",
                  "metric": "checkpoint_pass_rate",
                  "version": "v1",
                  "criteria": "判据",
                  "tasks": [
                    { "id": "cp-x", "metric": "checkpoint_pass_rate", "criteria": "判据", "version": "v1",
                      "payload": { "prompt": "p", "checkpoints": [ { "index": 1, "name": "n", "detect_any": ["k"] } ] } }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(dir, "future_v1.json"), json);

            var problems = new List<string>();
            var dataset = FixtureLoader.LoadFile(Path.Combine(dir, "future_v1.json"), problems);

            Assert.Null(dataset);
            Assert.Equal(2, problems.Count(p => p.Contains("v0")));
        }
        finally
        {
            EvalTestHost.DeleteTempDir(dir);
        }
    }

    [Fact]
    public void LoadAll_MissingDatasetsDir_Throws()
    {
        Assert.Throws<EvalFixtureException>(
            () => FixtureLoader.LoadAll(Path.Combine(Path.GetTempPath(), "aero-eval-nope", Guid.NewGuid().ToString("N"))));
    }
}
