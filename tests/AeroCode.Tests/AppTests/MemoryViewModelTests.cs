// Copyright (c) AeroCode V3.0
// MemoryViewModel tests — Hermes-style MEMORY.md/USER.md 截断治理（2200/1375）的真实落盘断言。
// AppDataPaths 支持构造函数注入自定义根目录（AppDataPaths(string rootDirectory)），
// 全部测试指向临时目录，不触碰用户真实数据。
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AeroCode.App.Services;
using AeroCode.App.ViewModels;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// MemoryViewModel 的 Save 截断治理：MemoryContent>2200 截断到 2200、UserContent>1375 截断到 1375，
/// 写盘并更新计数；未超限原样保存；文件缺失时按默认模板加载。
/// 注意（代码事实）：截断警告是保存过程中的瞬态 StatusText，保存成功后最终统一被
/// "✓ 已保存 (HH:mm:ss)" 覆盖——因此用 PropertyChanged 历史断言警告确实出现过。
/// </summary>
public sealed class MemoryViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryViewModel _vm;
    private readonly List<string> _statusHistory = new();

    private string MemoryFile => Path.Combine(_root, "memories", "MEMORY.md");
    private string UserFile => Path.Combine(_root, "memories", "USER.md");

    public MemoryViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"memory_vm_{Guid.NewGuid():N}");
        _vm = new MemoryViewModel(new AppDataPaths(_root));
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MemoryViewModel.StatusText))
            {
                _statusHistory.Add(_vm.StatusText);
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>MemoryContent 3000 字符 → 磁盘 MEMORY.md 恰好 2200、计数同步、截断提示真实出现过、最终报已保存。</summary>
    [Fact]
    public async Task Save_OverLimit_TruncatesMemoryFile()
    {
        _vm.MemoryContent = new string('x', 3000);

        await _vm.SaveCommand.ExecuteAsync(null);

        var onDisk = File.ReadAllText(MemoryFile);
        Assert.Equal(2200, onDisk.Length);                  // 磁盘恰好 2200
        Assert.Equal(new string('x', 2200), onDisk);        // 截断保留前缀，不是填充也不是截到其他长度
        Assert.Equal(2200, _vm.MemoryContent.Length);
        Assert.Equal(2200, _vm.MemoryCharCount);
        // 截断提示在保存过程中瞬态出现（最终被成功保存状态覆盖）
        Assert.Contains(_statusHistory, s => s.Contains("截断保存"));
        Assert.Contains("已保存", _vm.StatusText);
    }

    /// <summary>UserContent 2000 字符 → 磁盘 USER.md 恰好 1375；未超限的 MEMORY.md 原样写盘不受影响。</summary>
    [Fact]
    public async Task Save_OverLimit_TruncatesUserFile()
    {
        _vm.UserContent = new string('u', 2000);

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1375, File.ReadAllText(UserFile).Length);
        Assert.Equal(1375, _vm.UserContent.Length);
        Assert.Equal(1375, _vm.UserCharCount);
        // MEMORY.md（默认模板，未超限）原样写盘
        Assert.Contains("MEMORY.md", File.ReadAllText(MemoryFile));
        Assert.Contains("已保存", _vm.StatusText);
    }

    /// <summary>未超限保存：内容一字不变、计数一致、无截断提示、StatusText 报已保存。</summary>
    [Fact]
    public async Task Save_WithinLimit_NoTruncation()
    {
        const string memory = "## 项目笔记\n- 本地优先";
        const string user = "- 用户偏好：直接、要数据";
        _vm.MemoryContent = memory;
        _vm.UserContent = user;

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(memory, File.ReadAllText(MemoryFile));
        Assert.Equal(user, File.ReadAllText(UserFile));
        Assert.Equal(memory.Length, _vm.MemoryCharCount);
        Assert.Equal(user.Length, _vm.UserCharCount);
        Assert.DoesNotContain(_statusHistory, s => s.Contains("截断"));
        Assert.Contains("已保存", _vm.StatusText);
    }

    /// <summary>全新目录无文件：按默认模板加载，计数与内容一致，且默认模板不落盘（只有 Save 才写盘）。</summary>
    [Fact]
    public void Load_MissingFiles_UsesDefaults()
    {
        Assert.Contains("MEMORY.md", _vm.MemoryContent);    // 默认模板特征
        Assert.Contains("2200", _vm.MemoryContent);         // 默认模板自述上限
        Assert.Contains("USER.md", _vm.UserContent);
        Assert.Equal(_vm.MemoryContent.Length, _vm.MemoryCharCount);
        Assert.Equal(_vm.UserContent.Length, _vm.UserCharCount);
        Assert.Equal("已加载", _vm.StatusText);
        // 构造期只做内存默认加载，磁盘不应出现文件
        Assert.False(File.Exists(MemoryFile));
        Assert.False(File.Exists(UserFile));
    }
}
