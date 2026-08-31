// Copyright (c) AeroCode
// WorkspaceContext 边界与排除规则钉子：越界=null 是安全不变量，简化 gitignore 语义按实际行为钉死。
using System;
using System.IO;
using AeroAgent.Moa.Tools.Workspace;
using Xunit;

namespace AeroCode.Tests.MoaTests;

/// <summary>
/// 工作区边界测试：Resolve 只放行 Root 之下的路径；排除集（内建目录/敏感文件/gitignore）
/// 的每条规则都断言到 ShouldExclude 的真实返回值（含已知简化点：*.ext 通配只命中根层）。
/// </summary>
public sealed class WorkspaceBoundaryTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceContext _ws;

    public WorkspaceBoundaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wsbound_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _ws = new WorkspaceContext(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void Resolve_RelativePath_ReturnsAbsoluteUnderRoot()
    {
        var resolved = _ws.Resolve(Path.Combine("sub", "file.txt"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "sub", "file.txt")), resolved);
        Assert.True(_ws.IsWithin(resolved!));
    }

    [Fact]
    public void Resolve_ParentEscape_ReturnsNull()
    {
        Assert.Null(_ws.Resolve("../outside.txt"));
        Assert.Null(_ws.Resolve("a/../../outside.txt"));
        Assert.Null(_ws.Resolve("..\\..\\escape.txt"));
    }

    [Fact]
    public void Resolve_AbsolutePathInsideRoot_Ok()
    {
        var abs = Path.Combine(_dir, "inner.txt");

        Assert.Equal(Path.GetFullPath(abs), _ws.Resolve(abs));
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideRoot_ReturnsNull()
    {
        // 另一个真实存在的目录（%TEMP% 本身），绝不在工作区内
        Assert.Null(_ws.Resolve(Path.GetTempPath()));
        Assert.Null(_ws.Resolve(Environment.SystemDirectory));
    }

    [SkippableFact]
    public void Resolve_UncPath_ReturnsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "UNC 路径语义仅 Windows 有定义");

        Assert.Null(_ws.Resolve(@"\\unc-host\share\file.txt"));
    }

    [Fact]
    public void Resolve_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.Null(_ws.Resolve(null));
        Assert.Null(_ws.Resolve(string.Empty));
        Assert.Null(_ws.Resolve("   "));
    }

    [Fact]
    public void IsWithin_RootItself_True_SiblingPrefixTrapFalse()
    {
        Assert.True(_ws.IsWithin(_dir));

        // 前缀陷阱：兄弟目录 wsbound_xxx2 不得因字符串前缀被误判在界内
        var sibling = _dir + "2";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(_ws.IsWithin(Path.Combine(sibling, "f.txt")));
            Assert.Null(_ws.Resolve(Path.Combine(sibling, "f.txt")));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Constructor_NonexistentRoot_Throws()
    {
        var missing = Path.Combine(_dir, "does_not_exist");
        Assert.Throws<DirectoryNotFoundException>(() => new WorkspaceContext(missing));
    }

    [Fact]
    public void Constructor_EmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceContext(""));
        Assert.Throws<ArgumentException>(() => new WorkspaceContext("   "));
    }

    // ---------- 排除规则 ----------

    [Fact]
    public void ShouldExclude_DefaultExcludedDirs_AtAnyDepth()
    {
        Assert.True(_ws.ShouldExclude(".git/HEAD"));
        Assert.True(_ws.ShouldExclude("node_modules/pkg/index.js"));
        Assert.True(_ws.ShouldExclude("src/obj/debug.o"));
        Assert.True(_ws.ShouldExclude("dist/bundle.js"));
        Assert.False(_ws.ShouldExclude("src/code.cs"));
        // 名字只是包含不是命中：必须整段相等
        Assert.False(_ws.ShouldExclude("buildercs/tool.cs"));
    }

    [Fact]
    public void ShouldExclude_SensitiveFileNames_RegardlessOfDepth()
    {
        Assert.True(_ws.ShouldExclude(".env"));
        Assert.True(_ws.ShouldExclude("config/.env.local"));
        Assert.True(_ws.ShouldExclude("deploy/.env.production"));
        Assert.False(_ws.ShouldExclude("env.example"));
    }

    [Fact]
    public void Gitignore_LiteralSegment_ExcludesAtAnyDepth()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "logs\n# comment\n\n");

        var ws = new WorkspaceContext(_dir);

        Assert.True(ws.ShouldExclude("logs/out.txt"));
        Assert.True(ws.ShouldExclude("a/b/logs/c.txt"));
        Assert.False(ws.ShouldExclude("logbook.txt"));
        Assert.False(ws.ShouldExclude("a/catalogs/x.txt"));
    }

    [Fact]
    public void Gitignore_AnchoredLiteral_OnlyRootLevel()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "/secret.txt\n");

        var ws = new WorkspaceContext(_dir);

        Assert.True(ws.ShouldExclude("secret.txt"));
        // 锚定规则不放行子目录同名文件（gitignore 语义）
        Assert.False(ws.ShouldExclude("sub/secret.txt"));
    }

    [Fact]
    public void Gitignore_WildcardExtension_SimplifiedToRootLevel()
    {
        // 已知简化点（源码注释明示）：*.tmp 按单段通配处理，只命中根层文件。
        // 此用例把该简化语义钉死——若未来实现完整 gitignore 递归语义，需同步更新。
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.tmp\n");

        var ws = new WorkspaceContext(_dir);

        Assert.True(ws.ShouldExclude("notes.tmp"));
        Assert.False(ws.ShouldExclude("sub/notes.tmp"));
    }

    [Fact]
    public void Gitignore_DirPattern_MatchesSegment()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "secret-dir/\n");

        var ws = new WorkspaceContext(_dir);

        Assert.True(ws.ShouldExclude("secret-dir/a.txt"));
        Assert.False(ws.ShouldExclude("other/a.txt"));
    }

    [Fact]
    public void Gitignore_NegativePatterns_IgnoredBySimplifiedParser()
    {
        // 简化解析跳过 ! 负模式：!keep.log 不产生白名单效果，keep.log 仍被 *.log 命中排除。
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n!keep.log\n");

        var ws = new WorkspaceContext(_dir);

        Assert.True(ws.ShouldExclude("run.log"));
        Assert.True(ws.ShouldExclude("keep.log"));
    }

    [Fact]
    public void Display_RelativeFormForLogs()
    {
        var abs = Path.Combine(_dir, "a", "b.txt");
        Directory.CreateDirectory(Path.Combine(_dir, "a"));

        Assert.Equal("a/b.txt", _ws.Display(abs));
    }
}
