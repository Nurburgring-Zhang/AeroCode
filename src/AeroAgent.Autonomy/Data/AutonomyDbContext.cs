using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Autonomy.Data;

/// <summary>
/// 自主内核持久化上下文。missions / lessons 两张表，独立 SQLite 库
/// （与笔记库、对话库分离，避免跨域 EF 上下文互相干扰）。
/// 技术栈沿用 Core/Conversation 既有的 EF Core SQLite（同一提供程序，不引第二套）。
/// </summary>
public class AutonomyDbContext : DbContext
{
    public AutonomyDbContext(DbContextOptions<AutonomyDbContext> options) : base(options)
    {
    }

    public DbSet<MissionRecord> Missions => Set<MissionRecord>();
    public DbSet<LessonRecord> Lessons => Set<LessonRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MissionRecord>(e =>
        {
            e.ToTable("missions");
            e.HasKey(m => m.Id);
            e.Property(m => m.TaskText).IsRequired().HasColumnType("TEXT");
            e.Property(m => m.State).HasConversion<int>();
            e.Property(m => m.Outcome).HasConversion<int>();
            e.Property(m => m.AnalysisJson).HasColumnType("TEXT");
            e.Property(m => m.Strategy).HasMaxLength(64);
            e.Property(m => m.StrategyRationale).HasColumnType("TEXT");
            e.Property(m => m.ClarificationJson).HasColumnType("TEXT");
            e.Property(m => m.SteelmanJson).HasColumnType("TEXT");
            e.Property(m => m.PlanJson).HasColumnType("TEXT");
            e.Property(m => m.SessionId).HasMaxLength(64);
            e.Property(m => m.ExecutionJson).HasColumnType("TEXT");
            e.Property(m => m.VerificationJson).HasColumnType("TEXT");
            e.Property(m => m.RetrospectiveJson).HasColumnType("TEXT");
            e.Property(m => m.TransitionsJson).HasColumnType("TEXT");
            e.Property(m => m.Error).HasColumnType("TEXT");
            e.HasIndex(m => m.State);
            e.HasIndex(m => m.CreatedAtUtc);
        });

        modelBuilder.Entity<LessonRecord>(e =>
        {
            e.ToTable("lessons");
            e.HasKey(l => l.Id);
            e.Property(l => l.MissionId).IsRequired().HasMaxLength(64);
            e.Property(l => l.Phase).IsRequired().HasMaxLength(64);
            e.Property(l => l.Gap).IsRequired().HasColumnType("TEXT");
            e.Property(l => l.Suggestion).HasColumnType("TEXT");
            e.Property(l => l.Severity).HasMaxLength(16);
            e.HasIndex(l => l.MissionId);
            e.HasIndex(l => l.CreatedAtUtc);
        });
    }
}
