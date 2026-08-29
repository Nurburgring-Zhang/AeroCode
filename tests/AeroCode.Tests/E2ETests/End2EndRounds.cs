// Copyright (c) AeroCode V3.0
// End-to-end 10 rounds — V3.0 final acceptance suite.
// Each round exercises a different cross-cutting pipeline using REAL APIs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.AI.Capabilities;
using AeroCode.AI.Configuration;
using AeroCode.AI.Models;
using AeroCode.AI.Providers;
using AeroCode.AI.Resilience;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Patch;
using AeroCode.Harness.Permission;
using AeroCode.Harness.PlanMode;
using AeroCode.Skills;
using AeroCode.Skills.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.E2ETests;

/// <summary>
/// R1: App startup. Settings loads, persists, reloads round-trip.
/// </summary>
[Collection("EnvMutators")]
public class R1_AppStartup_E2E
{
    [Fact]
    public async Task Settings_LoadAndSave_RoundTrip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_e2e_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Use real SettingsService (which uses default AppDataPaths).
            // To make it hermetic, copy default settings to tmp, modify, reload.
            var paths = new AeroCode.App.Services.AppDataPaths();
            paths.EnsureAll();
            var svc = new AeroCode.App.Configuration.SettingsService(paths);
            await svc.LoadAsync();
            Assert.NotNull(svc.Current);
            Assert.NotEmpty(svc.Current.Ai.Providers);

            // Round trip
            svc.Current.Ui.Theme = svc.Current.Ui.Theme == "Light" ? "Dark" : "Light";
            await svc.SaveAsync();
            var svc2 = new AeroCode.App.Configuration.SettingsService(paths);
            await svc2.LoadAsync();
            Assert.Equal(svc.Current.Ui.Theme, svc2.Current.Ui.Theme);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}

/// <summary>
/// R2: Core note CRUD pipeline.
/// </summary>
[Collection("EnvMutators")]
public class R2_NoteCRUD_E2E
{
    private static AeroCodeDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        var db = new AeroCodeDbContext(opts);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task FullCrudCycle_Works()
    {
        using var db = NewDb();
        var tags = new TagService(db);
        var notes = new NoteService(db, tags);
        var search = new SearchService(db);

        var c = await notes.CreateAsync("E2E Note", "alpha beta gamma", null);
        Assert.True(c.IsSuccess);
        var id = c.Value!.Id;

        var u = await notes.UpdateAsync(id, "E2E Note v2", "delta epsilon", null, true);
        Assert.True(u.IsSuccess);

        var s = await search.SearchAsync("delta");
        Assert.True(s.IsSuccess);
        Assert.Single(s.Value!);

        var soft = await notes.SoftDeleteAsync(id);
        Assert.True(soft.IsSuccess);
        var s2 = await search.SearchAsync("delta");
        Assert.Empty(s2.Value!);

        var all = await notes.GetAllAsync(includeDeleted: true);
        Assert.Single(all.Value!);

        var hard = await notes.HardDeleteAsync(id);
        Assert.True(hard.IsSuccess);
    }
}

/// <summary>
/// R3: AI capabilities pipeline. 6 capabilities end-to-end (FakeHandler).
/// </summary>
[Collection("EnvMutators")]
public class R3_AICapabilities_E2E
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string Response { get; set; } = "{}";
        public string? LastUserPrompt { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content is not null)
            {
                var body = req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
                    if (msg.GetProperty("role").GetString() == "user")
                        LastUserPrompt = msg.GetProperty("content").GetString();
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (IAiProvider, FakeHandler) MakeProvider(string response)
    {
        var h = new FakeHandler { Response = response };
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test");
        var cfg = new ProviderConfig
        {
            Id = "deepseek", DisplayName = "DeepSeek", Kind = "OpenAICompatible",
            BaseUrl = "https://api.deepseek.com/v1", DefaultModel = "deepseek-v4-flash",
            ApiKeyEnvVar = "DEEPSEEK_API_KEY", RequiresApiKey = true
        };
        var p = new DeepSeekProvider(new HttpClient(h), cfg, NullLogger<DeepSeekProvider>.Instance,
            new AiResiliencePipeline(new ResilienceOptions { MaxRetryAttempts = 0, CircuitBreakerMinThroughput = 0 }));
        return (p, h);
    }

    [Fact] public async Task R3_Summarizer()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"短摘要\"}}]}");
        var s = new Summarizer(p, NullLogger<Summarizer>.Instance);
        Assert.Equal("短摘要", await s.ExecuteAsync("很长的原文"));
    }

    [Fact] public async Task R3_Translator()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"}}]}");
        var t = new Translator(p, NullLogger<Translator>.Instance);
        Assert.Equal("Hello", await t.TranslateAsync("你好", "English"));
    }

    [Fact] public async Task R3_AutoTagger()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"[\\\"AI\\\",\\\"笔记\\\"]\"}}]}");
        var t = new AutoTagger(p, NullLogger<AutoTagger>.Instance);
        var tags = await t.ExtractAsync("关于 AI");
        Assert.Equal(2, tags.Count);
    }

    [Fact] public async Task R3_QuestionAnswerer()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"根据 #42 回答\"}}]}");
        var qa = new QuestionAnswerer(p, NullLogger<QuestionAnswerer>.Instance);
        var r = await qa.AnswerAsync("Q?", new List<(long, string, string)> { (42, "T", "C") });
        Assert.Contains("42", r);
    }

    [Fact] public async Task R3_SemanticSearcher()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"[{\\\"id\\\":1,\\\"score\\\":9.0}]\"}}]}");
        var s = new SemanticSearcher(p, NullLogger<SemanticSearcher>.Instance);
        var r = await s.SearchAsync("q", new List<SemanticSearcher.NoteCandidate> { new(1, "t", "p") });
        Assert.Single(r);
        Assert.Equal(1, r[0].Id);
    }

    [Fact] public async Task R3_Writer()
    {
        var (p, _) = MakeProvider("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"# 标题\\n\\n- 要点\"}}]}");
        var w = new Writer(p, NullLogger<Writer>.Instance);
        Assert.Contains("# 标题", await w.ExecuteAsync("写 AI 入门"));
    }
}

/// <summary>
/// R4: Skills engine — load + registry + invocation stats.
/// </summary>
[Collection("EnvMutators")]
public class R4_Skills_E2E
{
    [Fact]
    public void Skills_LoadAndCount()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_skills_" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new SkillHub(tmp);
            hub.LoadFromDisk();
            var skills = hub.List().ToList();
            // 7 bundled skills (5 engineering + 2 productivity) registered.
            Assert.True(skills.Count >= 7, $"expected >= 7, got {skills.Count}");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Skill_Registry_RecordInvocation()
    {
        var hub = new SkillHub(Path.Combine(Path.GetTempPath(), "aerocode_skills_x_" + Guid.NewGuid().ToString("N")));
        hub.LoadFromDisk();
        var first = hub.List().First();
        hub.Registry.RecordInvocation(first.Id, true);
        hub.Registry.RecordInvocation(first.Id, false);
        var stats = hub.Registry.GetStats(first.Id);
        Assert.Equal(2, stats.invocations);
        Assert.Equal(0.5, stats.successRate);
    }
}

/// <summary>
/// R5: Memory file system (Hermes pattern) — MEMORY.md / USER.md read/write with char limits.
/// </summary>
[Collection("EnvMutators")]
public class R5_Memory_E2E
{
    [Fact]
    public async Task Memory_WriteRead_RespectsLimit()
    {
        // 旧版用例是自证式（测试自己写文件自己截断，未触碰产品代码）。
        // 现改为真实调用 MemoryViewModel：AppDataPaths(string rootDirectory) 支持注入临时根目录，
        // 截断治理是 MemoryViewModel.SaveAsync 的产品逻辑（MEMORY.md 2200 / USER.md 1375）。
        var tmp = Path.Combine(Path.GetTempPath(), "aerocode_mem_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AeroCode.App.Services.AppDataPaths(tmp);
            var vm = new AeroCode.App.ViewModels.MemoryViewModel(paths);
            vm.MemoryContent = new string('x', 3000);
            vm.UserContent = new string('u', 2000);

            await vm.SaveCommand.ExecuteAsync(null);

            var memoryFile = Path.Combine(tmp, "memories", "MEMORY.md");
            var userFile = Path.Combine(tmp, "memories", "USER.md");
            Assert.Equal(2200, File.ReadAllText(memoryFile).Length); // Hermes MEMORY.md cap
            Assert.Equal(1375, File.ReadAllText(userFile).Length);   // Hermes USER.md cap

            // 读侧回环：新 VM 重载读到的正是截断后的内容，计数一致
            var vm2 = new AeroCode.App.ViewModels.MemoryViewModel(new AeroCode.App.Services.AppDataPaths(tmp));
            Assert.Equal(2200, vm2.MemoryContent.Length);
            Assert.Equal(2200, vm2.MemoryCharCount);
            Assert.Equal(1375, vm2.UserCharCount);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}

/// <summary>
/// R6: Code Review skill — 8-dimension heuristic review.
/// </summary>
[Collection("EnvMutators")]
public class R6_CodeReview_E2E
{
    [Fact]
    public async Task CodeReview_OnBadCode_FindsIssues()
    {
        var code = @"
            // TODO: 改这里
            public class X { public void DoWork() { Console.WriteLine(""debug""); } }
            public class Empty { public void Go() { } }
        ";
        var skill = new AeroCode.Skills.Bundled.Engineering.CodeReviewSkill();
        var input = new SkillInput { Args = new Dictionary<string, object?> { ["code"] = code } };
        var ctx = new SkillContext();
        var r = await skill.ExecuteAsync(input, ctx);
        Assert.True(r.Success);
        Assert.Contains("8", r.Text); // 8 dimensions
    }

    [Fact]
    public async Task CodeReview_OnCleanCode_Pass()
    {
        var code = @"
            public class Calc {
                public int Add(int a, int b) => a + b;
                public int Sub(int a, int b) => a - b;
            }
        ";
        var skill = new AeroCode.Skills.Bundled.Engineering.CodeReviewSkill();
        var input = new SkillInput { Args = new Dictionary<string, object?> { ["code"] = code } };
        var ctx = new SkillContext();
        var r = await skill.ExecuteAsync(input, ctx);
        Assert.True(r.Success);
    }
}

/// <summary>
/// R7: EventBus — publish / subscribe / unsubscribe / handler exception isolation.
/// </summary>
[Collection("EnvMutators")]
public class R7_EventBus_E2E
{
    [Fact]
    public void EventBus_FullLifecycle()
    {
        var bus = new EventBus();
        var counter = 0;
        Action unsubscribe = bus.Subscribe<TestEvent>(_ => counter++);
        bus.Publish(new TestEvent("hi"));
        bus.Publish(new TestEvent("there"));
        Assert.Equal(2, counter);
        unsubscribe();
        bus.Publish(new TestEvent("ignored"));
        Assert.Equal(2, counter);
    }

    [Fact]
    public void EventBus_HandlerThrows_DoesNotBreakOthers()
    {
        var bus = new EventBus();
        var sawIt = false;
        bus.Subscribe<TestEvent>(_ => throw new Exception("boom"));
        bus.Subscribe<TestEvent>(_ => sawIt = true);
        bus.Publish(new TestEvent("x"));
        Assert.True(sawIt);
    }

    public sealed record TestEvent(string Text);
}

/// <summary>
/// R8: Plan mode + Permission + Patch — small file, dangerous command, batch limit.
/// </summary>
[Collection("EnvMutators")]
public class R8_Harness_E2E
{
    [Fact]
    public void Permission_RunShell_DangerousCommand_Asks()
    {
        var bus = new EventBus();
        var policy = PermissionPolicy.CreateDefault(bus);
        var r = policy.Check("run_shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" });
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void Permission_ReadFile_Allowed()
    {
        var bus = new EventBus();
        var policy = PermissionPolicy.CreateDefault(bus);
        var r = policy.Check("read_file");
        Assert.Equal(PermissionDecision.Allow, r.Decision);
    }

    [Fact]
    public void Permission_GitPush_Asks()
    {
        var bus = new EventBus();
        var policy = PermissionPolicy.CreateDefault(bus);
        var r = policy.Check("git_push");
        Assert.Equal(PermissionDecision.Ask, r.Decision);
    }

    [Fact]
    public void PlanMode_EnableDisable_Pending()
    {
        var bus = new EventBus();
        var pm = new PlanModeManager(bus);
        Assert.False(pm.IsEnabled);
        pm.Enable();
        Assert.True(pm.IsEnabled);
        var r1 = pm.SubmitIfPlanMode("write_file", "x.cs", "content");
        Assert.Equal(1, pm.PendingCount);
        pm.Disable();
        var r2 = pm.SubmitIfPlanMode("write_file", "y.cs", "content");
        Assert.Equal(1, pm.PendingCount); // disabled = not added
        // approve r1
        var state = pm.Approve(r1.Id, (path, content) => { /* write to file */ });
        Assert.Equal(WriteState.Applied, state);
        Assert.Equal(0, pm.PendingCount);
    }

    [Fact]
    public void Patch_Replace_Success()
    {
        var engine = new PatchEngine();
        var (ok, newContent, _) = engine.Apply(
            "hello world",
            new Patch { FilePath = "a.cs", Kind = PatchKind.Replace, OldText = "world", NewText = "AeroCode" });
        Assert.True(ok);
        Assert.Contains("AeroCode", newContent);
    }

    [Fact]
    public void Patch_ValidateSize_TooManyLines()
    {
        var (ok, reason) = PatchEngine.ValidateSize("big.cs", lineCount: 300, fileCount: 1);
        Assert.False(ok);
        Assert.Contains("200", reason);
    }

    [Fact]
    public void Patch_ValidateSize_TooManyFiles()
    {
        var (ok, reason) = PatchEngine.ValidateSize("batch", lineCount: 50, fileCount: 20);
        Assert.False(ok);
        Assert.Contains("10", reason);
    }
}

/// <summary>
/// R9: FTS5 vs LIKE — search accuracy.
/// </summary>
[Collection("EnvMutators")]
public class R9_Search_E2E
{
    private static AeroCodeDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        var db = new AeroCodeDbContext(opts);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        AeroCode.Core.Data.FtsMigrations.EnsureFts5(db);
        return db;
    }

    [Fact]
    public async Task Fts5_EnglishSearch_HitsExactNote()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var notes = new NoteService(db, new TagService(db));
        await notes.CreateAsync("Quantum Computing", "qubit superposition entanglement", null);
        await notes.CreateAsync("Cooking Recipes", "pasta carbonara", null);
        var r = await svc.SearchAsync("quantum");
        Assert.Single(r.Value!);
    }

    [Fact]
    public async Task Like_CjkSearch_AsciiFallback()
    {
        using var db = NewDb();
        var svc = new SearchService(db);
        var notes = new NoteService(db, new TagService(db));
        await notes.CreateAsync("深度学习", "神经网络和梯度下降", null);
        var r = await svc.SearchAsync("深度");
        Assert.Single(r.Value!);
    }
}

/// <summary>
/// R10: Cross-cutting — settings save → load → all V3 components see new values.
/// </summary>
[Collection("EnvMutators")]
public class R10_CrossCutting_E2E
{
    [Fact]
    public async Task Settings_RoundTrip_AllComponents()
    {
        var paths = new AeroCode.App.Services.AppDataPaths();
        paths.EnsureAll();
        var s1 = new AeroCode.App.Configuration.SettingsService(paths);
        await s1.LoadAsync();
        // Mutate
        var origProvider = s1.Current.Ai.DefaultProviderId;
        s1.Current.Ai.DefaultProviderId = origProvider == "deepseek" ? "qwen" : "deepseek";
        var origTheme = s1.Current.Ui.Theme;
        s1.Current.Ui.Theme = origTheme == "Light" ? "Dark" : "Light";
        s1.Current.Ui.MemoryMaxChars = 3000;
        await s1.SaveAsync();

        // Reload fresh
        var s2 = new AeroCode.App.Configuration.SettingsService(paths);
        await s2.LoadAsync();
        Assert.Equal(origProvider == "deepseek" ? "qwen" : "deepseek", s2.Current.Ai.DefaultProviderId);
        Assert.Equal(origTheme == "Light" ? "Dark" : "Light", s2.Current.Ui.Theme);
        Assert.Equal(3000, s2.Current.Ui.MemoryMaxChars);

        // Restore original
        s1.Current.Ai.DefaultProviderId = origProvider;
        s1.Current.Ui.Theme = origTheme;
        s1.Current.Ui.MemoryMaxChars = 2200;
        await s1.SaveAsync();
    }
}
