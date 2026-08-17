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
            e.Property(m => m.Error).HasColumnType("TEXT");
            e.HasIndex(m => m.SessionId);
            e.HasIndex(m => m.CreatedAtUtc);
            e.HasIndex(m => new { m.SessionId, m.CreatedAtUtc });
        });
    }
}
