// Copyright (c) AeroCode
// CommandClassifierGuard — shell 命令结构静态分级（批次 B G3，builder-β）。
// 纯词法分析，绝不执行、绝不联网：分词（引号感知）→ 结构类别 → 审慎度裁决——
// 单命令 Allow / 管道·重定向·链式·解析失败 Ask / 命令替换·嵌套 Deny。
// 分级只表达结构审慎度：Allow 不越过策略的 Deny/Ask（ToolRouter 已保证防线只升不降）。
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

/// <summary>shell 命令结构类别（静态分级结果，可机检）。</summary>
public enum ShellCommandClass
{
    /// <summary>单命令：git status</summary>
    Single,

    /// <summary>管道：a | b</summary>
    Pipeline,

    /// <summary>重定向：&gt; &gt;&gt; &lt; 2&gt;&amp;1</summary>
    Redirection,

    /// <summary>链式/多命令：a &amp;&amp; b / a ; b / a &amp; / 多行</summary>
    Chained,

    /// <summary>命令替换/嵌套：$(…) / `…`</summary>
    Substitution,

    /// <summary>解析失败：引号不闭合 / 裸括号 / 空命令</summary>
    ParseFailure,
}

/// <summary>shell 命令结构分类器（引号感知单趟扫描；public 静态以便测试直接钉住分级边界）。</summary>
public static class CommandClassifier
{
    /// <summary>对命令做结构分级；空/空白命令视为解析失败（宁可 Ask）。</summary>
    public static ShellCommandClass Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ShellCommandClass.ParseFailure;
        }

        var inSingle = false;
        var inDouble = false;
        var pipeline = false;
        var redirection = false;
        var chained = false;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (inSingle || inDouble)
            {
                continue;
            }

            switch (ch)
            {
                case '\\' when i + 1 < command.Length && IsMeta(command[i + 1]):
                    i++; // 转义的元字符不是结构操作符
                    break;
                case '`':
                    return ShellCommandClass.Substitution;
                case '$' when i + 1 < command.Length && command[i + 1] == '(':
                    return ShellCommandClass.Substitution;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|')
                    {
                        chained = true;
                        i++;
                    }
                    else
                    {
                        pipeline = true;
                    }

                    break;
                case '&':
                    if (i + 1 < command.Length && command[i + 1] == '&')
                    {
                        chained = true;
                        i++;
                    }
                    else
                    {
                        chained = true; // 后台执行同样是多控制流
                    }

                    break;
                case ';':
                    chained = true;
                    break;
                case '>' or '<':
                    redirection = true;
                    break;
                case '(' or ')':
                    // 引号外的裸括号 = 未知结构（子 shell/数组/残缺输入），宁可 Ask。
                    return ShellCommandClass.ParseFailure;
                case '\n' or '\r':
                    chained = true; // 多行脚本 = 多命令
                    break;
            }
        }

        if (inSingle || inDouble)
        {
            return ShellCommandClass.ParseFailure; // 引号不闭合 = 解析失败
        }

        if (pipeline)
        {
            return ShellCommandClass.Pipeline;
        }

        if (redirection)
        {
            return ShellCommandClass.Redirection;
        }

        if (chained)
        {
            return ShellCommandClass.Chained;
        }

        return ShellCommandClass.Single;
    }

    private static bool IsMeta(char c) => "|&;<>()$`\"'\\".Contains(c);
}

/// <summary>
/// 命令结构分级守卫：args 含 command 字符串时按 <see cref="CommandClassifier"/> 分级裁决。
/// </summary>
public sealed class CommandClassifierGuard : IToolGuard
{
    /// <inheritdoc />
    public string Name => "command-classifier";

    /// <inheritdoc />
    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || !args.TryGetValue("command", out var v) || v is not string command)
        {
            return null;
        }

        return DecisionFor(CommandClassifier.Classify(command));
    }

    /// <summary>类别 → 审慎度裁决（internal 供测试直接验证映射表）。</summary>
    internal static PermissionDecision DecisionFor(ShellCommandClass cls) => cls switch
    {
        ShellCommandClass.Single => PermissionDecision.Allow,
        ShellCommandClass.Substitution => PermissionDecision.Deny,
        _ => PermissionDecision.Ask, // Pipeline / Redirection / Chained / ParseFailure
    };
}
