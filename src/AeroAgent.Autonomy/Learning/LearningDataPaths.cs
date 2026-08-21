using System;
using System.IO;

namespace AeroAgent.Autonomy.Learning;

/// <summary>
/// 学习子系统数据路径（P6-T3）。与 <see cref="Data.AutonomyDataPaths"/> 同约定但完全独立：
/// 独立 SQLite 库文件（不扩展 AutonomyDbContext，避免触碰既有 missions/lessons 库结构）、
/// 独立经验日志 md、独立 RSI 留痕与参数快照目录。构造时指定根目录
/// （测试指向临时目录，生产指向应用数据根下的 learning 子目录）。
/// </summary>
public sealed class LearningDataPaths
{
    /// <summary>学习数据根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>学习子系统 SQLite 库文件（experiences / correction_rules / skill_flags 三张表）。</summary>
    public string DatabaseFile { get; }

    /// <summary>人类可读经验日志（每条经验写入时追加一个条目块）。</summary>
    public string ExperienceLogFile { get; }

    /// <summary>RSI 全程留痕日志（每轮变异/评估/决策 + 回退 + 创造档审批）。</summary>
    public string RsiLogFile { get; }

    /// <summary>RSI 参数快照目录（应用新参数前旧参数真实落盘 JSON，可回退）。</summary>
    public string ParameterSnapshotDirectory { get; }

    /// <summary>当前生效的 RSI 参数文件（JSON；不存在时按默认参数运行）。</summary>
    public string ActiveParameterFile { get; }

    /// <summary>技能归档目录（低质技能的 SKILL.md 真实移入此处）。</summary>
    public string SkillArchiveDirectory { get; }

    /// <summary>技能备份目录（归档前真实复制的完整副本，回滚的来源）。</summary>
    public string SkillBackupDirectory { get; }

    /// <summary>指定根目录构造（自动派生子路径；目录本身由 <see cref="EnsureDirectories"/> 幂等创建）。</summary>
    public LearningDataPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("rootDirectory 不能为空。", nameof(rootDirectory));
        }

        RootDirectory = rootDirectory;
        DatabaseFile = Path.Combine(RootDirectory, "AeroCode.Learning.db");
        ExperienceLogFile = Path.Combine(RootDirectory, "experience-log.md");
        RsiLogFile = Path.Combine(RootDirectory, "rsi-log.md");
        ParameterSnapshotDirectory = Path.Combine(RootDirectory, "rsi-snapshots");
        ActiveParameterFile = Path.Combine(RootDirectory, "rsi-params-current.json");
        SkillArchiveDirectory = Path.Combine(RootDirectory, "skill-archive");
        SkillBackupDirectory = Path.Combine(RootDirectory, "skill-backup");
    }

    /// <summary>确保全部目录存在（幂等）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ParameterSnapshotDirectory);
        Directory.CreateDirectory(SkillArchiveDirectory);
        Directory.CreateDirectory(SkillBackupDirectory);
    }
}
