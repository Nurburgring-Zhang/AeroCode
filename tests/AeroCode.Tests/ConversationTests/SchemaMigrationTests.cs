using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AeroAgent.Conversation.Data;
using AeroAgent.Conversation.Models;
using AeroAgent.Conversation.Orchestration;
using AeroCode.Tests.MoaTests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AeroCode.Tests.ConversationTests;

/// <summary>
/// 存量库升级路径回归：Phase 1 形态的库（无 Label/IsFinal 列）经
/// EnsureSchemaAsync 补列后，EF 查询/写入必须立即可用——
/// 补列列名与 EF 映射列名不一致会让存量用户升级即崩（no such column）。
/// </summary>
public sealed class SchemaMigrationTests : MoaTestBase
{
    /// <summary>把当前库改回 Phase 1 形态：删掉 Label 与 IsFinal 两列。</summary>
    private async Task DropPhase2ColumnsAsync()
    {
        await Db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE chat_messages DROP COLUMN \"Label\";");
        await Db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE chat_messages DROP COLUMN \"IsFinal\";");
        Assert.False(await ColumnExistsAsync("Label"));
        Assert.False(await ColumnExistsAsync("IsFinal"));
    }

    private async Task<bool> ColumnExistsAsync(string columnName)
    {
        var conn = Db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(chat_messages);";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<List<string>> ListColumnsAsync()
    {
        var names = new List<string>();
        var conn = Db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(chat_messages);";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }

            return names;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Phase1Database_EnsureSchemaBackfillsColumns_EfQueriesWork()
    {
        await DropPhase2ColumnsAsync();

        // 模拟 App 启动序列：EnsureCreated（不补已存在表的列）→ EnsureSchemaAsync
        Db.Database.EnsureCreated();
        await ConversationDbContext.EnsureSchemaAsync(Db);

        Assert.True(await ColumnExistsAsync("Label"));
        Assert.True(await ColumnExistsAsync("IsFinal"));

        // 升级后立即真实读写：写入带 IsFinal/Label 的消息并经 EF 查回
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "升级回归",
            Strategy = OrchestrationStrategy.Single,
        };
        Db.Sessions.Add(session);
        await Db.SaveChangesAsync();

        Db.Messages.Add(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = session.Id,
            Role = ChatRole.Assistant,
            Content = "编排中间产物",
            Label = "子任务甲",
            IsFinal = false,
            Status = MessageStatus.Completed,
        });
        await Db.SaveChangesAsync();

        var loaded = await Db.Messages
            .Where(m => m.SessionId == session.Id && m.IsFinal == false)
            .SingleAsync();
        Assert.Equal("子任务甲", loaded.Label);
        Assert.Equal(false, loaded.IsFinal);
    }

    [Fact]
    public async Task FreshDatabase_EnsureSchemaTwice_NoDuplicateColumns()
    {
        // 全新库：EnsureCreated 已建好列；EnsureSchemaAsync 必须幂等（不重复 ADD）
        await ConversationDbContext.EnsureSchemaAsync(Db);
        await ConversationDbContext.EnsureSchemaAsync(Db);

        var columns = await ListColumnsAsync();
        Assert.Equal(1, columns.Count(c => string.Equals(c, "Label", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, columns.Count(c => string.Equals(c, "IsFinal", StringComparison.OrdinalIgnoreCase)));
    }
}
