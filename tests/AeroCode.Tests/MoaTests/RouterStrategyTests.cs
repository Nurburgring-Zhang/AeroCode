using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>路由策略：LLM 分类 → 按画像路由；LLM 不可用时启发式兜底。</summary>
public sealed class RouterStrategyTests : MoaTestBase
{
    private RouterStrategy MakeStrategy() => new(Runner, Resolver, Options);

    [Fact]
    public async Task Router_LlmCategory_RoutesToMatchingModel()
    {
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.ChatQueue.Enqueue("{\"category\":\"code\",\"reason\":\"代码请求\"}");

        var coder = AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        coder.Deltas = new[] { "这是", "代码实现" };

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一个排序函数"));

        // 路由决策与 worker 答复都是真实消息，归属角色正确
        var starts = events.OfType<AssistantMessageStarted>().ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(StrategyRole.Router, starts[0].OrchestrationRole);
        Assert.Equal("fast", starts[0].ProviderId);
        Assert.Equal(StrategyRole.Worker, starts[1].OrchestrationRole);
        Assert.Equal("coder", starts[1].ProviderId);
        Assert.Equal(starts[0].MessageId, starts[1].ParentMessageId); // worker 挂在路由决策下

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var worker = messages.Single(m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Equal("这是代码实现", worker.Content);
        Assert.Equal(MessageStatus.Completed, worker.Status);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Router, turn.Strategy);
        Assert.Equal(2, turn.TotalMessages);
    }

    [Fact]
    public async Task Router_MalformedLlmOutput_FallsBackToHeuristic()
    {
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.NonStreamContent = "抱歉，我无法输出 JSON"; // 不可解析

        var translator = AddProvider("translator");
        SetProfile("translator", new[] { ModelStrength.Translation });
        translator.Deltas = new[] { "译文" };

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "翻译这句话"));

        var worker = events.OfType<AssistantMessageStarted>()
            .Single(s => s.OrchestrationRole == StrategyRole.Worker);
        Assert.Equal("translator", worker.ProviderId); // 启发式识别为 translation
    }

    [Fact]
    public async Task Router_RouterModelDown_StillAnswersViaHeuristic()
    {
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.ThrowDuringChat = new System.InvalidOperationException("router 上游 503");

        var math = AddProvider("math");
        SetProfile("math", new[] { ModelStrength.Math });
        math.Deltas = new[] { "42" };

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "计算 6 乘 7"));

        // 路由消息如实落库为失败
        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var routerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Router);
        Assert.Equal(MessageStatus.Failed, routerMsg.Status);
        Assert.Contains("router 上游 503", routerMsg.Error);

        // 但整轮仍有答复（启发式路由到 math）
        var workerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Equal("math", workerMsg.ProviderId);
        Assert.Equal("42", workerMsg.Content);
        Assert.DoesNotContain(events, e => e is MessageFailedEvent f && f.MessageId == string.Empty && f.Error.Contains("没有已配置"));
    }

    [Fact]
    public async Task Router_NoModels_FailsHonestly()
    {
        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        var events = await CollectAsync(facade.SendAsync(session.Id, "你好"));

        Assert.Contains(events, e => e is MessageFailedEvent f && f.Error.Contains("没有已配置的模型"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Router_ExplicitBinding_OverridesAutoAssignment()
    {
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.ChatQueue.Enqueue("{\"category\":\"general\"}");

        var designated = AddProvider("designated");
        SetProfile("designated", new[] { ModelStrength.General });
        designated.Deltas = new[] { "指定模型回答" };

        Options.Router = new ModelBinding("fast", null);

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);
        var events = await CollectAsync(facade.SendAsync(session.Id, "随便聊聊"));

        var routerStart = events.OfType<AssistantMessageStarted>()
            .Single(s => s.OrchestrationRole == StrategyRole.Router);
        Assert.Equal("fast", routerStart.ProviderId); // 绑定生效
    }

    [Fact]
    public async Task Router_GhostProviderBinding_FallsBackToAutoAssignment_StillAnswers()
    {
        // 孤儿绑定运行期回归（Reviewer-A P2-4）：设置页允许绑定指向已删除的 provider
        // （如实列"（未配置）"、绝不静默重置）；运行期 ModelResolver 对这类绑定
        // 必须回退到画像自动分配，整轮照常出答，而不是抛异常/失败。
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.ChatQueue.Enqueue("{\"category\":\"code\",\"reason\":\"代码请求\"}"); // 分类调用落到自动分配的 fast

        var coder = AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        coder.Deltas = new[] { "自动分配回答" };

        Options.Router = new ModelBinding("ghost-provider", null); // 注册表里不存在

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);
        var events = await CollectAsync(facade.SendAsync(session.Id, "写一个排序函数"));

        // 整轮成功：路由分类走回退分配，最终答复产出，无失败事件
        Assert.DoesNotContain(events, e => e is MessageFailedEvent);
        Assert.IsType<TurnCompletedEvent>(events[^1]);

        var routerStart = events.OfType<AssistantMessageStarted>()
            .Single(s => s.OrchestrationRole == StrategyRole.Router);
        Assert.Equal("fast", routerStart.ProviderId); // 回退到画像打分指派

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var workerMsg = messages.Single(m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.Equal("coder", workerMsg.ProviderId); // code 类别路由到 Code 强项
        Assert.Equal("自动分配回答", workerMsg.Content);
    }

    [Fact]
    public async Task Router_SecondTurn_ContextExcludesClassification()
    {
        // P1-1 端到端回归：第二轮调用时，第一轮的"路由分类"中间产物（IsFinal=false）
        // 不得出现在发给模型的上下文里，而第一轮最终答复（IsFinal=true）必须在。
        var fast = AddProvider("fast");
        SetProfile("fast", new[] { ModelStrength.General }, speed: SpeedTier.Fast);
        fast.ChatQueue.Enqueue("{\"category\":\"code\",\"reason\":\"代码请求\"}"); // 第一轮分类
        fast.ChatQueue.Enqueue("{\"category\":\"code\",\"reason\":\"仍是代码\"}"); // 第二轮分类

        var coder = AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        coder.Deltas = new[] { "第一轮答复" };

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Router);

        await CollectAsync(facade.SendAsync(session.Id, "写一个排序函数"));

        // 中间产物与最终答复的 IsFinal 标记如实落库
        var turn1 = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var routerMsg = turn1.Single(m => m.OrchestrationRole == StrategyRole.Router);
        var workerMsg = turn1.Single(m => m.OrchestrationRole == StrategyRole.Worker);
        Assert.False(routerMsg.IsFinal);
        Assert.True(workerMsg.IsFinal);

        // ---- 第二轮 ----
        coder.Deltas = new[] { "第二轮答复" };
        await CollectAsync(facade.SendAsync(session.Id, "再补一个单元测试"));

        Assert.NotNull(coder.LastRequestMessages);
        var secondRequest = coder.LastRequestMessages!;

        // 上下文 = user1 + 最终答复 + user2；分类 JSON 不在其中
        var roles = secondRequest.Select(m => m.Role).ToArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, roles);
        Assert.Equal("写一个排序函数", secondRequest[0].Content);
        Assert.Equal("第一轮答复", secondRequest[1].Content);
        Assert.Equal("再补一个单元测试", secondRequest[2].Content);
        Assert.DoesNotContain(secondRequest, m => m.Content.Contains("category"));
    }

    [Theory]
    [InlineData("帮我翻译这段英文", ModelStrength.Translation)]
    [InlineData("写一首关于秋天的诗", ModelStrength.Writing)]
    [InlineData("证明根号2是无理数", ModelStrength.Math)]
    [InlineData("评审这份设计文档", ModelStrength.Review)]
    [InlineData("总结这篇文章的要点", ModelStrength.Analysis)]
    [InlineData("制定一个学习计划", ModelStrength.Planning)]
    [InlineData("实现一个 HTTP 服务器", ModelStrength.Code)]
    [InlineData("今天天气怎么样", ModelStrength.General)]
    public void HeuristicCategory_ClassifiesCommonRequests(string text, string expected)
    {
        Assert.Equal(expected, RouterStrategy.HeuristicCategory(text));
    }

    [Fact]
    public void ParseCategory_JsonWithProse_ExtractsCategory()
    {
        var parsed = RouterStrategy.ParseCategory(
            "好的，分类结果是 {\"category\":\"math\",\"reason\":\"数学\"} 完毕", "x");
        Assert.Equal(ModelStrength.Math, parsed);
    }

    [Fact]
    public void ParseCategory_UnknownCategory_FallsBackToHeuristic()
    {
        var parsed = RouterStrategy.ParseCategory(
            "{\"category\":\"不存在的类别\"}", "翻译这个");
        Assert.Equal(ModelStrength.Translation, parsed);
    }
}
