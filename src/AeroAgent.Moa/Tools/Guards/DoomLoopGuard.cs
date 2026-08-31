// Copyright (c) AeroCode
// DoomLoopGuard — 同工具同参数反复调用守卫（批次 B G3，builder-β）。
// 对标 goose doom_loop / 循环检测：args 规范化（键序排序 + 空白折叠）后取真实 SHA256，
// 环形窗口内同键出现次数达到阈值即升级 Ask 强制人工。阈值与窗口构造注入；
// 内部仅一个锁保护的环形队列，MOA 并行 worker 并发 Check 安全。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

public sealed class DoomLoopGuard : IToolGuard
{
    private readonly int _threshold;
    private readonly int _windowCapacity;
    private readonly object _sync = new();
    private readonly Queue<(string Tool, string Hash)> _window;

    /// <param name="threshold">同一（工具, 规范化参数）连续窗口内允许的调用次数上限；达到即 Ask。</param>
    /// <param name="windowCapacity">环形窗口容量（最近 N 次调用参与计数）。</param>
    public DoomLoopGuard(int threshold = 3, int windowCapacity = 64)
    {
        if (threshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "doom-loop threshold must be >= 2");
        }

        if (windowCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowCapacity), "window capacity must be >= 1");
            // 容量 < 阈值是合法配置（窗口内永远数不满 → 守卫不触发），不是错误。
        }

        _threshold = threshold;
        _windowCapacity = windowCapacity;
        _window = new Queue<(string, string)>(windowCapacity);
    }

    /// <inheritdoc />
    public string Name => "doom-loop";

    /// <inheritdoc />
    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        var tool = toolName ?? string.Empty;
        var hash = CanonicalHash(args);

        lock (_sync)
        {
            var seen = 0;
            foreach (var (t, h) in _window)
            {
                if (t == tool && h == hash)
                {
                    seen++;
                }
            }

            _window.Enqueue((tool, hash));
            while (_window.Count > _windowCapacity)
            {
                _window.Dequeue();
            }

            // 本次调用是窗口内第 seen+1 次：达到阈值即强制人工。
            return seen + 1 >= _threshold ? PermissionDecision.Ask : null;
        }
    }

    /// <summary>规范化参数的真实 SHA256（测试直接验证规范化边界）。</summary>
    internal static string CanonicalHash(IReadOnlyDictionary<string, object?>? args)
    {
        var canonical = Canonicalize(args);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// 参数规范化：键按 Ordinal 排序，字符串值去首尾空白并把空白串折叠为单个空格
    /// （"a | b" 与 "a|b" 之外的等价写法归一），其余值用不变文化序列化。
    /// </summary>
    internal static string Canonicalize(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "{}";
        }

        var parts = new List<string>(args.Count);
        foreach (var kv in args.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            parts.Add($"{kv.Key}={NormalizeValue(kv.Value)}");
        }

        return string.Join("\u0002", parts);
    }

    private static string NormalizeValue(object? value) => value switch
    {
        null => "\u0000",
        string s => WhitespaceRx.Replace(s.Trim(), " "),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
