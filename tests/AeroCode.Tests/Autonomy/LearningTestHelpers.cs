// Shared hand-written test environment for the Learning subsystem (P6-T3).
// No mocking library: real SQLite files, real file IO, real PHASE 5 components.
using AeroAgent.Autonomy.Data;
using AeroAgent.Autonomy.Experience;
using AeroAgent.Autonomy.Learning;
using Microsoft.EntityFrameworkCore;

namespace AeroCode.Tests.Autonomy;

/// <summary>
/// Learning test environment: real temp directories + real SQLite databases
/// (autonomy db for missions/lessons, learning db for experiences/rules/flags)
/// + real PHASE 5 components (MissionStore / ExperienceInjector).
/// Each component gets its own DbContext instance (SQLite multi-connection safe).
/// </summary>
internal sealed class LearningEnv : IDisposable
{
    public string Root { get; }
    public AutonomyDataPaths AutonomyPaths { get; }
    public AutonomyDbContext AutonomyDb { get; }
    public MissionStore Missions { get; }

    public LearningDataPaths LearningPaths { get; }
    public LearningDbContext StoreDb { get; }
    public ExperienceStore Experiences { get; }
    public ExperienceInjector Injector { get; }
    public ExperienceBridge Bridge { get; }
    public SystemPromptBuilder PromptBuilder { get; }

    public LearningEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "aerocode-learning-" + Guid.NewGuid().ToString("N"));
        AutonomyPaths = new AutonomyDataPaths(Path.Combine(Root, "autonomy"));
        AutonomyPaths.EnsureDirectories();
        AutonomyDb = new AutonomyDbContext(
            new DbContextOptionsBuilder<AutonomyDbContext>()
                .UseSqlite($"Data Source={AutonomyPaths.DatabaseFile}")
                .Options);
        Missions = new MissionStore(AutonomyDb);
        Missions.EnsureCreatedAsync().GetAwaiter().GetResult();

        LearningPaths = new LearningDataPaths(Path.Combine(Root, "learning"));
        LearningPaths.EnsureDirectories();
        StoreDb = LearningDbContext.Create(LearningPaths);
        Experiences = new ExperienceStore(StoreDb, LearningPaths);
        Experiences.EnsureCreatedAsync().GetAwaiter().GetResult();

        Injector = new ExperienceInjector(Missions);
        Bridge = new ExperienceBridge(Missions, Experiences);
        PromptBuilder = new SystemPromptBuilder(Injector, Experiences);
    }

    /// <summary>Opens an additional independent context over the same learning db file.</summary>
    public LearningDbContext NewLearningDb() => LearningDbContext.Create(LearningPaths);

    public void Dispose()
    {
        Experiences.Dispose();
        StoreDb.Dispose();
        Missions.Dispose();
        AutonomyDb.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test on cleanup.
        }
    }
}
