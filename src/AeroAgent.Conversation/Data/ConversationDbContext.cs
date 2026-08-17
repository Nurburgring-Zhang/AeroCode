using System;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Conversation.Models;
using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Conversation.Data;

/// <summary>
/// 统一对话持久化上下文。chat_sessions / chat_messages 两张表，
/// 消息自带 MOA 归属列（provider/model/策略角色/成本/延迟）。
/// </summary>
public class ConversationDbContext : DbContext
{
    public ConversationDbContext(DbContextOptions<ConversationDbContext> options) : base(options)
    {
    }

    public DbSet<ChatSession> Sessions => Set<ChatSession>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    /// <summary>
    /// 既有数据库的兼容升级。EnsureCreated 不会给已存在的库补列：
    /// Phase 1 的库没有 chat_messages.Label（Phase 2 MOA 子任务标签）
    /// 与 chat_messages.IsFinal（历史回灌过滤标记）；
    /// Phase 2 的库没有 chat_messages.ToolCallsJson / ToolCallId / Name
    /// （Phase 3 工具循环：助手轮的工具调用与 tool 结果消息）。
    /// 此处用 PRAGMA table_info 检测缺列并以 ALTER TABLE 补齐（幂等）。
    /// 注意：列名必须与 EF 实际映射名一致（本模型未配置 snake_case 约定，
    /// 列名即属性名）——写成 is_final 会让存量库升级后 EF 查询报 no such column。
    /// </summary>
    public static async Task EnsureSchemaAsync(ConversationDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.OpenConnectionAsync(ct);
        var conn = db.Database.GetDbConnection();

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(chat_messages);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // PRAGMA table_info 第 1 列（0 基）是列名。
                existingColumns.Add(reader.GetString(1));
            }
        }

        // (缺列名, 补列 DDL)——类型与 OnModelCreating 映射一致。
        var missing = new (string Column, string Ddl)[]
        {
            ("Label", "ALTER TABLE chat_messages ADD COLUMN \"Label\" TEXT NULL;"),
            ("IsFinal", "ALTER TABLE chat_messages ADD COLUMN \"IsFinal\" INTEGER NULL;"),
            ("ToolCallsJson", "ALTER TABLE chat_messages ADD COLUMN \"ToolCallsJson\" TEXT NULL;"),
            ("ToolCallId", "ALTER TABLE chat_messages ADD COLUMN \"ToolCallId\" TEXT NULL;"),
            ("Name", "ALTER TABLE chat_messages ADD COLUMN \"Name\" TEXT NULL;"),
        };

        foreach (var (column, ddl) in missing)
        {
            if (existingColumns.Contains(column))
            {
                continue;
            }

            await using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.ToTable("chat_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Title).IsRequired().HasMaxLength(500);
            e.Property(s => s.Strategy).HasConversion<int>();
            e.Property(s => s.PreferredProviderId).HasMaxLength(128);
            e.Property(s => s.PreferredModel).HasMaxLength(256);
            e.HasIndex(s => s.UpdatedAtUtc);
            e.HasIndex(s => s.IsDeleted);
            e.HasMany(s => s.Messages)
                .WithOne()
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).HasColumnType("TEXT");
            e.Property(m => m.Role).HasConversion<int>();
            e.Property(m => m.OrchestrationRole).HasConversion<int>();
            e.Property(m => m.Status).HasConversion<int>();
            e.Property(m => m.ProviderId).HasMaxLength(128);
            e.Property(m => m.ModelId).HasMaxLength(256);
            e.Property(m => m.ParentMessageId).HasMaxLength(32);
            e.Property(m => m.Label).HasMaxLength(500);
            e.Property(m => m.IsFinal).HasColumnType("INTEGER");
            e.Property(m => m.ToolCallsJson).HasColumnType("TEXT");
            e.Property(m => m.ToolCallId).HasMaxLength(128);
            e.Property(m => m.Name).HasMaxLength(128);
            e.Property(m => m.Error).HasColumnType("TEXT");
            e.HasIndex(m => m.SessionId);
            e.HasIndex(m => m.CreatedAtUtc);
            e.HasIndex(m => new { m.SessionId, m.CreatedAtUtc });
        });
    }
}
