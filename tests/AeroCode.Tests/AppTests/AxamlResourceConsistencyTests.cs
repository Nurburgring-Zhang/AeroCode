using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AeroCode.Tests.AppTests;

/// <summary>
/// AXAML 资源引用一致性回归（P0 哨兵）：每个 {StaticResource K} / {DynamicResource K}
/// 都必须在「本文件 → 宿主窗口 → App.axaml」查找链内解析到已定义资源，
/// 否则 AvaloniaXamlLoader 在运行期抛 XamlException、整个窗口无法加载。
/// 事故案例：S9 的 SettingsDialog MOA 段标题引用 AccentPink，
/// 而该文件 Window.Resources 从未定义此键——设置对话框开窗即抛异常，
/// 又被 MainWindow 的 catch 吞掉，表现为"点设置无反应"，S7-S9 全部 UI 不可达。
/// VM 层单测抓不到这类 XAML 资源错误，故在此静态核对全部视图的资源引用闭包。
/// </summary>
public sealed class AxamlResourceConsistencyTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex ResourceRefRx = new(
        @"\{(StaticResource|DynamicResource)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    [SkippableFact]
    public void AllAxamlResourceReferences_ResolveWithinLookupChain()
    {
        var root = FindRepoRoot();
        Skip.If(root is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var viewsDir = Path.Combine(root!, "src", "AeroCode.App", "Views");
        var appAxaml = Path.Combine(root!, "src", "AeroCode.App", "App.axaml");
        Assert.True(Directory.Exists(viewsDir), $"Views 目录不存在：{viewsDir}");
        Assert.True(File.Exists(appAxaml), $"App.axaml 不存在：{appAxaml}");

        var axamlFiles = Directory.EnumerateFiles(viewsDir, "*.axaml").OrderBy(f => f).ToList();
        var definedKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in axamlFiles.Concat(new[] { appAxaml }))
        {
            definedKeys[f] = ReadDefinedKeys(f);
        }

        var appKeys = definedKeys[appAxaml];
        var mainWindowFile = axamlFiles.FirstOrDefault(f =>
            Path.GetFileName(f).StartsWith("MainWindow", StringComparison.OrdinalIgnoreCase));
        var mainWindowKeys = mainWindowFile is not null
            ? definedKeys[mainWindowFile]
            : new HashSet<string>(StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var file in axamlFiles)
        {
            var content = File.ReadAllText(file);
            var scope = new HashSet<string>(definedKeys[file], StringComparer.Ordinal);
            scope.UnionWith(appKeys);
            if (!IsWindowRoot(content))
            {
                // UserControl 宿主于 MainWindow：Avalonia 资源查找链允许使用宿主窗口资源。
                scope.UnionWith(mainWindowKeys);
            }

            foreach (Match match in ResourceRefRx.Matches(content))
            {
                var key = match.Groups[2].Value;
                if (!scope.Contains(key))
                {
                    failures.Add(
                        $"{Path.GetFileName(file)} 引用 {{{match.Groups[1].Value} {key}}}，" +
                        "可见作用域内未定义（运行期将抛 XamlException，整窗无法加载）");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "存在不可解析的 AXAML 资源引用：\n" + string.Join("\n", failures));
    }

    /// <summary>读取文件内全部 x:Key 定义（含 Window.Resources / UserControl.Resources 等嵌套作用域）。</summary>
    private static HashSet<string> ReadDefinedKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var doc = XDocument.Load(path);
        if (doc.Root is null)
        {
            return keys;
        }

        foreach (var el in doc.Root.DescendantsAndSelf())
        {
            var key = el.Attribute(XamlNs + "Key")?.Value;
            if (!string.IsNullOrEmpty(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>根元素为 Window 的文件自成资源作用域根（独立开窗，不继承宿主）。</summary>
    private static bool IsWindowRoot(string content) =>
        content.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')
               .StartsWith("<Window", StringComparison.Ordinal);

    /// <summary>与 McpRealProcessE2ETests 同款定位：自测试输出目录向上找解决方案根。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
