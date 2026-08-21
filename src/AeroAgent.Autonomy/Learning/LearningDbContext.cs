using Microsoft.EntityFrameworkCore;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// 学习子系统持久化上下文（P6-T3）。独立 SQLite 库文件（<see cref="LearningDataPaths.DatabaseFile"/>），
/// 技术栈与 <see cref="Data.AutonomyDbContext"/> 完全相同（EF Core Sqlite 同一提供程序），
/// 但不扩展既有上下文——missions/lessons 库结构零改动。
/// 三张表：experiences（三分经验）、correction_rules（RSI L1 修正规则）、skill_flags（技能治理标记）。
/// </summary>
public class LearningDbContext : DbContext
{
    public LearningDbContext(DbContextOptions<LearningDbContext> options) : base(options)
    {
    }

    /// <summary>按学习数据路径创建上下文（独立库文件，幂等）。</summary>
    public static LearningDbContext Create(LearningDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        return new LearningDbContext(
            new DbContextOptionsBuilder<LearningDbContext>()
                .UseSqlite($"Data Source={paths.DatabaseFile}")
                .Options);
    }

    public DbSet<ExperienceEntity> Experiences => Set<ExperienceEntity>();
    public DbSet<CorrectionRuleEntity> CorrectionRules => Set<CorrectionRuleEntity>();
    public DbSet<SkillFlagEntity> SkillFlags => Set<SkillFlagEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ExperienceEntity>(e =>
        {
            e.ToTable("experiences");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Title).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.Content).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.SourceKey).IsRequired().HasMaxLength(191);
            e.Property(x => x.SourceMissionId).HasMaxLength(64);
            e.Property(x => x.SourcePhase).HasMaxLength(64);
            e.Property(x => x.TagsJson).HasColumnType("TEXT");
            e.HasIndex(x => x.SourceKey).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Kind);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<CorrectionRuleEntity>(e =>
        {
            e.ToTable("correction_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.MissionId).IsRequired().HasMaxLength(64);
            e.Property(x => x.GapDescription).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.RuleText).IsRequired().HasColumnType("TEXT");
            e.Property(x => x.Severity).HasMaxLength(16);
            e.HasIndex(x => x.MissionId);
            e.HasIndex(x => x.Promoted);
        });

        modelBuilder.Entity<SkillFlagEntity>(e =>
        {
            e.ToTable("skill_flags");
            e.HasKey(x => x.SkillId);
            e.Property(x => x.SkillId).HasMaxLength(191);
            e.Property(x => x.Flag).IsRequired().HasMaxLength(32);
            e.Property(x => x.Reason).HasColumnType("TEXT");
        });
    }
}
