using System;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Moa.Profiles;
using AeroAgent.Moa.Strategies;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>流水线策略：起草 → 评审 → 修订接力；阶段产物落库成链，失败即如实停止。</summary>
public sealed class PipelineStrategyTests : MoaTestBase
{
    private PipelineStrategy MakeStrategy() => new(Runner, Resolver, Options);

    /// <summary>标准接力阵容：writing 起草/修订 + review 评审。</summary>
    private (AeroCode.Tests.ConversationTests.ScriptedProvider Writer,
             AeroCode.Tests.ConversationTests.ScriptedProvider Reviewer) SetupRelay()
    {
        var writer = AddProvider("writer");
        SetProfile("writer", new[] { ModelStrength.Writing });
        writer.NonStreamContent = "DRAFT-V1"; // 阶段 1 非流式
        writer.Deltas = new[] { "终稿", "正文" }; // 阶段 3 流式

        var reviewer = AddProvider("reviewer");
        SetProfile("reviewer", new[] { ModelStrength.Review });
        reviewer.NonStreamContent = "REVIEW-NOTES";

        return (writer, reviewer);
    }

    [Fact]
    public async Task Pipeline_ThreeStages_ChainAndContentFlow()
    {
        var relay = SetupRelay();
        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一篇公众号文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var assistants = messages.Where(m => m.Role == ChatRole.Assistant).ToList();
        Assert.Equal(3, assistants.Count);

        // 阶段 1：起草（Worker，非流式）
        var draft = assistants.Single(m => m.Label == "起草");
        Assert.Equal("writer", draft.ProviderId);
        Assert.Equal(StrategyRole.Worker, draft.OrchestrationRole);
        Assert.Equal("DRAFT-V1", draft.Content);
        Assert.Equal(MessageStatus.Completed, draft.Status);
        Assert.Null(draft.ParentMessageId);

        // 阶段 2：评审（Judge 角色，挂在草稿下）
        var review = assistants.Single(m => m.Label == "评审");
        Assert.Equal("reviewer", review.ProviderId);
        Assert.Equal(StrategyRole.Judge, review.OrchestrationRole);
        Assert.Equal(draft.Id, review.ParentMessageId);
        Assert.Equal("REVIEW-NOTES", review.Content);

        // 阶段 3：修订终稿（Worker，流式，挂在评审下）
        var revise = assistants.Single(m => m.Label == "修订终稿");
        Assert.Equal("writer", revise.ProviderId);
        Assert.Equal(review.Id, revise.ParentMessageId);
        Assert.Equal("终稿正文", revise.Content);
        Assert.Equal(MessageStatus.Completed, revise.Status);

        // 内容流转真实：评审拿到初稿，修订同时拿到初稿与评审意见
        var reviewPrompt = relay.Reviewer.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("写一篇公众号文章", reviewPrompt);
        Assert.Contains("DRAFT-V1", reviewPrompt);

        var revisePrompt = relay.Writer.LastRequestMessages!.Single(m => m.Role == "user").Content;
        Assert.Contains("DRAFT-V1", revisePrompt);
        Assert.Contains("REVIEW-NOTES", revisePrompt);

        // 最终答复流式送达
        var deltas = events.OfType<TextDeltaEvent>()
            .Where(d => d.MessageId == revise.Id)
            .Select(d => d.Delta)
            .ToArray();
        Assert.Equal(new[] { "终稿", "正文" }, deltas);

        var turn = events.OfType<TurnCompletedEvent>().Single();
        Assert.Equal(OrchestrationStrategy.Pipeline, turn.Strategy);
        Assert.Equal(3, turn.TotalMessages);
    }

    [Fact]
    public async Task Pipeline_CodeRequest_DraftsViaCodeStrength()
    {
        var coder = AddProvider("coder");
        SetProfile("coder", new[] { ModelStrength.Code });
        coder.NonStreamContent = "CODE-DRAFT";
        coder.Deltas = new[] { "修订", "代码" };

        var writer = AddProvider("writer");
        SetProfile("writer", new[] { ModelStrength.Writing });
        writer.NonStreamContent = "不该是我";

        var reviewer = AddProvider("reviewer");
        SetProfile("reviewer", new[] { ModelStrength.Review });
        reviewer.NonStreamContent = "REVIEW-NOTES";

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        await CollectAsync(facade.SendAsync(session.Id, "实现一个缓存函数"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 请求含代码关键词 → 起草/修订走 code 画像而非 writing
        var draft = messages.Single(m => m.Label == "起草");
        Assert.Equal("coder", draft.ProviderId);
        var revise = messages.Single(m => m.Label == "修订终稿");
        Assert.Equal("coder", revise.ProviderId);
        Assert.DoesNotContain(messages, m => m.ProviderId == "writer");
    }

    [Fact]
    public async Task Pipeline_DraftFails_StopsHonestly()
    {
        var relay = SetupRelay();
        relay.Writer.ThrowDuringChat = new InvalidOperationException("draft 上游 502");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一篇公众号文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var assistants = messages.Where(m => m.Role == ChatRole.Assistant).ToList();

        // 只有起草一条消息，且如实失败；后续阶段不启动
        var draft = Assert.Single(assistants);
        Assert.Equal(MessageStatus.Failed, draft.Status);
        Assert.Contains("draft 上游 502", draft.Error);
        Assert.DoesNotContain(messages, m => m.Label == "评审" || m.Label == "修订终稿");

        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.MessageId == draft.Id && f.Error.Contains("draft 上游 502"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Pipeline_ReviewFails_DraftKept_RevisionSkipped()
    {
        var relay = SetupRelay();
        relay.Reviewer.ThrowDuringChat = new InvalidOperationException("review 熔断");

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写一篇公众号文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;

        // 草稿保留为 Completed，评审如实失败，修订未发生
        var draft = messages.Single(m => m.Label == "起草");
        Assert.Equal(MessageStatus.Completed, draft.Status);
        var review = messages.Single(m => m.Label == "评审");
        Assert.Equal(MessageStatus.Failed, review.Status);
        Assert.Contains("review 熔断", review.Error);
        Assert.DoesNotContain(messages, m => m.Label == "修订终稿");
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Pipeline_NoModels_FailsHonestly()
    {
        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        var events = await CollectAsync(facade.SendAsync(session.Id, "写点什么"));

        Assert.Contains(events, e => e is MessageFailedEvent f
            && f.Error.Contains("没有已配置的模型可用于起草"));
        Assert.IsType<TurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Pipeline_ExplicitJudgeBinding_UsedForReview()
    {
        var relay = SetupRelay();
        // 另一个 review 强项模型：无绑定时自动分配会与 pinned 竞争
        var autoReviewer = AddProvider("auto-reviewer");
        SetProfile("auto-reviewer", new[] { ModelStrength.Review });
        autoReviewer.NonStreamContent = "不该是我";

        var pinned = AddProvider("pinned");
        SetProfile("pinned", new[] { ModelStrength.General });
        pinned.NonStreamContent = "PINNED-REVIEW";
        Options.Judge = new ModelBinding("pinned", null);

        var facade = MakeFacade(MakeStrategy());
        var session = await NewSessionAsync(OrchestrationStrategy.Pipeline);

        await CollectAsync(facade.SendAsync(session.Id, "写一篇公众号文章"));

        var messages = (await Sessions.GetMessagesAsync(session.Id)).Value!;
        var review = messages.Single(m => m.Label == "评审");
        Assert.Equal("pinned", review.ProviderId); // 绑定优先
        Assert.Equal("PINNED-REVIEW", review.Content);
        Assert.DoesNotContain(messages, m => m.ProviderId == "auto-reviewer");
    }
}
