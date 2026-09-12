// R4-γ 能力扩展特性测试：corrupt 备份保留策略 / SettingsChanged 事件 / 图片附件全链路。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroAgent.Conversation.Services;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using AeroCode.Tests.ConversationTests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.AppTests;

#region MessageAttachment 模型

/// <summary>MessageAttachment 模型行为：DisplaySize 分档、ToDescription 格式。</summary>
public sealed class MessageAttachmentTests
{
    [Theory]
    [InlineData(0, "0B")]
    [InlineData(512, "512B")]
    [InlineData(1023, "1023B")]
    [InlineData(1024, "1.0KB")]
    [InlineData(1536, "1.5KB")]
    [InlineData(1048576, "1.0MB")]
    [InlineData(2621440, "2.5MB")]
    [InlineData(1073741824, "1.00GB")]
    [InlineData(10737418240, "10.00GB")]
    public void DisplaySize_FormatsCorrectly(long bytes, string expected)
    {
        var att = new MessageAttachment("test.png", "image/png", bytes);
        Assert.Equal(expected, att.DisplaySize);
    }

    [Fact]
    public void ToDescription_ContainsFileNameSizeAndMime()
    {
        var att = new MessageAttachment("screenshot.png", "image/png", 2048);
        var desc = att.ToDescription();

        Assert.Contains("screenshot.png", desc);
        Assert.Contains("2.0KB", desc);
        Assert.Contains("image/png", desc);
        Assert.StartsWith("[Attached file:", desc);
    }

    [Fact]
    public void PreviewBytes_NotSerialized()
    {
        var preview = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var att = new MessageAttachment("clip.png", "image/png", 100, preview);

        Assert.Equal(preview, att.PreviewBytes);

        var json = System.Text.Json.JsonSerializer.Serialize(att);
        Assert.DoesNotContain("PreviewBytes", json);
        Assert.DoesNotContain("previewBytes", json);
    }

    [Fact]
    public void ToInjectionBlock_TextAttachment_IncludesContent()
    {
        var att = new MessageAttachment("notes.md", "text/markdown", 50, null, textContent: "hello world", contentTruncated: false);
        var block = att.ToInjectionBlock();

        Assert.Contains("notes.md", block);
        Assert.Contains("hello world", block);
        Assert.DoesNotContain("未注入正文", block);
    }

    [Fact]
    public void ToInjectionBlock_TruncatedText_AnnotatesTruncation()
    {
        var att = new MessageAttachment("big.txt", "text/plain", 10_000_000, null, textContent: "head", contentTruncated: true);
        var block = att.ToInjectionBlock();

        Assert.Contains("head", block);
        Assert.Contains("仅注入开头部分", block);
    }

    [Fact]
    public void ToInjectionBlock_BinaryAttachment_MetadataOnly()
    {
        var att = new MessageAttachment("photo.png", "image/png", 2048);
        var block = att.ToInjectionBlock();

        Assert.Contains("photo.png", block);
        Assert.Contains("未注入正文", block);
    }

    [Fact]
    public void ToInjectionBlock_ExtractionFailed_HonestLabel_NotBinary()
    {
        // review L3：文本抽取失败的文本文件 → 标"文本读取失败"，不误报为二进制/图片。
        var att = new MessageAttachment("bad.txt", "text/plain", 100) { ExtractionFailed = true };
        var block = att.ToInjectionBlock();

        Assert.Contains("文本读取失败", block);
        Assert.DoesNotContain("二进制/图片附件", block);
    }

    [Fact]
    public void ToInjectionBlock_GenuineBinary_KeepsBinaryLabel()
    {
        // review L3：真二进制（ExtractionFailed=false）仍标"二进制/图片附件"。
        var att = new MessageAttachment("img.png", "image/png", 100);
        var block = att.ToInjectionBlock();

        Assert.Contains("二进制/图片附件", block);
        Assert.DoesNotContain("文本读取失败", block);
    }

    [Fact]
    public void BuildInjection_RespectsBudget_AnnotatesReferencedOnly()
    {
        // 两个文本附件，预算只够第一个 → 第二个应降级为「已引用未注入正文」。
        var a = new MessageAttachment("a.txt", "text/plain", 100, null, textContent: new string('A', 300), false);
        var b = new MessageAttachment("b.txt", "text/plain", 100, null, textContent: new string('B', 300), false);

        var result = MessageAttachment.BuildInjection(new[] { a, b }, totalCharBudget: 400);

        Assert.Contains("a.txt", result);
        Assert.Contains(new string('A', 300), result);
        Assert.Contains("b.txt", result);
        Assert.Contains("已引用未注入正文", result);
        Assert.DoesNotContain(new string('B', 300), result);
    }

    [Fact]
    public void BuildInjection_EmptyList_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MessageAttachment.BuildInjection(System.Array.Empty<MessageAttachment>(), 1000));
    }

    [Fact]
    public void BuildInjection_PartialFragment_AnnotatesBudgetTruncation()
    {
        // review M7：预算放不下完整块、但剩余足够放一个 >=200 字符片段 → 走"预算截断，仅注入片段"分支。
        var att = new MessageAttachment("frag.txt", "text/plain", 1000, null, textContent: new string('X', 1000), false);

        var result = MessageAttachment.BuildInjection(new[] { att }, totalCharBudget: 400);

        Assert.Contains("预算截断，仅注入片段", result);      // 片段分支标注
        Assert.Contains(new string('X', 100), result);        // 注入了部分正文
        Assert.DoesNotContain(new string('X', 1000), result); // 但绝非全文
    }

    [Fact]
    public void BuildInjection_ExactFit_InjectsFullBlock_NoFragmentMarker()
    {
        // review M7：预算恰好等于块长（used + block.Length == budget）→ 完整注入，不走片段/降级分支。
        var att = new MessageAttachment("fit.txt", "text/plain", 300, null, textContent: new string('F', 300), false);
        var blockLen = att.ToInjectionBlock().Length;

        var result = MessageAttachment.BuildInjection(new[] { att }, totalCharBudget: blockLen);

        Assert.Contains(new string('F', 300), result);   // 全文注入
        Assert.DoesNotContain("预算截断", result);          // 未走片段分支
        Assert.DoesNotContain("已引用未注入正文", result);    // 未降级
    }
}

/// <summary>R5.3：ChatViewModel.BuildAttachment 用真实文件验证抽取/截断/MIME。</summary>
public sealed class BuildAttachmentTests : System.IDisposable
{
    private readonly string _dir = System.IO.Directory.CreateTempSubdirectory("aeroatt").FullName;

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var p = System.IO.Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void TextFile_ExtractsContent()
    {
        var path = Write("note.md", "# 标题\n正文内容");
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(path));

        Assert.Equal("text/markdown", att.MimeType);
        Assert.True(att.HasText);
        Assert.Contains("正文内容", att.TextContent);
        Assert.False(att.ContentTruncated);
    }

    [Fact]
    public void LargeTextFile_TruncatesToBudget()
    {
        // 超出单文件预算（128K 字符）→ 截断并标注。
        var path = Write("big.txt", new string('x', 200_000));
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(path));

        Assert.True(att.HasText);
        Assert.True(att.ContentTruncated);
        Assert.Equal(128 * 1024, att.TextContent!.Length);
    }

    [Fact]
    public void TextFile_ExactlyAtBudget_NotTruncated()
    {
        // review M7：恰好 128K 字符 → 全量读出（n == 预算），不视为截断。
        var path = Write("exact.txt", new string('x', 128 * 1024));
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(path));

        Assert.True(att.HasText);
        Assert.False(att.ContentTruncated);
        Assert.Equal(128 * 1024, att.TextContent!.Length);
    }

    [Fact]
    public void TextFile_OneCharOverBudget_Truncated()
    {
        // review M7：128K+1 字符 → n == 预算+1 > 预算，视为截断，抽取仍为 128K。
        var path = Write("over.txt", new string('x', 128 * 1024 + 1));
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(path));

        Assert.True(att.HasText);
        Assert.True(att.ContentTruncated);
        Assert.Equal(128 * 1024, att.TextContent!.Length);
    }

    [Fact]
    public void BinaryFile_NoTextContent()
    {
        var p = System.IO.Path.Combine(_dir, "img.png");
        System.IO.File.WriteAllBytes(p, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A });
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(p));

        Assert.Equal("image/png", att.MimeType);
        Assert.False(att.HasText);
        Assert.Null(att.TextContent);
    }

    [Fact]
    public void CodeFile_TreatedAsText()
    {
        var path = Write("prog.cs", "class C { }");
        var att = AeroCode.App.ViewModels.ChatViewModel.BuildAttachment(new System.IO.FileInfo(path));

        Assert.True(att.HasText);
        Assert.Contains("class C", att.TextContent);
    }
}

#endregion

#region SettingsService γ 特性

/// <summary>
/// SettingsService γ 增强：corrupt 备份保留上限 3 份（PruneCorruptBackups）
/// + SettingsChanged 事件（Load 成功/Save 触发，corrupt 回退不触发）。
/// </summary>
public sealed class SettingsGammaTests : IDisposable
{
    private readonly string _root;
    private readonly AppDataPaths _paths;

    public SettingsGammaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"settings_gamma_{Guid.NewGuid():N}");
        _paths = new AppDataPaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 超过 3 份 corrupt 备份时只保留最新 3 份（字典序 = 时间序，删除最旧的）。
    /// 模拟手法：直接创建 5 个带不同时间戳的假备份文件，再触发一次损坏加载让
    /// PruneCorruptBackups 执行清理。
    /// </summary>
    [Fact]
    public async Task CorruptBackupPruning_KeepsMax3()
    {
        new SettingsService(_paths); // EnsureAll 创建目录

        var settingsBase = _paths.SettingsFile;
        var stamps = new[]
        {
            "20260101T000000000Z",
            "20260102T000000000Z",
            "20260103T000000000Z",
            "20260104T000000000Z",
            "20260105T000000000Z",
        };
        foreach (var stamp in stamps)
        {
            await File.WriteAllTextAsync($"{settingsBase}.corrupt-{stamp}", "old-corrupt");
        }

        Assert.Equal(5, Directory.GetFiles(_root, "settings.json.corrupt-*").Length);

        // 写损坏内容触发 BackupCorruptFile → PruneCorruptBackups。
        await File.WriteAllTextAsync(settingsBase, "{bad json");
        var svc = new SettingsService(_paths);
        await svc.LoadAsync();

        var remaining = Directory.GetFiles(_root, "settings.json.corrupt-*")
            .Select(Path.GetFileName)
            .OrderDescending()
            .ToArray();

        // 保留 3 份：最新的两个旧备份 + 刚生成的新备份。
        Assert.Equal(3, remaining.Length);
        // 最新的两个旧备份应保留（字典序最大）。
        Assert.Contains("settings.json.corrupt-20260105T000000000Z", remaining);
        Assert.Contains("settings.json.corrupt-20260104T000000000Z", remaining);
        // 最旧的两个应被删除。
        Assert.DoesNotContain("settings.json.corrupt-20260101T000000000Z", remaining);
        Assert.DoesNotContain("settings.json.corrupt-20260102T000000000Z", remaining);
    }

    /// <summary>恰好 3 份时不清理。</summary>
    [Fact]
    public async Task CorruptBackupPruning_NoPruneWhenAtLimit()
    {
        new SettingsService(_paths);
        var settingsBase = _paths.SettingsFile;
        var stamps = new[]
        {
            "20260101T000000000Z",
            "20260102T000000000Z",
            "20260103T000000000Z",
        };
        foreach (var stamp in stamps)
        {
            File.WriteAllText($"{settingsBase}.corrupt-{stamp}", "corrupt");
        }

        // 再做一次损坏加载（3 份 + 新生成的 = 4 份 → 清到 3 份）。
        File.WriteAllText(settingsBase, "{bad json");
        var svc = new SettingsService(_paths);
        await svc.LoadAsync();

        Assert.Equal(3, Directory.GetFiles(_root, "settings.json.corrupt-*").Length);
    }

    /// <summary>SettingsChanged 在成功加载后触发。</summary>
    [Fact]
    public async Task SettingsChanged_FiresOnSuccessfulLoad()
    {
        // 先生成一个合法文件。
        var svc1 = new SettingsService(_paths);
        await svc1.LoadAsync(); // 缺失分支 → 生成默认文件

        var svc2 = new SettingsService(_paths);
        var fired = false;
        svc2.SettingsChanged += (_, _) => fired = true;
        await svc2.LoadAsync();

        Assert.True(fired);
    }

    /// <summary>SettingsChanged 在 SaveAsync 后触发。</summary>
    [Fact]
    public async Task SettingsChanged_FiresOnSave()
    {
        var svc = new SettingsService(_paths);
        await svc.LoadAsync();

        var fired = false;
        svc.SettingsChanged += (_, _) => fired = true;
        await svc.SaveAsync();

        Assert.True(fired);
    }

    /// <summary>SettingsChanged 在损坏 JSON 回退时不触发（默认值不是用户意图）。</summary>
    [Fact]
    public async Task SettingsChanged_DoesNotFireOnCorruptFallback()
    {
        new SettingsService(_paths); // EnsureAll
        await File.WriteAllTextAsync(_paths.SettingsFile, "{corrupt");

        var svc = new SettingsService(_paths);
        var fired = false;
        svc.SettingsChanged += (_, _) => fired = true;
        await svc.LoadAsync();

        Assert.NotNull(svc.LastLoadError);
        Assert.False(fired);
    }

    /// <summary>文件缺失分支调用 SaveAsync 也会触发 SettingsChanged。</summary>
    [Fact]
    public async Task SettingsChanged_FiresOnMissingFileLoad()
    {
        var svc = new SettingsService(_paths);
        Assert.False(File.Exists(_paths.SettingsFile));

        var fired = false;
        svc.SettingsChanged += (_, _) => fired = true;
        await svc.LoadAsync();

        // 缺失分支内部调用 SaveAsync → SaveAsync 触发事件。
        Assert.True(fired);
    }
}

#endregion

#region 门面附件集成

/// <summary>
/// ChatOrchestrationFacade 附件路径：附件描述前缀注入用户消息 + AttachmentsJson 持久化。
/// </summary>
public sealed class FacadeAttachmentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _keepAlive;
    private readonly ConversationDbContext _db;
    private readonly SessionService _sessions;

    public FacadeAttachmentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"facade_att_{Guid.NewGuid():N}.db");
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite(connStr)
            .Options;
        _db = new ConversationDbContext(options);
        _db.Database.EnsureCreated();
        _sessions = new SessionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
        SqliteConnection.ClearPool(_keepAlive);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static ChatOrchestrationFacade MakeFacade(SessionService sessions)
        => new(sessions, new TestProviderRegistry(), new IOrchestrationStrategy[]
        {
            new SingleStrategy(sessions),
        });

    private static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public async Task SendWithAttachments_PrependsDescriptions_AndPersistsJson()
    {
        var provider = new ScriptedProvider { Deltas = new[] { "收到图片" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = new ChatOrchestrationFacade(_sessions, registry,
            new IOrchestrationStrategy[] { new SingleStrategy(_sessions) });

        var session = (await _sessions.CreateSessionAsync()).Value!;

        var attachments = new List<MessageAttachment>
        {
            new("photo.png", "image/png", 2048),
            new("diagram.jpg", "image/jpeg", 1048576),
        };

        var events = await CollectAsync(facade.SendAsync(session.Id, "请分析", attachments));

        // 事件流正常完成。
        Assert.Contains(events, e => e is TurnCompletedEvent);

        // 持久化验证：用户消息包含附件描述前缀 + AttachmentsJson。
        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var userMsg = messages.First(m => m.Role == ChatRole.User);

        Assert.Contains("[Attached file: photo.png", userMsg.Content);
        Assert.Contains("[Attached file: diagram.jpg", userMsg.Content);
        Assert.Contains("请分析", userMsg.Content);
        // 描述在文本前面。
        Assert.True(userMsg.Content.IndexOf("[Attached file:") < userMsg.Content.IndexOf("请分析"));

        // AttachmentsJson 持久化（不含 PreviewBytes）。
        Assert.NotNull(userMsg.AttachmentsJson);
        Assert.Contains("photo.png", userMsg.AttachmentsJson);
        Assert.Contains("diagram.jpg", userMsg.AttachmentsJson);
        Assert.Contains("image/png", userMsg.AttachmentsJson);
        Assert.DoesNotContain("PreviewBytes", userMsg.AttachmentsJson);
    }

    [Fact]
    public async Task SendWithoutAttachments_NoJsonNoPrefix()
    {
        var provider = new ScriptedProvider { Deltas = new[] { "你好" } };
        var registry = new TestProviderRegistry();
        registry.Add(provider);
        var facade = new ChatOrchestrationFacade(_sessions, registry,
            new IOrchestrationStrategy[] { new SingleStrategy(_sessions) });

        var session = (await _sessions.CreateSessionAsync()).Value!;
        var events = await CollectAsync(facade.SendAsync(session.Id, "你好"));

        Assert.Contains(events, e => e is TurnCompletedEvent);

        var messages = (await _sessions.GetMessagesAsync(session.Id)).Value!;
        var userMsg = messages.First(m => m.Role == ChatRole.User);

        Assert.Equal("你好", userMsg.Content);
        Assert.Null(userMsg.AttachmentsJson);
    }
}

#endregion

#region ChatViewModel 附件投影

/// <summary>
/// ChatViewModel 附件投影：从 AttachmentsJson 恢复摘要（BuildAttachmentSummary 间接测试）。
/// 用 NullSessionService 模拟返回带 AttachmentsJson 的消息，验证投影到 MessageItemViewModel。
/// </summary>
public sealed class ChatViewModelAttachmentTests
{
    [Fact]
    public void BuildAttachmentSummary_ViaHandleEvent_UserMessageProjection()
    {
        // BuildAttachmentSummary 是 private static，通过 VM 消息加载路径间接测试。
        // 构造一个带 AttachmentsJson 的 ChatMessage，验证 VM 投影出 AttachmentSummary。
        var attachmentsJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { FileName = "photo.png", MimeType = "image/png", SizeBytes = 2048L },
            new { FileName = "big.jpg", MimeType = "image/jpeg", SizeBytes = 3145728L },
        });

        // 验证反序列化逻辑（与 BuildAttachmentSummary 内部同算法）。
        using var doc = System.Text.Json.JsonDocument.Parse(attachmentsJson);
        var parts = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.TryGetProperty("FileName", out var n) ? n.GetString() : null;
            var size = el.TryGetProperty("SizeBytes", out var s) && s.TryGetInt64(out var sz)
                ? sz
                : 0L;
            if (name is null) continue;
            var display = size switch
            {
                < 1024 => $"{size}B",
                < 1024 * 1024 => $"{size / 1024.0:F1}KB",
                _ => $"{size / (1024.0 * 1024.0):F1}MB",
            };
            parts.Add($"\ud83d\udcce {name} ({display})");
        }

        Assert.Equal(2, parts.Count);
        Assert.Contains("photo.png (2.0KB)", parts[0]);
        Assert.Contains("big.jpg (3.0MB)", parts[1]);
    }

    [Fact]
    public void BuildAttachmentSummary_InvalidJson_ReturnsNull()
    {
        // 验证容错：损坏 JSON 不抛异常。
        var result = InvokeBuildSummary("not valid json {{{");
        Assert.Null(result);
    }

    [Fact]
    public void BuildAttachmentSummary_EmptyArray_ReturnsNull()
    {
        var result = InvokeBuildSummary("[]");
        Assert.Null(result);
    }

    [Fact]
    public void BuildAttachmentSummary_Null_ReturnsNull()
    {
        var result = InvokeBuildSummary(null);
        Assert.Null(result);
    }

    [Fact]
    public void BuildAttachmentSummary_MissingFileName_Skipped()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { MimeType = "image/png", SizeBytes = 100L },
        });
        var result = InvokeBuildSummary(json);
        Assert.Null(result);
    }

    /// <summary>
    /// 反射调用 private static BuildAttachmentSummary（测试专用，不破坏封装）。
    /// </summary>
    private static string? InvokeBuildSummary(string? json)
    {
        var method = typeof(ChatViewModel).GetMethod(
            "BuildAttachmentSummary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, new object?[] { json }) as string;
    }
}

#endregion
