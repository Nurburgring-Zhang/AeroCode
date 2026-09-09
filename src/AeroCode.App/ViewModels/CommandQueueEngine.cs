// Copyright (c) AeroCode V3.0
// CommandQueueEngine — 可复用的指令排队队列（UIR-5 铺全输入框）。
// 逐条自动执行 + 排序/插队/编辑/删除/折叠。执行体由宿主以 executor 委托注入，
// 引擎只负责队列编排，不感知具体业务（AI 助手/笔记 AI/对话 各自注入自己的发送逻辑）。
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AeroCode.App.ViewModels;

/// <summary>队列项：Text 可在队列中直接编辑（双向绑定）。</summary>
public partial class QueuedCommandViewModel : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _text = string.Empty;
}

/// <summary>
/// 通用指令队列引擎。宿主构造时注入：
/// executor(text, ct) —— 执行一条指令（通常内部设置宿主输入框并调用其发送）；
/// isHostIdle —— 宿主当前是否空闲（用于自动开始与运行守卫），可为 null（视为恒空闲）。
/// </summary>
public partial class CommandQueueEngine : ObservableObject
{
    private readonly Func<string, CancellationToken, Task> _executor;
    private readonly Func<bool>? _isHostIdle;
    private CancellationTokenSource? _cts;

    /// <summary>待执行指令队列（按顺序自动执行）。</summary>
    public ObservableCollection<QueuedCommandViewModel> Queue { get; } = new();

    /// <summary>队列面板是否折叠。</summary>
    [ObservableProperty] private bool _isCollapsed = true;

    /// <summary>是否正在自动执行队列。</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>队列状态文本（供宿主展示，可为空）。</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    public CommandQueueEngine(Func<string, CancellationToken, Task> executor, Func<bool>? isHostIdle = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _isHostIdle = isHostIdle;
    }

    private bool HostIdle => _isHostIdle?.Invoke() ?? true;

    /// <summary>把一条指令加入队列（不立即执行）。空闲且未在跑时自动开始。</summary>
    public void Enqueue(string? text)
    {
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            StatusText = "请输入要加入队列的指令";
            return;
        }

        Queue.Add(new QueuedCommandViewModel { Text = t });
        IsCollapsed = false;
        StatusText = $"已加入队列（共 {Queue.Count} 条）";
        if (!IsRunning && HostIdle)
        {
            _ = RunQueueAsync();
        }
    }

    /// <summary>自动执行队列：逐条执行，上一条完成/中断后自动执行下一条，直到队列空或停止。</summary>
    [RelayCommand]
    public async Task RunQueueAsync()
    {
        if (IsRunning) return;
        if (Queue.Count == 0)
        {
            StatusText = "队列为空";
            return;
        }
        if (!HostIdle)
        {
            StatusText = "当前正忙，待完成后再运行队列";
            return;
        }

        IsRunning = true;
        IsCollapsed = false;
        try
        {
            while (IsRunning && Queue.Count > 0)
            {
                var cmd = Queue[0];
                _cts = new CancellationTokenSource();
                try
                {
                    await _executor(cmd.Text, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    StatusText = "当前指令已中断";
                }
                catch (Exception ex)
                {
                    StatusText = $"执行失败：{ex.Message}";
                }
                finally
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                // 无论完成还是中断，都移除已处理的首条并继续下一条（除非已停止）。
                if (Queue.Count > 0 && ReferenceEquals(Queue[0], cmd))
                {
                    Queue.RemoveAt(0);
                }
            }

            StatusText = Queue.Count == 0
                ? "✓ 队列执行完毕"
                : $"队列已停止，剩余 {Queue.Count} 条";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>停止队列自动执行（并中断当前正在执行的条）。</summary>
    [RelayCommand]
    public void StopQueue()
    {
        if (!IsRunning)
        {
            StatusText = "队列未在运行";
            return;
        }

        IsRunning = false;
        _cts?.Cancel();
        StatusText = "正在停止队列…";
    }

    /// <summary>从队列删除一条指令。</summary>
    [RelayCommand]
    public void RemoveQueuedCommand(QueuedCommandViewModel? cmd)
    {
        if (cmd is null) return;
        if (Queue.Remove(cmd))
        {
            StatusText = $"已删除，剩余 {Queue.Count} 条";
        }
    }

    /// <summary>上移一条（排序）。</summary>
    [RelayCommand]
    public void MoveQueuedUpCommand(QueuedCommandViewModel? cmd)
    {
        if (cmd is null) return;
        var idx = Queue.IndexOf(cmd);
        if (idx > 0)
        {
            Queue.Move(idx, idx - 1);
            StatusText = $"已上移到第 {idx} 位";
        }
    }

    /// <summary>插队到队首（下一个执行）。</summary>
    [RelayCommand]
    public void JumpQueuedToFrontCommand(QueuedCommandViewModel? cmd)
    {
        if (cmd is null) return;
        var idx = Queue.IndexOf(cmd);
        if (idx > 0)
        {
            Queue.Move(idx, 0);
            StatusText = "已插队到队首（下一个执行）";
        }
    }

    /// <summary>切换队列面板折叠/展开。</summary>
    [RelayCommand]
    public void ToggleCollapsed() => IsCollapsed = !IsCollapsed;
}
