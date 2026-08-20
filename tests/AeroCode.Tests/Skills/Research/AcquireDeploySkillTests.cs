// AcquireDeploySkill tests — local zip safety envelope (no network needed) plus
// URL conversion rules. Network-gated live tests (incl. the real-repo E2E below)
// follow the repo convention: AEROCODE_RUN_NETWORK_TESTS=1 enables them.
using System.IO.Compression;
using AeroCode.Skills.Bundled.Research;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.Skills.Research;

public sealed class AcquireDeploySkillTests : IDisposable
{
    private readonly string _tempRoot;

    public AcquireDeploySkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "aerocode-acquire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    private string MakeZip(string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(_tempRoot, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    private static async Task<SkillResult> RunSkill(string zipPath, string targetDir, Dictionary<string, object?>? extra = null)
    {
        var skill = new AcquireDeploySkill();
        var args = new Dictionary<string, object?>
        {
            ["url"] = new Uri(zipPath).AbsoluteUri,
            ["target_dir"] = targetDir,
            ["method"] = "zip",
        };
        if (extra is not null)
        {
            foreach (var kv in extra) args[kv.Key] = kv.Value;
        }

        return await skill.ExecuteAsync(
            new SkillInput { Args = args },
            new SkillContext { WorkspaceRoot = Path.GetDirectoryName(zipPath)! });
    }

    [Fact]
    public async Task ZipAcquire_ExtractsAndIndexes_RealFiles()
    {
        var zip = MakeZip("good.zip", z =>
        {
            AddEntry(z, "README.md", "# Sample project");
            AddEntry(z, "src/main.py", "print('hello')");
            AddEntry(z, "docs/guide.md", "guide");
        });
        var target = Path.Combine(_tempRoot, "out-good");

        var result = await RunSkill(zip, target);

        Assert.True(result.Success, result.Text);
        var data = Assert.IsType<AcquireResult>(result.Data);
        Assert.Equal("zip-download", data.Method);
        Assert.Equal(3, data.FileCount);
        Assert.Contains("README.md", data.KeyFiles);
        Assert.Contains("docs/guide.md", data.KeyFiles);
        Assert.True(File.Exists(Path.Combine(target, "src", "main.py")));
        Assert.True(File.Exists(data.LogPath)); // real trace log on disk
    }

    [Fact]
    public async Task DangerousExtensions_AreKeptOnDisk_ButExcludedFromIndex()
    {
        var zip = MakeZip("mixed.zip", z =>
        {
            AddEntry(z, "README.md", "# readme");
            AddEntry(z, "tools/launcher.exe", "MZ-fake-binary");
            AddEntry(z, "scripts/setup.ps1", "Write-Host hi");
            AddEntry(z, "src/app.cs", "class App {}");
        });
        var target = Path.Combine(_tempRoot, "out-mixed");

        var result = await RunSkill(zip, target);

        Assert.True(result.Success, result.Text);
        var data = Assert.IsType<AcquireResult>(result.Data);
        Assert.Contains("README.md", data.IndexedFiles);
        Assert.Contains("src/app.cs", data.IndexedFiles);
        Assert.DoesNotContain(data.IndexedFiles, f => f.EndsWith(".exe") || f.EndsWith(".ps1"));
        Assert.Equal(2, data.BlockedFiles.Count);
        // Files are kept on disk (reported, not silently destroyed).
        Assert.True(File.Exists(Path.Combine(target, "tools", "launcher.exe")));
    }

    [Fact]
    public async Task SizeCap_ExtractionAborts_Honestly()
    {
        var zip = MakeZip("big.zip", z =>
        {
            // One entry whose declared size exceeds a tiny cap.
            AddEntry(z, "big.bin", new string('x', 3 * 1024 * 1024));
        });
        var target = Path.Combine(_tempRoot, "out-big");

        // max_mb=1 → the 3MB entry must abort the acquisition.
        var result = await RunSkill(zip, target, new Dictionary<string, object?> { ["max_mb"] = 1 });

        Assert.False(result.Success);
        Assert.Contains("上限", result.Text);
    }

    [Fact]
    public async Task DepthCap_DeepFilesExcludedFromIndex()
    {
        var zip = MakeZip("deep.zip", z =>
        {
            AddEntry(z, "a/b/c/d/e/file.txt", "deep"); // depth 5
            AddEntry(z, "top.txt", "top");             // depth 1
        });
        var target = Path.Combine(_tempRoot, "out-deep");

        var result = await RunSkill(zip, target, new Dictionary<string, object?> { ["max_depth"] = 2 });

        Assert.True(result.Success, result.Text);
        var data = Assert.IsType<AcquireResult>(result.Data);
        Assert.Contains("top.txt", data.IndexedFiles);
        Assert.DoesNotContain("a/b/c/d/e/file.txt", data.IndexedFiles);
        Assert.Contains(data.BlockedFiles, b => b.Contains("depth"));
    }

    [Fact]
    public void ExtractZipSafe_ZipSlipEntry_Throws()
    {
        var zip = MakeZip("evil.zip", z => AddEntry(z, "../escape.txt", "pwned"));
        var target = Path.Combine(_tempRoot, "out-evil");

        var ex = Assert.Throws<InvalidOperationException>(
            () => AcquireDeploySkill.ExtractZipSafe(zip, target, 10));
        Assert.Contains("zip-slip", ex.Message);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://codeload.github.com/owner/repo/zip/refs/heads/HEAD")]
    [InlineData("https://github.com/owner/repo.git", "https://codeload.github.com/owner/repo/zip/refs/heads/HEAD")]
    [InlineData("https://github.com/owner/repo/tree/dev", "https://codeload.github.com/owner/repo/zip/refs/heads/dev")]
    public void ToZipUrl_GitHubRepo_ConvertsToCodeloadZipball(string input, string expected)
    {
        Assert.Equal(new Uri(expected), AcquireDeploySkill.ToZipUrl(new Uri(input)));
    }

    [Fact]
    public void ToZipUrl_NonGitHub_PassesThrough()
    {
        var url = new Uri("https://example.com/pkg.zip");
        Assert.Equal(url, AcquireDeploySkill.ToZipUrl(url));
    }

    [Fact]
    public void IsGitAvailable_ReturnsDeterministicBool()
    {
        // Whatever the machine state, the probe must answer without throwing.
        var available = AcquireDeploySkill.IsGitAvailable();
        Assert.Equal(available, AcquireDeploySkill.IsGitAvailable());
    }

    [Fact]
    public async Task MissingUrlArg_FailsWithClearMessage()
    {
        var skill = new AcquireDeploySkill();
        var result = await skill.ExecuteAsync(
            new SkillInput { Args = new Dictionary<string, object?>() },
            new SkillContext { WorkspaceRoot = _tempRoot });
        Assert.False(result.Success);
        Assert.Contains("url", result.Text);
    }

    [Fact]
    public void SkillMetadata_IsHonestAboutCapabilities()
    {
        var skill = new AcquireDeploySkill();
        Assert.Equal("research/acquire-deploy", skill.Id);
        Assert.True(skill.IsAvailable());
        Assert.Contains("git", skill.GetSystemPrompt());
    }

    [SkippableFact]
    public async Task Live_RealSmallRepo_ZipballAcquisition_EndToEnd()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AEROCODE_RUN_NETWORK_TESTS") == "1",
            "AEROCODE_RUN_NETWORK_TESTS != 1，真实仓库 E2E 如实跳过");

        // A tiny, stable public repo; fetched via GitHub's codeload zipball (real HTTPS).
        var skill = new AcquireDeploySkill();
        var target = Path.Combine(_tempRoot, "live-repo");
        var result = await skill.ExecuteAsync(
            new SkillInput
            {
                Args = new Dictionary<string, object?>
                {
                    ["url"] = "https://codeload.github.com/octocat/Hello-World/zip/refs/heads/master",
                    ["target_dir"] = target,
                    ["method"] = "zip",
                    ["max_mb"] = 20,
                },
            },
            new SkillContext { WorkspaceRoot = _tempRoot });

        Assert.True(result.Success, result.Text);
        var data = Assert.IsType<AcquireResult>(result.Data);
        Assert.True(data.FileCount > 0);
        Assert.True(File.Exists(Path.Combine(target, "README")), "expected the real repo's README file");
        Assert.True(File.Exists(data.LogPath));
    }
}
