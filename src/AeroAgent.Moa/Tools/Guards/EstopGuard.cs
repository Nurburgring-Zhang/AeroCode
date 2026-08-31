// Copyright (c) AeroCode
// EstopGuard — 全链路急停哨兵（批次 B G3，builder-β）。
// 哨兵文件路径构造注入：文件可读（含任选内容标记校验）即对全部工具 Deny，
// 并在触发沿发布一次 EtopTrippedEvent；移除哨兵即自动恢复，重新出现再发布。
// 哨兵"损坏/不可读"按 fail-safe 开关处理：启用=宁可全停（Deny），停用=本守卫弃权。
// 每次检查读一次哨兵文件（无缓存——急停状态必须实时）；判定同时供调度器/主循环入口复用。
using AeroCode.Harness.EventBus;
using AeroCode.Harness.Permission;

namespace AeroAgent.Moa.Tools;

public sealed class EstopGuard : IToolGuard
{
    private readonly string _sentinelPath;
    private readonly EventBus _eventBus;
    private readonly bool _failSafe;
    private readonly string? _expectedMarker;
    private readonly object _sync = new();
    private bool _announced;

    /// <param name="sentinelFilePath">急停哨兵文件路径（构造注入；存在且可读 = 触发）。</param>
    /// <param name="eventBus">触发沿发布 <see cref="EtopTrippedEvent"/>。</param>
    /// <param name="failSafeWhenUnavailable">哨兵损坏/不可读时是否按触发处理（fail-safe，默认启用）。</param>
    /// <param name="expectedMarker">可空内容标记；提供时内容不含该标记视为"损坏"（配合 fail-safe 判定）。</param>
    public EstopGuard(
        string sentinelFilePath,
        EventBus eventBus,
        bool failSafeWhenUnavailable = true,
        string? expectedMarker = null)
    {
        _sentinelPath = string.IsNullOrWhiteSpace(sentinelFilePath)
            ? throw new ArgumentException("estop sentinel path must not be empty", nameof(sentinelFilePath))
            : sentinelFilePath;
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _failSafe = failSafeWhenUnavailable;
        _expectedMarker = string.IsNullOrEmpty(expectedMarker) ? null : expectedMarker;
    }

    /// <inheritdoc />
    public string Name => "estop";

    /// <summary>哨兵当前是否判定为触发（工具守卫之外，调度器/主循环入口复用同一判定）。</summary>
    public bool IsTripped()
    {
        var tripped = Inspect(out var reason);
        AnnounceIfNeeded(tripped, reason);
        return tripped;
    }

    /// <inheritdoc />
    public PermissionDecision? Check(string toolName, IReadOnlyDictionary<string, object?>? args)
        => IsTripped() ? PermissionDecision.Deny : null;

    private bool Inspect(out string reason)
    {
        if (!File.Exists(_sentinelPath))
        {
            reason = string.Empty;
            return false;
        }

        string content;
        try
        {
            content = File.ReadAllText(_sentinelPath);
        }
        catch (Exception)
        {
            // 不可读（被锁/无权限）：fail-safe 启用=按触发处理（宁全停）。
            reason = _failSafe ? "estop sentinel unreadable (fail-safe)" : string.Empty;
            return _failSafe;
        }

        if (_expectedMarker is { } marker && !content.Contains(marker, StringComparison.Ordinal))
        {
            // 损坏（可读但不是有效哨兵内容）：同按 fail-safe 开关处理。
            reason = _failSafe ? "estop sentinel corrupted (fail-safe)" : string.Empty;
            return _failSafe;
        }

        reason = "estop sentinel present";
        return true;
    }

    /// <summary>只在未触发→触发的沿上发布一次事件；恢复后重置，重新触发再发布。</summary>
    private void AnnounceIfNeeded(bool tripped, string reason)
    {
        bool publish;
        lock (_sync)
        {
            if (tripped && !_announced)
            {
                _announced = true;
                publish = true;
            }
            else
            {
                if (!tripped)
                {
                    _announced = false;
                }

                publish = false;
            }
        }

        if (publish && reason.Length > 0)
        {
            _eventBus.Publish(new EtopTrippedEvent(reason, DateTime.UtcNow));
        }
    }
}
