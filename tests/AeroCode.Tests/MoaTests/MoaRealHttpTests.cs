using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Aggregation;
using AeroAgent.Moa.Assignment;
using AeroAgent.Moa.Planning;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using AeroCode.AI.Configuration;
using AeroCode.AI.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// MOA 真实 HTTP 端到端：本地 HttpListener 按请求体里的 model 字段分发多个
/// “模型”（真实 socket、真实 HTTP/SSE 协议），ProviderFactory 走真实 HttpClient。
/// 验证 Router 与 Ensemble 两条多模型链路：统一门面 → 策略 → Provider → SSE/JSON
/// 解析 → 持久化，全程无进程内假实现。
/// </summary>
public sealed class MoaRealHttpTests : MoaTestBase
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Task _serverTask;
    private readonly object _sync = new();
    private readonly List<string> _receivedBodies = new();

    /// <summary>各“模型”的非流式答复内容。</summary>
    private static readonly Dictionary<string, string> CompletionModels = new()
    {
        ["router-model"] = "{\"category\":\"code\",\"reason\":\"代码请求\"}",
        ["alpha-model"] = "ANSWER-A",
        ["beta-model"] = "ANSWER-B",
        // Decompose：planner 产出两步 DAG（s2 依赖 s1）
        ["decomp-planner"] =
            "{\"goal\":\"真实链路任务\",\"steps\":[" +
            "{\"id\":\"s1\",\"title\":\"调研\",\"description\":\"收集资料\",\"dependsOn\":[],\"kind\":\"analysis\"}," +
            "{\"id\":\"s2\",\"title\":\"成文\",\"description\":\"写成文章\",\"dependsOn\":[\"s1\"],\"kind\":\"write\"}]}",
        ["analyst-model"] = "分析产出",
        ["writer-model"] = "初稿内容", // 同一模型：起草非流式、修订流式
        ["review-model"] = "评审意见",
    };

    /// <summary>各“模型”的流式增量。</summary>
    private static readonly Dictionary<string, string[]> StreamModels = new()
    {
        ["code-model"] = new[] { "真实", "链路", "打通" },
        ["judge-model"] = new[] { "裁决", "合成" },
        ["synth-model"] = new[] { "最终", "合成" },
        ["writer-model"] = new[] { "修订", "终稿" },
    };

    public MoaRealHttpTests()
    {
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _serverTask = Task.Run(ServeLoopAsync);
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

        base.Dispose();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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
            using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            lock (_sync)
            {
                _receivedBodies.Add(body);
            }

            string model = string.Empty;
            var stream = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                model = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
                stream = doc.RootElement.TryGetProperty("stream", out var s)
                    && s.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                // 请求体非法 → 400，让上层如实失败
            }

            if (stream && StreamModels.TryGetValue(model, out var deltas))
            {
                await WriteSseAsync(ctx, model, deltas);
            }
            else if (!stream && CompletionModels.TryGetValue(model, out var content))
            {
                WriteCompletion(ctx, model, content);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
    }

    private async Task WriteSseAsync(HttpListenerContext ctx, string model, string[] deltas)
    {
        var events = deltas
            .Select(d => $"data: {{\"id\":\"s1\",\"model\":\"{model}\",\"choices\":[{{\"delta\":{{\"content\":\"{d}\"}}}}]}}")
            .ToList();
        events.Add("data: [DONE]");
        var payload = string.Join("\n\n", events) + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.KeepAlive = false; // 关连接收尾：SSE 以连接结束为流终点
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private void WriteCompletion(HttpListenerContext ctx, string model, string content)
    {
        var escaped = content.Replace("\"", "\\\"");
        var payload = $"{{\"id\":\"r1\",\"model\":\"{model}\"," +
            $"\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}},\"finish_reason\":\"stop\"}}]," +
            "\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length; // 明确长度边界，防止复用连接上响应串流
        ctx.Response.KeepAlive = false;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
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

    /// <summary>真实 ProviderFactory + 按画像分配的 MOA 装配（不用测试桩注册表）。</summary>
    private (ChatOrchestrationFacade Facade, ProviderFactory Factory) MakeRealFacade(
        string defaultProviderId,
        params (string Id, string Model)[] providers)
    {
        var aiOptions = new AIOptions { DefaultProviderId = defaultProviderId };
        foreach (var (id, model) in providers)
        {
            aiOptions.Providers.Add(new ProviderConfig
            {
                Id = id,
                DisplayName = id,
                Kind = "OpenAICompatible",
                BaseUrl = $"{_baseUrl}v1",
                DefaultModel = model,
                RequiresApiKey = false,
                SupportsStreaming = true,
                TimeoutSeconds = 30,
            });
        }

        var factory = new ProviderFactory(aiOptions, NullLoggerFactory.Instance);
        var assigner = new ModelAssigner(factory, Catalog);
        var resolver = new ModelResolver(factory, Catalog, assigner);
        var synthesizer = new Synthesizer(Runner);
        var router = new RouterStrategy(Runner, resolver, Options);
        var ensemble = new EnsembleStrategy(Sessions, Runner, resolver, assigner, synthesizer, Options);
        var planner = new TaskPlanner(Runner);
        var decompose = new DecomposeStrategy(Sessions, Runner, resolver, assigner, planner, synthesizer, Options);
        var pipeline = new PipelineStrategy(Runner, resolver, Options);
        var facade = new ChatOrchestrationFacade(
            Sessions, factory,
            new IOrchestrationStrategy[]
            {
                new SingleStrategy(Sessions), router, ensemble, decompose, pipeline,
            });
        return (facade, factory);
    }

    [Fact]
    public async Task RouterOverRealHttp_ClassifiesThenStreamsFromSpecialist()
    {
        // router-prov 固定做路由；coder-prov 的 code 画像赢得分类后的分配
        SetProfile("router-prov", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        SetProfile("coder-prov", new[] { ModelStrength.Code });
        Options.Router = new ModelBinding("router-prov", null);

        var (facade, _) = MakeRealFacade(
            "router-prov",
            ("router-prov", "router-model"),
            ("coder-prov", "code-model"));
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一个排序函数"));

        // 事件链：路由决策（非流式）→ 专家流式作答
        var starts = events.OfType<AssistantMessageStarted>().ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(StrategyRole.Router, starts[0].OrchestrationRole);
        Assert.Equal("router-prov", starts[0].ProviderId);
        Assert.Equal(StrategyRole.Worker, starts[1].OrchestrationRole);
        Assert.Equal("coder-prov", starts[1].ProviderId);
        Assert.Equal(starts[0].MessageId, starts[1].ParentMessageId);

        var deltas = events.OfType<TextDeltaEvent>()
            .Where(d => d.MessageId == starts[1].MessageId)
            .Select(d => d.Delta)
            .ToArray();
        Assert.Equal(new[] { "真实", "链路", "打通" }, deltas);

        // 持久化：路由原文留痕 + 专家答复完整
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var routerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Router);
        Assert.Contains("\"category\":\"code\"", routerMsg.Content);
        Assert.Equal(MessageStatus.Completed, routerMsg.Status);
        var workerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Equal("真实链路打通", workerMsg.Content);
        Assert.Equal("code-model", workerMsg.ModelId);
        Assert.Equal(MessageStatus.Completed, workerMsg.Status);

        // 真实 HTTP：两次请求分别命中 router-model 与 code-model
        var bodies = ReceivedBodies;
        Assert.Equal(2, bodies.Count);
        using var first = JsonDocument.Parse(bodies[0]);
        Assert.Equal("router-model", first.RootElement.GetProperty("model").GetString());
        Assert.False(first.RootElement.GetProperty("stream").GetBoolean());
        using var second = JsonDocument.Parse(bodies[1]);
        Assert.Equal("code-model", second.RootElement.GetProperty("model").GetString());
        Assert.True(second.RootElement.GetProperty("stream").GetBoolean());
        var lastMsg = second.RootElement.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("写一个排序函数", lastMsg.GetProperty("content").GetString());

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Router, turn.Strategy);
        Assert.Equal(2, turn.TotalMessages);
    }

    [Fact]
    public async Task EnsembleOverRealHttp_TwoCandidatesAndJudge_AllRealHttp()
    {
        SetProfile("alpha-prov", new[] { ModelStrength.General });
        SetProfile("beta-prov", new[] { ModelStrength.General });
        SetProfile("judge-prov", new[] { ModelStrength.Review });

        var (facade, _) = MakeRealFacade(
            "alpha-prov",
            ("alpha-prov", "alpha-model"),
            ("beta-prov", "beta-model"),
            ("judge-prov", "judge-model"));
        var session = await NewSessionAsync(OrchestrationStrategy.Ensemble);

        var events = await CollectAsync(facade.SendAsync(session.Id, "哪个答案更可靠？"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 两个候选经真实 HTTP 非流式调用拿到答复
        var alphaMsg = messages.Single(m => m.Label == "候选 A");
        Assert.Equal("alpha-prov", alphaMsg.ProviderId);
        Assert.Equal("ANSWER-A", alphaMsg.Content);
        Assert.Equal(MessageStatus.Completed, alphaMsg.Status);
        // 真实 usage 透传落库（服务器返回 7/3）
        Assert.Equal(7, alphaMsg.TokensIn);
        Assert.Equal(3, alphaMsg.TokensOut);

        var betaMsg = messages.Single(m => m.Label == "候选 B");
        Assert.Equal("beta-prov", betaMsg.ProviderId);
        Assert.Equal("ANSWER-B", betaMsg.Content);

        // judge 经真实 SSE 流式合成
        var judgeMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Judge);
        Assert.Equal("judge-prov", judgeMsg.ProviderId);
        Assert.Equal("裁决合成", judgeMsg.Content);
        Assert.Equal(MessageStatus.Completed, judgeMsg.Status);

        // 真实 HTTP：三次请求，候选非流式、judge 流式
        var bodies = ReceivedBodies;
        Assert.Equal(3, bodies.Count);
        var models = new List<(string Model, bool Stream)>();
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            models.Add((
                doc.RootElement.GetProperty("model").GetString()!,
                doc.RootElement.GetProperty("stream").GetBoolean()));
        }

        Assert.Contains(("alpha-model", false), models);
        Assert.Contains(("beta-model", false), models);
        Assert.Contains(("judge-model", true), models);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Ensemble, turn.Strategy);
        Assert.Equal(3, turn.TotalMessages);
    }

    [Fact]
    public async Task DecomposeOverRealHttp_PlanExecuteSynthesize_AllRealHttp()
    {
        SetProfile("planner-prov", new[] { ModelStrength.Planning });
        SetProfile("analyst-prov", new[] { ModelStrength.Analysis });
        SetProfile("writer-prov", new[] { ModelStrength.Writing });
        SetProfile("synth-prov", new[] { ModelStrength.General });
        Options.Planner = new ModelBinding("planner-prov", null);
        Options.Synthesizer = new ModelBinding("synth-prov", null);

        var (facade, _) = MakeRealFacade(
            "planner-prov",
            ("planner-prov", "decomp-planner"),
            ("analyst-prov", "analyst-model"),
            ("writer-prov", "writer-model"),
            ("synth-prov", "synth-model"));
        var session = await NewSessionAsync(OrchestrationStrategy.Decompose);

        var events = await CollectAsync(facade.SendAsync(session.Id, "调研并写一篇文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // planner 真实调用落库：规划 JSON 是中间产物（IsFinal=false）
        var plannerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Planner);
        Assert.Contains("steps", plannerMsg.Content);
        Assert.Equal(MessageStatus.Completed, plannerMsg.Status);
        Assert.False(plannerMsg.IsFinal);

        // 两个 worker 按强项分配并经真实 HTTP 各自完成（都是中间产物）
        var analyst = messages.Single(m => m.Label == "调研");
        Assert.Equal("analyst-prov", analyst.ProviderId);
        Assert.Equal("分析产出", analyst.Content);
        Assert.False(analyst.IsFinal);
        var writer = messages.Single(m => m.Label == "成文");
        Assert.Equal("writer-prov", writer.ProviderId);
        Assert.Equal("初稿内容", writer.Content);
        Assert.False(writer.IsFinal);

        // 依赖链真实传递：s2 的请求体携带 s1 的产出（解析后断言，避开线上 \u 转义差异）
        var writerBody = ReceivedBodies.Single(b =>
        {
            using var d = JsonDocument.Parse(b);
            return d.RootElement.GetProperty("model").GetString() == "writer-model";
        });
        using (var writerReq = JsonDocument.Parse(writerBody))
        {
            var workerPrompt = writerReq.RootElement.GetProperty("messages").EnumerateArray()
                .Last().GetProperty("content").GetString()!;
            Assert.Contains("分析产出", workerPrompt);
        }

        // synthesizer 经真实 SSE 流式合成最终答复（IsFinal=true）
        var synth = messages.Single(m => m.OrchestrationRole == StrategyRole.Synthesizer);
        Assert.Equal("synth-prov", synth.ProviderId);
        Assert.Equal("最终合成", synth.Content);
        Assert.Equal(MessageStatus.Completed, synth.Status);
        Assert.True(synth.IsFinal);

        // 合成提示词如实携带两份子任务产出
        var synthBody = ReceivedBodies.Single(b =>
        {
            using var d = JsonDocument.Parse(b);
            return d.RootElement.GetProperty("model").GetString() == "synth-model";
        });
        using (var synthReq = JsonDocument.Parse(synthBody))
        {
            var synthPrompt = synthReq.RootElement.GetProperty("messages").EnumerateArray()
                .Last().GetProperty("content").GetString()!;
            Assert.Contains("分析产出", synthPrompt);
            Assert.Contains("初稿内容", synthPrompt);
        }

        // 真实 HTTP：四次请求（planner/两 worker 非流式，synth 流式）
        var bodies = ReceivedBodies;
        Assert.Equal(4, bodies.Count);
        var models = new List<(string Model, bool Stream)>();
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            models.Add((
                doc.RootElement.GetProperty("model").GetString()!,
                doc.RootElement.TryGetProperty("stream", out var s)
                    && s.ValueKind == JsonValueKind.True));
        }

        Assert.Contains(("decomp-planner", false), models);
        Assert.Contains(("analyst-model", false), models);
        Assert.Contains(("writer-model", false), models);
        Assert.Contains(("synth-model", true), models);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Decompose, turn.Strategy);
        Assert.Equal(4, turn.TotalMessages);
    }

    [Fact]
    public async Task PipelineOverRealHttp_DraftReviewRevise_AllRealHttp()
    {
        SetProfile("writer-prov", new[] { ModelStrength.Writing });
        SetProfile("review-prov", new[] { ModelStrength.Review });
        Options.Judge = new ModelBinding("review-prov", null);

        var (facade, _) = MakeRealFacade(
            "writer-prov",
            ("writer-prov", "writer-model"),
            ("review-prov", "review-model"));
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一篇关于秋天的短文"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 起草 → 评审 → 修订：ParentMessageId 串成接力链，IsFinal 逐级如实
        var draft = messages.Single(m => m.Label == "起草");
        Assert.Equal("writer-prov", draft.ProviderId);
        Assert.Equal("初稿内容", draft.Content);
        Assert.False(draft.IsFinal);

        var review = messages.Single(m => m.Label == "评审");
        Assert.Equal("review-prov", review.ProviderId);
        Assert.Equal("评审意见", review.Content);
        Assert.Equal(draft.Id, review.ParentMessageId);
        Assert.False(review.IsFinal);

        var revise = messages.Single(m => m.Label == "修订终稿");
        Assert.Equal("writer-prov", revise.ProviderId);
        Assert.Equal("修订终稿", revise.Content); // 流式增量合成
        Assert.Equal(review.Id, revise.ParentMessageId);
        Assert.Equal(MessageStatus.Completed, revise.Status);
        Assert.True(revise.IsFinal);

        // 评审请求携带初稿正文
        var reviewBody = ReceivedBodies.Single(b =>
        {
            using var d = JsonDocument.Parse(b);
            return d.RootElement.GetProperty("model").GetString() == "review-model";
        });
        using (var reviewReq = JsonDocument.Parse(reviewBody))
        {
            var reviewPrompt = reviewReq.RootElement.GetProperty("messages").EnumerateArray()
                .Last().GetProperty("content").GetString()!;
            Assert.Contains("初稿内容", reviewPrompt);
        }

        // 修订请求（writer-model 的流式调用）同时携带初稿与评审意见
        var reviseBody = ReceivedBodies.Single(b =>
        {
            using var d = JsonDocument.Parse(b);
            return d.RootElement.GetProperty("model").GetString() == "writer-model"
                   && d.RootElement.TryGetProperty("stream", out var s)
                   && s.ValueKind == JsonValueKind.True;
        });
        using (var reviseReq = JsonDocument.Parse(reviseBody))
        {
            var revisePrompt = reviseReq.RootElement.GetProperty("messages").EnumerateArray()
                .Last().GetProperty("content").GetString()!;
            Assert.Contains("初稿内容", revisePrompt);
            Assert.Contains("评审意见", revisePrompt);
        }

        // 真实 HTTP：三次请求（起草/评审非流式，修订流式）
        Assert.Equal(3, ReceivedBodies.Count);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Pipeline, turn.Strategy);
        Assert.Equal(3, turn.TotalMessages);
    }
}
