// Copyright (c) AeroCode
// 批次 B G1/G5 澄清接线（builder-δ）：ClarifyToolbox 的端口在组合根的真实适配。
// - ClarificationGatePort：把 question 文本交给 Autonomy 的真实 ClarificationGate 评估
//   （Moa 不反向引用 Autonomy——依赖倒置端口由 App 适配，见 ClarifyToolbox 头注）。
// - AvaloniaClarificationPresenter：真实弹窗征求用户回答。桌面 = 模态 Window；
//   single-view（Android）= OverlayService 覆盖层。返回 null = 用户关闭/取消（诚实失败）。
// - PresenterClarificationResponder：Mission 控制器的澄清应答方，复用同一弹窗端口。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AeroAgent.Autonomy.Clarification;
using AeroAgent.Autonomy.Mission;
using AeroAgent.Moa.Tools;
using AeroCode.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Services;

/// <summary>
/// IClarificationPort 的生产适配：转发到真实 <see cref="ClarificationGate"/>。
/// 评估结果按端口投影（歧义度/是否需要澄清/问题列表/来源），不泄漏 Autonomy 类型之外
/// 的信息——Autonomy 的 Source 枚举以可读字符串透出。
/// </summary>
public sealed class ClarificationGatePort : IClarificationPort
{
    private readonly ClarificationGate _gate;

    public ClarificationGatePort(ClarificationGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async Task<ClarifyEvaluation> EvaluateAsync(string question, CancellationToken ct)
    {
        // 阈值用门的内建默认；EvaluateAsync 对空文本抛 ArgumentException——按评估失败如实上抛，
        // ClarifyToolbox 会转 Fail（不伪造"无需澄清"）。
        var result = await _gate.EvaluateAsync(question, ClarificationGate.DefaultThreshold, ct)
            .ConfigureAwait(false);
        return new ClarifyEvaluation(
            result.AmbiguityScore,
            result.RequiresClarification,
            result.Questions.Select(q => $"{q.Dimension}: {q.Question}").ToList(),
            result.Source.ToString());
    }
}

/// <summary>
/// 澄清弹窗呈现（真实 Avalonia UI）。桌面模态窗口；Android 覆盖层；
/// 无界面生命周期（无头测试）返回 null → ClarifyToolbox 显式 [DEGRADED] 标注（诚实失败）。
/// </summary>
public sealed class AvaloniaClarificationPresenter : IClarificationPresenter
{
    public static readonly SolidColorBrush CardBg = new(Color.FromRgb(0x16, 0x1A, 0x23));
    public static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0x2A, 0x31, 0x42));
    public static readonly SolidColorBrush FgPrimary = new(Color.FromRgb(0xE5, 0xE9, 0xF0));
    public static readonly SolidColorBrush FgMuted = new(Color.FromRgb(0x8A, 0x93, 0xA6));
    public static readonly SolidColorBrush AccentCyan = new(Color.FromRgb(0x06, 0xB6, 0xD4));


    /// <inheritdoc />
    public async ValueTask<string?> PresentAsync(string question, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime
            && lifetime.MainWindow is { } owner)
        {
            var completion = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new ClarifyDialogWindow(question);
                using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
                try
                {
                    return await dialog.ShowDialog<string?>(owner);
                }
                catch (InvalidOperationException)
                {
                    // 属主窗口先行关闭等竞态：无回答 → 调用方诚实失败。
                    return null;
                }
            });
            return completion;
        }

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            return await PresentViaOverlayAsync(question, ct);
        }

        return null;
    }

    /// <summary>Android（single-view）：与授权对话框同一条 OverlayService 覆盖层路径。</summary>
    private static async ValueTask<string?> PresentViaOverlayAsync(string question, CancellationToken ct)
    {
        OverlayService overlay;
        try
        {
            overlay = App.Services.GetRequiredService<OverlayService>();
        }
        catch
        {
            return null;
        }

        if (!overlay.HasHost)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var answerBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72,
                Watermark = "输入回答后提交；关闭 = 不回答",
            };
            var completed = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var card = new Border
            {
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MaxWidth = 520,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var submit = new Button
            {
                Content = "提交回答",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            // 提交 = 记录回答 + 收层（ShowAsync 随之完成）；空回答按不回答处理（诚实）。
            submit.Click += (_, _) =>
            {
                completed.TrySetResult(string.IsNullOrWhiteSpace(answerBox.Text) ? null : answerBox.Text);
                overlay.CloseOverlay(card);
            };

            card.Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "❓ 模型有一个澄清问题",
                        FontSize = 15,
                        FontWeight = FontWeight.Bold,
                        Foreground = AccentCyan,
                    },
                    new TextBlock
                    {
                        Text = question,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = FgPrimary,
                    },
                    answerBox,
                    submit,
                    new TextBlock
                    {
                        Text = "关闭浮层 = 不回答；工具将以失败如实返回。",
                        FontSize = 11,
                        Foreground = FgMuted,
                    },
                },
            };

            // 覆盖层被移除（返回键/TryCloseTop/宿主重挂）= ShowAsync 完成；
            // 若提交尚未发生 → 补 null（无回答），与授权浮层同语义。TrySetResult 幂等。
            try
            {
                await overlay.ShowAsync(card);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            finally
            {
                // 提交路径未经过 CloseOverlay（提交只 TrySetResult）：这里兜底收层。
                overlay.CloseOverlay(card);
                completed.TrySetResult(null);
            }

            return await completed.Task;
        });
    }
}

/// <summary>
/// 桌面澄清窗口（代码构建，与覆盖层卡片同一视觉语义）：问题 + 回答框 + 提交。
/// 关闭窗口（含取消）= 无回答，由 presenter 诚实返回 null。
/// </summary>
public sealed class ClarifyDialogWindow : Window
{
    private readonly TextBox _answer = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 88,
        Watermark = "输入回答后提交；直接关闭 = 不回答",
    };

    public ClarifyDialogWindow()
    {
        Title = "AeroCode 澄清";
        Width = 520;
        MinHeight = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
        };
    }

    public ClarifyDialogWindow(string question) : this()
    {
        var panel = (StackPanel)Content!;
        panel.Children.Add(new TextBlock
        {
            Text = "❓ 模型有一个澄清问题",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = AvaloniaClarificationPresenter.AccentCyan,
        });
        panel.Children.Add(new TextBlock
        {
            Text = question,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AvaloniaClarificationPresenter.FgPrimary,
        });
        panel.Children.Add(_answer);
        var submit = new Button
        {
            Content = "提交回答",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        submit.Click += (_, _) => Close(string.IsNullOrWhiteSpace(_answer.Text) ? null : _answer.Text);
        panel.Children.Add(submit);
        panel.Children.Add(new TextBlock
        {
            Text = "关闭窗口 = 不回答；工具将以失败如实返回。",
            FontSize = 11,
            Foreground = AvaloniaClarificationPresenter.FgMuted,
        });
    }
}

/// <summary>
/// Mission 控制器的澄清应答方（MissionRunOptions.ClarificationResponder）：
/// 逐题弹窗征求用户回答；关闭/取消的题按"未回答"处理（控制器如实记录 UnansweredCount）。
/// </summary>
public sealed class PresenterClarificationResponder : IClarificationResponder
{
    private readonly IClarificationPresenter _presenter;

    public PresenterClarificationResponder(IClarificationPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AnswerAsync(
        IReadOnlyList<ClarificationQuestion> questions, CancellationToken ct)
    {
        var answers = new List<string>();
        foreach (var q in questions)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var answer = await _presenter.PresentAsync($"{q.Dimension}: {q.Question}", ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer))
            {
                break; // 用户停止作答：已答部分照常采纳，其余按未回答。
            }

            answers.Add(answer);
        }

        return answers;
    }
}
