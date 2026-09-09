// Copyright (c) AeroCode V3.0
// MemoryViewModel tests — MEMORY.md/USER.md 无字符上限、原样真实落盘断言。
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
/// MemoryViewModel 的 Save 无上限语义：MemoryContent/UserContent 无论多长都原样写盘、
/// 计数同步、无截断提示；文件缺失时按默认模板加载（模板自述"无字符上限"）。
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

    /// <summary>MemoryContent 3000 字符（超旧上限）→ 磁盘 MEMORY.md 一字不少 3000、计数同步、无截断提示。</summary>
    [Fact]
    public async Task Save_LargeMemory_NoTruncation()
    {
        _vm.MemoryContent = new string('x', 3000);

        await _vm.SaveCommand.ExecuteAsync(null);

        var onDisk = File.ReadAllText(MemoryFile);
        Assert.Equal(3000, onDisk.Length);                    // 磁盘恰好 3000，未截断
        Assert.Equal(new string('x', 3000), onDisk);
        Assert.Equal(3000, _vm.MemoryContent.Length);
        Assert.Equal(3000, _vm.MemoryCharCount);
        Assert.DoesNotContain(_statusHistory, s => s.Contains("截断"));
        Assert.Contains("已保存", _vm.StatusText);
    }

    /// <summary>UserContent 2000 字符（超旧上限）→ 磁盘 USER.md 一字不少 2000；MEMORY.md 原样写盘。</summary>
    [Fact]
    public async Task Save_LargeUser_NoTruncation()
    {
        _vm.UserContent = new string('u', 2000);

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2000, File.ReadAllText(UserFile).Length);
        Assert.Equal(2000, _vm.UserContent.Length);
        Assert.Equal(2000, _vm.UserCharCount);
        Assert.Contains("MEMORY.md", File.ReadAllText(MemoryFile));
        Assert.DoesNotContain(_statusHistory, s => s.Contains("截断"));
        Assert.Contains("已保存", _vm.StatusText);
    }

    /// <summary>常规保存：内容一字不变、计数一致、无截断提示、StatusText 报已保存。</summary>
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
        Assert.Contains("无字符上限", _vm.MemoryContent);    // 默认模板自述无上限
        Assert.Contains("USER.md", _vm.UserContent);
        Assert.Equal(_vm.MemoryContent.Length, _vm.MemoryCharCount);
        Assert.Equal(_vm.UserContent.Length, _vm.UserCharCount);
        Assert.Equal("已加载", _vm.StatusText);
        // 构造期只做内存默认加载，磁盘不应出现文件
        Assert.False(File.Exists(MemoryFile));
        Assert.False(File.Exists(UserFile));
    }
}
