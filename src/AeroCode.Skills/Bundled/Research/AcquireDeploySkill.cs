// Copyright (c) AeroCode V3.3
// AcquireDeploySkill — real "search → download → deploy → index" acquisition pipeline.
// Given a repo/zip URL it genuinely fetches the source (git clone or HTTPS zip),
// unpacks it into a sandboxed directory under enforced safety limits, builds a content
// index and returns it as reference context. Every step is traced to an on-disk log.
//
// Args:
//   url=<git-repo | zip-url>     required
//   target_dir=<path>            optional sandbox target (default <workspace>/.aero-acquired/<slug>)
//   max_mb=<int>                 default 200 (total extracted size cap)
//   max_depth=<int>              default 12 (directory depth cap)
//   method=auto|git|zip          default auto
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AeroCode.Skills.Registry;

namespace AeroCode.Skills.Bundled.Research;

/// <summary>Outcome of a real acquisition run.</summary>
/// <param name="LocalPath">Sandbox directory containing the acquired content.</param>
/// <param name="Method">How the content was fetched: "git-clone" or "zip-download".</param>
/// <param name="FileCount">Number of files present after extraction.</param>
/// <param name="TotalBytes">Total extracted payload size in bytes.</param>
/// <param name="IndexedFiles">Files admitted into the reference index.</param>
/// <param name="BlockedFiles">Files kept on disk but excluded from the index (dangerous extensions).</param>
/// <param name="KeyFiles">Detected entry points (README/docs/project manifests).</param>
/// <param name="LogPath">On-disk operation trace log.</param>
public sealed record AcquireResult(
    string LocalPath,
    string Method,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> IndexedFiles,
    IReadOnlyList<string> BlockedFiles,
    IReadOnlyList<string> KeyFiles,
    string LogPath);

/// <summary>
/// Skill: acquire a repository/zip from the network, deploy it into a sandbox and
/// produce an indexed reference context. No simulation — real git/HTTP/extraction.
/// </summary>
public sealed class AcquireDeploySkill : ISkill
{
    /// <summary>Default total extracted-size cap (MB).</summary>
    public const long DefaultMaxMb = 200;

    /// <summary>Default directory-depth cap.</summary>
    public const int DefaultMaxDepth = 12;

    /// <summary>
    /// Extensions excluded from index injection (binaries / script launchers). Files are
    /// kept on disk and reported, but never fed into prompt context automatically.
    /// </summary>
    public static readonly IReadOnlySet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".com", ".pif", ".scr", ".msi", ".msp",
        ".bat", ".cmd", ".vbs", ".vbe", ".jse", ".wsf", ".wsh",
        ".hta", ".cpl", ".lnk", ".ps1", ".psm1",
    };

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "AeroCode-AcquireDeploy/1.0" } },
    };

    public string Id => "research/acquire-deploy";
    public string Name => "Acquire & Deploy";
    public string Description => "真实下载 git 仓库/zip → 沙箱解压 → 安全限额 → 内容索引，产出可注入的参考上下文";
    public string Category => "research";
    public string Author => "AeroCode Team (human first, Hermes rule)";
    public string Version => "1.0.0";
    public IReadOnlyList<string> Tags => new[] { "acquire", "deploy", "git", "zip", "index", "sandbox" };
    public bool IsAvailable() => true;

    public string GetSystemPrompt() =>
        "# Acquire & Deploy Skill\n" +
        "Args:\n" +
        "  url=<git-repo|zip-url>        # required\n" +
        "  target_dir=<path>             # optional sandbox target\n" +
        "  max_mb=200 max_depth=12 method=auto|git|zip\n" +
        "Behavior: real git clone (depth 1) or HTTPS zip download → sandboxed extraction\n" +
        "(zip-slip protected, size/depth capped, dangerous extensions excluded from index)\n" +
        "→ content index (file tree + key files). Full trace in aero-acquire.log.";

    public async Task<SkillResult> ExecuteAsync(SkillInput input, SkillContext ctx, CancellationToken ct = default)
    {
        var args = input.Args ?? new Dictionary<string, object?>();
        var url = args.TryGetValue("url", out var u) ? u as string : null;
        if (string.IsNullOrWhiteSpace(url)) return new SkillResult { Success = false, Text = "需要 'url' 参数" };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new SkillResult { Success = false, Text = $"Invalid URL: {url}" };

        var maxMb = args.TryGetValue("max_mb", out var mm) && mm is not null ? Convert.ToInt64(mm) : DefaultMaxMb;
        var maxDepth = args.TryGetValue("max_depth", out var md) && md is not null ? Convert.ToInt32(md) : DefaultMaxDepth;
        var method = ((args.TryGetValue("method", out var me) ? me as string : null) ?? "auto").ToLowerInvariant();

        var workspace = string.IsNullOrWhiteSpace(ctx.WorkspaceRoot) ? Path.GetTempPath() : ctx.WorkspaceRoot;
        var targetDir = (args.TryGetValue("target_dir", out var td) ? td as string : null)
            ?? Path.Combine(workspace, ".aero-acquired", MakeSlug(uri));

        var logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetDir)) ?? workspace, "aero-acquire.log");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetDir))!);

        var log = new StringBuilder();
        void Trace(string msg)
        {
            var line = $"{DateTime.UtcNow:O} {msg}";
            log.AppendLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* log dir may be read-only; in-memory trace still returned */ }
        }

        Trace($"ACQUIRE start url={url} target={targetDir} max_mb={maxMb} max_depth={maxDepth} method={method}");
        try
        {
            var usedMethod = await FetchAsync(uri, targetDir, method, maxMb, Trace, ct);
            Trace($"FETCH ok method={usedMethod}");

            var report = ScanExtracted(targetDir, maxMb, maxDepth, Trace);
            Trace($"ACQUIRE done files={report.FileCount} bytes={report.TotalBytes} indexed={report.IndexedFiles.Count} blocked={report.BlockedFiles.Count}");

            var result = new AcquireResult(
                targetDir, usedMethod, report.FileCount, report.TotalBytes,
                report.IndexedFiles, report.BlockedFiles, report.KeyFiles, logPath);

            var sb = new StringBuilder();
            sb.AppendLine($"# Acquire & Deploy: {url}");
            sb.AppendLine($"- Method: {usedMethod}");
            sb.AppendLine($"- Local path: {targetDir}");
            sb.AppendLine($"- Files: {report.FileCount} ({FormatBytes(report.TotalBytes)}); indexed: {report.IndexedFiles.Count}; blocked(dangerous ext, kept on disk): {report.BlockedFiles.Count}");
            sb.AppendLine($"- Trace log: {logPath}");
            if (report.KeyFiles.Count > 0)
            {
                sb.AppendLine("- Key files:");
                foreach (var k in report.KeyFiles.Take(20)) sb.AppendLine($"  - {k}");
            }
            sb.AppendLine("- File tree (first 60):");
            foreach (var f in report.IndexedFiles.Take(60)) sb.AppendLine($"  - {f}");
            if (report.BlockedFiles.Count > 0)
            {
                sb.AppendLine("- Blocked from index (dangerous extensions):");
                foreach (var b in report.BlockedFiles.Take(20)) sb.AppendLine($"  - {b}");
            }
            return new SkillResult { Success = true, Text = sb.ToString(), Data = result };
        }
        catch (Exception ex)
        {
            Trace($"ACQUIRE failed: {ex.GetType().Name}: {ex.Message}");
            return new SkillResult { Success = false, Text = $"AcquireDeploy failed: {ex.GetType().Name}: {ex.Message}\nTrace: {log}" };
        }
    }

    // ============== fetch ===============

    private static async Task<string> FetchAsync(Uri uri, string targetDir, string method, long maxMb, Action<string> trace, CancellationToken ct)
    {
        // Local archive (file:// scheme): real copy path — HttpClient does not support file://.
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            var localPath = uri.LocalPath;
            if (!File.Exists(localPath)) throw new FileNotFoundException($"本地归档不存在: {localPath}");
            var size = new FileInfo(localPath).Length;
            if (size > maxMb * 1024 * 1024) throw new InvalidOperationException($"本地归档超过 {maxMb}MB 上限");
            var zipDest = targetDir + ".zip";
            trace($"COPY local {localPath} ({size} bytes)");
            File.Copy(localPath, zipDest, overwrite: true);
            ExtractZipSafe(zipDest, targetDir, maxMb, trace);
            return "zip-download";
        }

        var isZipUrl = uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var gitAvailable = IsGitAvailable();

        if (method == "zip" || (method == "auto" && isZipUrl))
        {
            var zipPath = targetDir + ".zip";
            await DownloadFileAsync(uri, zipPath, maxMb, trace, ct);
            ExtractZipSafe(zipPath, targetDir, maxMb, trace);
            return "zip-download";
        }

        if (method == "git" || (method == "auto" && gitAvailable))
        {
            if (!gitAvailable) throw new InvalidOperationException("method=git 但 git CLI 不可用");
            if (await TryGitCloneAsync(uri, targetDir, trace, ct)) return "git-clone";
            if (method == "git") throw new InvalidOperationException("git clone 失败且 method=git（不允许静默降级）");
            trace("git clone 失败，[DEGRADED] 回退到 zip 下载路径");
        }

        // zip path (auto fallback or github conversion)
        var zipUrl = ToZipUrl(uri);
        var zipLocal = targetDir + ".zip";
        await DownloadFileAsync(zipUrl, zipLocal, maxMb, trace, ct);
        ExtractZipSafe(zipLocal, targetDir, maxMb, trace);
        return "zip-download";
    }

    /// <summary>Convert a GitHub repo URL to its codeload zipball; other URLs pass through.</summary>
    public static Uri ToZipUrl(Uri uri)
    {
        if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return uri;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return uri;
        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        var refName = "HEAD";
        var treeIdx = Array.IndexOf(parts, "tree");
        if (treeIdx >= 0 && treeIdx + 1 < parts.Length) refName = parts[treeIdx + 1];
        return new Uri($"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{refName}");
    }

    private static async Task DownloadFileAsync(Uri url, string destPath, long maxMb, Action<string> trace, CancellationToken ct)
    {
        trace($"DOWNLOAD {url}");
        using var resp = await SharedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var limit = maxMb * 1024 * 1024;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidOperationException($"下载超过 {maxMb}MB 上限，中止");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        trace($"DOWNLOAD ok {total} bytes");
    }

    /// <summary>Extract a zip with zip-slip protection + total-size cap. Public for tests.</summary>
    public static void ExtractZipSafe(string zipPath, string targetDir, long maxMb, Action<string>? trace = null)
    {
        trace?.Invoke($"EXTRACT {zipPath} → {targetDir}");
        Directory.CreateDirectory(targetDir);
        var fullTarget = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        var limit = maxMb * 1024 * 1024;
        long total = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!dest.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"zip-slip 攻击防护: 非法条目 {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            total += entry.Length;
            if (total > limit) throw new InvalidOperationException($"解压超过 {maxMb}MB 上限，中止");
            entry.ExtractToFile(dest, overwrite: true);
        }
        trace?.Invoke($"EXTRACT ok {total} bytes");
    }

    private static async Task<bool> TryGitCloneAsync(Uri uri, string targetDir, Action<string> trace, CancellationToken ct)
    {
        var cloneUrl = uri.ToString();
        trace($"GIT clone --depth 1 {cloneUrl}");
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            ArgumentList = { "clone", "--depth", "1", cloneUrl, targetDir },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            trace($"GIT exit={proc.ExitCode}");
            if (proc.ExitCode != 0)
            {
                trace($"GIT stderr: {Truncate(stderr, 500)}");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(stdout)) trace($"GIT stdout: {Truncate(stdout, 300)}");
            return Directory.Exists(targetDir);
        }
        catch (Exception ex)
        {
            trace($"GIT 启动失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>Probe for a usable git CLI on PATH. Public for tests/diagnostics.</summary>
    public static bool IsGitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ============== scan & index ===============

    /// <summary>Scan an extracted tree: enforce depth cap, split indexed vs blocked, find key files.</summary>
    public static ScanReport ScanExtracted(string root, long maxMb, int maxDepth, Action<string>? trace = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var indexed = new List<string>();
        var blocked = new List<string>();
        var keyFiles = new List<string>();
        long total = 0;
        var fileCount = 0;

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            fileCount++;
            var rel = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var depth = rel.Count(c => c == '/') + 1;
            long size;
            try { size = new FileInfo(file).Length; } catch { size = 0; }
            total += size;
            if (total > maxMb * 1024 * 1024)
                throw new InvalidOperationException($"内容超过 {maxMb}MB 上限");

            if (depth > maxDepth)
            {
                blocked.Add(rel + " (depth>max)");
                continue;
            }
            var ext = Path.GetExtension(file);
            if (DangerousExtensions.Contains(ext))
            {
                blocked.Add(rel);
                continue;
            }
            indexed.Add(rel);

            var name = Path.GetFileName(file);
            if (IsKeyFile(name, rel)) keyFiles.Add(rel);
        }

        trace?.Invoke($"SCAN files={fileCount} bytes={total} indexed={indexed.Count} blocked={blocked.Count} key={keyFiles.Count}");
        return new ScanReport(fileCount, total, indexed, blocked, keyFiles);
    }

    private static bool IsKeyFile(string fileName, string relPath)
    {
        if (fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (relPath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return true;
        return fileName switch
        {
            "package.json" or "pyproject.toml" or "setup.py" or "Cargo.toml" or "go.mod"
                or "pom.xml" or "build.gradle" or "CMakeLists.txt" or "Makefile" => true,
            _ => fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string MakeSlug(Uri uri)
    {
        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "repo";
        if (last.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        var sb = new StringBuilder();
        foreach (var ch in last)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
        var slug = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(slug) ? "repo" : slug;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }
}

/// <summary>Result of scanning an extracted tree.</summary>
/// <param name="FileCount">Total files found.</param>
/// <param name="TotalBytes">Total size in bytes.</param>
/// <param name="IndexedFiles">Relative paths admitted to the index.</param>
/// <param name="BlockedFiles">Relative paths excluded from the index (with reason).</param>
/// <param name="KeyFiles">Detected entry-point files.</param>
public sealed record ScanReport(
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> IndexedFiles,
    IReadOnlyList<string> BlockedFiles,
    IReadOnlyList<string> KeyFiles);
