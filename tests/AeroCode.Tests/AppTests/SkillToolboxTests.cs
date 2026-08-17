using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.App.Tools;
using AeroCode.Skills;
using AeroCode.Tests.ConversationTests;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// SkillToolbox：list_skills / run_skill 经真实 SkillHub 执行。
/// LlmInvoker 链路用可编程 provider 验证（默认 provider + 默认模型解析 → 真实 ChatAsync 调用），
/// DeepAuditSkill 作为真实消费 LlmInvoker 的内建技能参与端到端断言。
/// </summary>
public sealed class SkillToolboxTests : IDisposable
{
    private readonly string _hubRoot;
    private readonly string _workspaceRoot;
    private readonly SkillHub _hub;
    private readonly TestProviderRegistry _registry;
    private readonly ScriptedProvider _provider;
    private readonly SkillToolbox _toolbox;

    public SkillToolboxTests()
    {
        _hubRoot = Path.Combine(Path.GetTempPath(), $"skill_toolbox_hub_{Guid.NewGuid():N}");
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"skill_toolbox_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
        _hub = new SkillHub(_hubRoot);
        _provider = new ScriptedProvider { ProviderId = "scripted" };
        _registry = new TestProviderRegistry { DefaultProviderId = "scripted" };
        _registry.Add(_provider);
        _toolbox = new SkillToolbox(_hub, _registry, _workspaceRoot);
    }

    public void Dispose()
    {
        SafeDelete(_hubRoot);
        SafeDelete(_workspaceRoot);
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    /// <summary>造一个含 60+ 行 .cs 文件的临时项目目录（DeepAudit 采样需要）。</summary>
    private string MakeSampleProject()
    {
        var dir = Path.Combine(_workspaceRoot, "sample-project");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("namespace Sample;");
        sb.AppendLine("public sealed class Demo");
        sb.AppendLine("{");
        for (var i = 0; i < 60; i++)
        {
            sb.AppendLine($"    public int Field{i} => {i};");
        }

        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(dir, "Demo.cs"), sb.ToString());
        return dir;
    }

    [Fact]
    public void Definitions_ExposeExactlyTwoTools()
    {
        Assert.Equal(2, _toolbox.Definitions.Count);
        Assert.Equal(new[] { "list_skills", "run_skill" },
            _toolbox.Definitions.Select(d => d.Name).ToArray());
        foreach (var def in _toolbox.Definitions)
        {
            using var schema = JsonDocument.Parse(def.ParametersJsonSchema);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task ListSkills_ReturnsBundledSet_IncludingDeepAudit()
    {
        var result = await _toolbox.InvokeAsync("list_skills", "{}", CancellationToken.None);
        Assert.True(result.Success);

        using var doc = JsonDocument.Parse(result.Output);
        var count = doc.RootElement.GetProperty("count").GetInt32();
        Assert.True(count >= 8, $"内建技能应至少 8 个，实际 {count}");

        var ids = doc.RootElement.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("analysis/deep_audit", ids);
        Assert.Contains("productivity/summarize-note", ids);

        // 按分类过滤真实生效
        var filtered = await _toolbox.InvokeAsync("list_skills", """{"category":"analysis"}""", CancellationToken.None);
        using var f = JsonDocument.Parse(filtered.Output);
        Assert.All(f.RootElement.GetProperty("skills").EnumerateArray(),
            s => Assert.Equal("analysis", s.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task RunSkill_SummarizeNote_HonestPromptChain_NoLlmCall()
    {
        var result = await _toolbox.InvokeAsync("run_skill", """
            {"skill_id":"productivity/summarize-note","args":{"content":"alpha beta gamma delta"}}
            """, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Summarize the following note", doc.RootElement.GetProperty("text").GetString());
        var nextActions = doc.RootElement.GetProperty("next_actions").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("call-llm", nextActions);

        // SummarizeNoteSkill 不需要 LLM：provider 不应被调用
        Assert.Null(_provider.LastRequest);
    }

    [Fact]
    public async Task RunSkill_DeepAudit_LlmInvokerCallsDefaultProviderModel()
    {
        _provider.NonStreamContent = "SEC-VERDICT-42";
        var projectDir = MakeSampleProject();

        var argsJson = JsonSerializer.Serialize(new
        {
            skill_id = "analysis/deep_audit",
            user_message = "审一下安全",
            args = new { path = projectDir, dimensions = "security" },
        });
        var result = await _toolbox.InvokeAsync("run_skill", argsJson, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("SEC-VERDICT-42", text);       // LLM 真实产出进入报告
        Assert.Contains("LLM available**: yes", text);  // LlmInvoker 通道确实接通

        // LlmInvoker 解析到默认 provider 的默认模型，发起真实 ChatAsync
        Assert.NotNull(_provider.LastRequest);
        Assert.Equal("scripted-model", _provider.LastRequest!.Model);
        var prompt = Assert.Single(_provider.LastRequest.Messages).Content;
        Assert.Contains("Security", prompt); // 维度 prompt 真实送达
    }

    [Fact]
    public async Task RunSkill_DeepAudit_NoDefaultProvider_HonestFailureInReport()
    {
        _registry.DefaultProviderId = "ghost-provider"; // 未配置
        var projectDir = MakeSampleProject();

        var argsJson = JsonSerializer.Serialize(new
        {
            skill_id = "analysis/deep_audit",
            args = new { path = projectDir, dimensions = "security" },
        });
        var result = await _toolbox.InvokeAsync("run_skill", argsJson, CancellationToken.None);

        // DeepAuditSkill 捕获 LLM 失败并如实写入报告（静态部分仍然真实产出）——不伪造分析
        Assert.True(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("LLM call failed", text);
        Assert.Contains("ghost-provider", text);
        Assert.Contains("Static Metrics (real numbers)", text);
    }

    [Fact]
    public async Task RunSkill_UnknownSkill_FailsWithCount()
    {
        var result = await _toolbox.InvokeAsync("run_skill",
            """{"skill_id":"no/such-skill"}""", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("no/such-skill", result.Output);
        Assert.Contains("list_skills", result.Output);
    }

    [Fact]
    public async Task RunSkill_MissingSkillId_Fails()
    {
        var result = await _toolbox.InvokeAsync("run_skill", "{}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("缺少必填参数 'skill_id'", result.Output);
    }

    [Fact]
    public async Task RunSkill_ArgsNotObject_Fails()
    {
        var result = await _toolbox.InvokeAsync("run_skill",
            """{"skill_id":"productivity/summarize-note","args":5}""", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("'args' 必须是对象", result.Output);
    }

    [Fact]
    public async Task Invoke_InvalidJson_Fails()
    {
        var result = await _toolbox.InvokeAsync("run_skill", "{broken", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("JSON 非法", result.Output);
    }

    [Fact]
    public void Constructor_NullDeps_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillToolbox(null!, _registry, _workspaceRoot));
        Assert.Throws<ArgumentNullException>(() => new SkillToolbox(_hub, null!, _workspaceRoot));
        Assert.Throws<ArgumentNullException>(() => new SkillToolbox(_hub, _registry, null!));
    }
}
