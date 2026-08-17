using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroAgent.Moa.Tools;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using AeroCode.App.Tools;
using AeroCode.Core.Data;
using AeroCode.Core.Services;
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;
using AeroCode.Skills;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// S6 内建工具域真实链路 E2E：本地 HttpListener 按脚本回 OpenAI 兼容响应
/// （真实 socket/真实 HTTP/真实 JSON），ProviderFactory 走真实 HttpClient，
/// WorkerRunner 工具循环 → ToolRouter 授权 → NoteToolbox/SkillToolbox 真实执行
/// （真实 SQLite 落库 / 真实 SkillHub + LlmInvoker 再入 HTTP）。
/// 对话“记一条笔记”→ 模型 tool_calls → 笔记真实出现在 AeroCode.db；
/// 技能链同链：run_skill → DeepAudit → LlmInvoker 真实调用默认模型。
/// </summary>
public sealed class BuiltInToolE2ETests : MoaTestBase
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private readonly object _sync = new();
    private readonly List<string> _receivedBodies = new();
    private readonly Queue<string> _scriptedResponses = new();

    // ---- 笔记侧真实 DB ----
    private readonly string _noteDir;
    private readonly SqliteConnection _noteKeepAlive;
    private readonly AeroCodeDbContext _noteDb;
    private readonly NoteToolbox _noteToolbox;

    public BuiltInToolE2ETests()
    {
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _serverTask = Task.Run(ServeLoopAsync);

        _noteDir = Path.Combine(Path.GetTempPath(), $"builtin_tool_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_noteDir);
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_noteDir, "notes.db"),
        }.ToString();
        _noteKeepAlive = new SqliteConnection(connStr);
        _noteKeepAlive.Open();
        var options = new DbContextOptionsBuilder<AeroCodeDbContext>()
            .UseSqlite(connStr)
            .Options;
        _noteDb = new AeroCodeDbContext(options);
        _noteDb.Database.EnsureCreated();

        var tags = new TagService(_noteDb);
        _noteToolbox = new NoteToolbox(
            new NoteService(_noteDb, tags),
            new NotebookService(_noteDb),
            tags,
            new SearchService(_noteDb));
    }

    public override void Dispose()
    {
        _serverCts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 忽略关闭竞态
        }

        _noteDb.Dispose();
        _noteKeepAlive.Dispose();
        SqliteConnection.ClearPool(_noteKeepAlive);
        try
        {
            Directory.Delete(_noteDir, recursive: true);
        }
        catch
        {
            // 子进程/池连接短暂持文件时容忍残留
        }

        base.Dispose();
    }

    // ---- 脚本化 OpenAI 兼容服务器 ----

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void Enqueue(string responseJson)
    {
        lock (_sync)
        {
            _scriptedResponses.Enqueue(responseJson);
        }
    }

    private List<string> ReceivedBodies
    {
        get
        {
            lock (_sync)
            {
                return _receivedBodies.ToList();
            }
        }
    }

    private async Task ServeLoopAsync()
    {
        while (!_serverCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // listener 已停止
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            string? response;
            lock (_sync)
            {
                _receivedBodies.Add(body);
                response = _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : null;
            }

            if (response is null)
            {
                // 脚本耗尽：如实 500，让上层链路诚实失败，不伪造响应
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(response);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.KeepAlive = false; // 明确长度边界 + 关连接，防响应串流
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static string CompletionPayload(string model, string content)
    {
        var payload = new
        {
            id = "r1",
            model,
            choices = new object[]
            {
                new
                {
                    message = new { role = "assistant", content },
                    finish_reason = "stop",
                },
            },
            usage = new { prompt_tokens = 13, completion_tokens = 4, total_tokens = 17 },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string ToolCallPayload(string model, string callId, string toolName, object args)
    {
        var payload = new
        {
            id = "r1",
            model,
            choices = new object[]
            {
                new
                {
                    message = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                        // arguments 是 JSON 字符串字面量（provider 按字符串取值后再解析）
                        ["tool_calls"] = new object[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new { name = toolName, arguments = JsonSerializer.Serialize(args) },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new { prompt_tokens = 11, completion_tokens = 5, total_tokens = 16 },
        };
        return JsonSerializer.Serialize(payload);
    }

    // ---- 装配 ----

    private ProviderFactory MakeFactory(string providerId, string model)
    {
        var aiOptions = new AIOptions { DefaultProviderId = providerId };
        aiOptions.Providers.Add(new ProviderConfig
        {
            Id = providerId,
            DisplayName = providerId,
            Kind = "OpenAICompatible",
            BaseUrl = $"{_baseUrl}v1",
            DefaultModel = model,
            RequiresApiKey = false,
            SupportsStreaming = false,
            SupportsThinking = false,
            TimeoutSeconds = 30,
        });
        return new ProviderFactory(aiOptions, NullLoggerFactory.Instance);
    }

    private static (Channel<ChatEvent> Sink, ChannelWriter<ChatEvent> Writer) NewSink()
    {
        var ch = Channel.CreateUnbounded<ChatEvent>();
        return (ch, ch.Writer);
    }

    private static async Task<List<ChatEvent>> DrainAsync(Channel<ChatEvent> ch)
    {
        ch.Writer.TryComplete();
        var list = new List<ChatEvent>();
        await foreach (var e in ch.Reader.ReadAllAsync())
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public async Task NoteChain_RealHttp_RealDb_CreateNoteViaToolCalls()
    {
        const string model = "note-model";
        var factory = MakeFactory("note-prov", model);
        var profile = SetProfile("note-prov", new[] { ModelStrength.General });

        // 注册表 + 授权：内建笔记工具默认放行（与 App 组合根策略一致）
        var registry = new ToolboxRegistry();
        registry.Register(_noteToolbox);
        var policy = PermissionPolicy.CreateDefault(new EventBus());
        policy.SetDefaultDecision("create_note", PermissionDecision.Allow);
        var router = new ToolRouter(registry, policy, broker: null);
        var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

        // 脚本：第一轮要求调用 create_note，第二轮给出最终答复
        Enqueue(ToolCallPayload(model, "call-n1", "create_note",
            new { title = "E2E 真实笔记", content = "工具链写入" }));
        Enqueue(CompletionPayload(model, "笔记已记录"));

        var session = await NewSessionAsync(OrchestrationStrategy.Single);
        var ctx = new OrchestrationContext
        {
            Session = session,
            History = Array.Empty<ChatMessage>(),
            UserMessageId = "msg-user",
            Providers = factory,
        };
        var (sink, writer) = NewSink();

        var outcome = await loopRunner.RunAsync(
            ctx, new ModelAssignment("note-prov", model, profile), StrategyRole.Worker,
            parentMessageId: null, label: null,
            new List<AeroCode.AI.Models.ChatMessage>
            {
                new() { Role = "user", Content = "帮我记一条笔记" },
            },
            stream: false, isFinal: true, sink: writer, budget: null, CancellationToken.None);

        // ---- 结果链：模型最终答复 ----
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("笔记已记录", outcome.Content);

        // ---- 真实 DB：笔记确实落库（直查，不经工具自身）----
        var note = await _noteDb.Notes.AsNoTracking().SingleAsync(n => n.Title == "E2E 真实笔记");
        Assert.Equal("工具链写入", note.Content);
        Assert.False(note.IsDeleted);

        // ---- 会话库：工具轮次完整留痕 ----
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        Assert.Equal(3, messages.Count); // 助手 tool_calls 轮 + tool 结果 + 最终答复
        var turn = Assert.Single(messages, m => m.Role == ChatRole.Assistant && m.IsFinal == false);
        Assert.Contains("create_note", turn.ToolCallsJson);
        var toolRow = Assert.Single(messages, m => m.Role == ChatRole.Tool);
        Assert.Equal("create_note", toolRow.Name);
        Assert.Equal("call-n1", toolRow.ToolCallId);
        Assert.Equal(MessageStatus.Completed, toolRow.Status);
        Assert.Contains("\"ok\":true", toolRow.Content); // 工具真实返回了新建 id
        var final = Assert.Single(messages, m => m.IsFinal == true);
        Assert.Equal("笔记已记录", final.Content);

        // ---- 事件链：工具开始/完成事件 ----
        var events = await DrainAsync(sink);
        var started = Assert.Single(events.OfType<ToolCallStartedEvent>());
        Assert.Equal("create_note", started.ToolName);
        // 序列化默认将非 ASCII 转义为 \uXXXX → 必须解析后按解码值断言
        Assert.NotNull(started.ArgumentsJson);
        using (var argsDoc = JsonDocument.Parse(started.ArgumentsJson))
        {
            Assert.Equal("E2E 真实笔记", argsDoc.RootElement.GetProperty("title").GetString());
        }
        var completed = Assert.Single(events.OfType<ToolCallCompletedEvent>());
        Assert.True(completed.Success);
        Assert.False(completed.Denied);

        // ---- 真实 HTTP：两次请求，第一次携带 12 个工具定义，第二次回灌 tool 结果 ----
        var bodies = ReceivedBodies;
        Assert.Equal(2, bodies.Count);
        using var first = JsonDocument.Parse(bodies[0]);
        Assert.Equal(model, first.RootElement.GetProperty("model").GetString());
        var toolNames = first.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(12, toolNames.Count);
        Assert.Contains("create_note", toolNames);
        Assert.Contains("search_notes", toolNames);

        using var second = JsonDocument.Parse(bodies[1]);
        var roles = second.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        Assert.Equal(new[] { "user", "assistant", "tool" }, roles);
        var toolMsg = second.RootElement.GetProperty("messages")[2];
        Assert.Equal("call-n1", toolMsg.GetProperty("tool_call_id").GetString());
        Assert.Contains("\"ok\":true", toolMsg.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SkillChain_RealHttp_RunSkill_LlmInvokerReentersHttp()
    {
        const string model = "skill-model";
        var factory = MakeFactory("skill-prov", model);
        var profile = SetProfile("skill-prov", new[] { ModelStrength.General });

        // 真实 SkillHub（含 8 个内建技能）+ SkillToolbox（LlmInvoker 指向同一真实工厂）
        var hubRoot = Path.Combine(Path.GetTempPath(), $"e2e_hub_{Guid.NewGuid():N}");
        var auditDir = Path.Combine(Path.GetTempPath(), $"e2e_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(auditDir);
        WriteSampleSource(auditDir);
        try
        {
            var hub = new SkillHub(hubRoot);
            var skillToolbox = new SkillToolbox(hub, factory, auditDir);
            var registry = new ToolboxRegistry();
            registry.Register(skillToolbox);
            var policy = PermissionPolicy.CreateDefault(new EventBus());
            policy.SetDefaultDecision("run_skill", PermissionDecision.Allow);
            var router = new ToolRouter(registry, policy, broker: null);
            var loopRunner = new WorkerRunner(Sessions, Catalog, tools: router);

            // 脚本：外层第一轮调 run_skill；技能内部 LlmInvoker 消费第二条；外层收尾第三条
            Enqueue(ToolCallPayload(model, "call-s1", "run_skill", new
            {
                skill_id = "analysis/deep_audit",
                user_message = "审一下安全",
                args = new { path = auditDir, dimensions = "security" },
            }));
            Enqueue(CompletionPayload(model, "AUDIT-VERDICT-77"));
            Enqueue(CompletionPayload(model, "审计完成"));

            var session = await NewSessionAsync(OrchestrationStrategy.Single);
            var ctx = new OrchestrationContext
            {
                Session = session,
                History = Array.Empty<ChatMessage>(),
                UserMessageId = "msg-user",
                Providers = factory,
            };
            var (sink, writer) = NewSink();

            var outcome = await loopRunner.RunAsync(
                ctx, new ModelAssignment("skill-prov", model, profile), StrategyRole.Worker,
                parentMessageId: null, label: null,
                new List<AeroCode.AI.Models.ChatMessage>
                {
                    new() { Role = "user", Content = "深度审计这个项目" },
                },
                stream: false, isFinal: true, sink: writer, budget: null, CancellationToken.None);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal("审计完成", outcome.Content);

            // ---- 三次真实 HTTP：外层工具轮 / 技能内 LlmInvoker / 外层收尾 ----
            var bodies = ReceivedBodies;
            Assert.Equal(3, bodies.Count);
            using var inner = JsonDocument.Parse(bodies[1]);
            Assert.Equal(model, inner.RootElement.GetProperty("model").GetString());
            // LlmInvoker 用的是默认 provider 的默认模型，且只带单条 user prompt
            Assert.Single(inner.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Contains("Security",
                inner.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

            // ---- 会话库：工具行承载真实技能报告（含 LLM 真实产出）----
            var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
            var toolRow = Assert.Single(messages, m => m.Role == ChatRole.Tool);
            Assert.Equal("run_skill", toolRow.Name);
            Assert.Equal(MessageStatus.Completed, toolRow.Status);
            Assert.Contains("AUDIT-VERDICT-77", toolRow.Content);
            Assert.Contains("Deep Audit Report", toolRow.Content);

            var events = await DrainAsync(sink);
            var completed = Assert.Single(events.OfType<ToolCallCompletedEvent>());
            Assert.True(completed.Success);
        }
        finally
        {
            TryDeleteDir(hubRoot);
            TryDeleteDir(auditDir);
        }
    }

    private static void WriteSampleSource(string dir)
    {
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
    }

    private static void TryDeleteDir(string dir)
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
}
