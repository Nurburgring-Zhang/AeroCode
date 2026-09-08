// Copyright (c) AeroCode V3.0
// R3-A Mission 后台运行：前台 service——常驻低优先级通知 + PARTIAL_WAKE_LOCK。
// 契约 A-ANDROID：
//   - 经 Context.StartForegroundService 启动，intent 携带 missionId extra；
//   - 通知渠道 id = aerocode-mission（IMPORTANCE_LOW，OnCreate 创建，幂等）；
//   - wakelock 仅 mission 运行期持有：锁本体 SetReferenceCounted(false)，
//     引用计数语义由活跃 mission 集合承担——集合 0→1 时 Acquire 一次、
//     归零（最后一个 mission 移除/停止路径/OnDestroy）时 Release 一次；
//   - 停止路径 StopForeground + StopSelf；OnDestroy 兜底释放；
//   - 与数据根/AppDataPaths 无关（不触碰）。
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace AeroCode.App.Android.Mission;

[Service(
    Name = "com.aerocode.app.MissionForegroundService",
    Exported = false,
    Enabled = true,
    // targetSdk=35（csproj 钉值）≥34：FGS 必须声明具体 type；保活属于数据同步型长任务。
    // 与 Properties/AndroidManifest.xml 的 service 节点声明保持一致（合并时零冲突）。
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public class MissionForegroundService : Service
{
    private const string ChannelTitle = "AeroCode Mission";
    private const string ChannelDescription = "Keeps the current mission running in the background";
    private const string ContentTitle = "AeroCode";

    /// <summary>契约：通知内容显示 missionId 与「运行中」。</summary>
    private const string ContentTextFormat = "Mission {0} 运行中";
    private const string WakeLockTag = "AeroCode:MissionForeground";
    private const string LogTag = "AeroCodeMission";
    private const int PendingIntentRequestId = 0xAE01;

    /// <summary>活跃 mission 集合（引用计数的承担者：同一 missionId 幂等去重）。</summary>
    private readonly HashSet<string> _activeMissions = new(StringComparer.Ordinal);

    /// <summary>PARTIAL_WAKE_LOCK（本体非引用计数，SetReferenceCounted(false)）；
    /// 非空 = 锁对象已创建，IsHeld = 当前持有。</summary>
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        // 渠道必须先于 StartForeground 存在：targetSdk 34+ 下通知缺席会被系统判为
        // ForegroundServiceDidNotShowInTime（服务无法进入前台态）。CreateNotificationChannel 幂等。
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == MissionForegroundDecisions.ActionStop)
        {
            RemoveMission(intent.GetStringExtra(MissionForegroundDecisions.ExtraMissionId));
            if (_activeMissions.Count == 0)
            {
                StopForegroundAndSelf();
            }

            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == MissionForegroundDecisions.ActionStart)
        {
            var missionId = intent.GetStringExtra(MissionForegroundDecisions.ExtraMissionId);
            if (!MissionForegroundDecisions.IsUsableMissionId(missionId))
            {
                // 无法归属任务的保活请求：不进前台、不持锁，直接自停。
                StopForegroundAndSelf();
                return StartCommandResult.NotSticky;
            }

            // 引用计数由集合承担：同一 missionId 重复启动幂等（Add=false 不再触发持锁）；
            // AcquireWakeLock 内部再查 IsHeld，集合非空期间锁只持一次。
            if (_activeMissions.Add(missionId!))
            {
                AcquireWakeLock();
            }

            StartForegroundWithNotification(missionId!);
            return StartCommandResult.NotSticky;
        }

        // null intent（异常重建路径）或未知 action：本服务声明 NotSticky，保守自停，
        // 绝不在无 mission 归属时空转持锁。
        StopForegroundAndSelf();
        return StartCommandResult.NotSticky;
    }

    /// <summary>API 34+：shortService 型超时回调（本服务未用 shortService，防御性终止）。</summary>
    public override void OnTimeout(int startId) => StopForegroundAndSelf();

    /// <summary>API 35+：dataSync 型 FGS 有 6h/24h 配额，超时系统回调这里；立即终止并释放。</summary>
    public override void OnTimeout(int startId, ForegroundService flags) => StopForegroundAndSelf();

    public override void OnDestroy()
    {
        // 兜底：无论何种销毁路径（StopSelf/StopService/系统回收），wakelock 都在这里释放；
        // 进程被杀时由内核按 Binder 归属回收 wakelock。
        ReleaseWakeLock();
        _wakeLock = null;
        _activeMissions.Clear();
        base.OnDestroy();
    }

    /// <summary>渠道只建一次语义由系统幂等保证（同 id 重复创建 = no-op/更新）。</summary>
    private void EnsureNotificationChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null)
        {
            return;
        }

        var channel = new NotificationChannel(
            MissionForegroundDecisions.ChannelId,
            ChannelTitle,
            NotificationImportance.Low)
        {
            Description = ChannelDescription,
        };
        channel.EnableLights(false);
        channel.EnableVibration(false);
        manager.CreateNotificationChannel(channel);
    }

    /// <summary>
    /// 持锁（契约：PowerManager.NewWakeLock）。锁本体 SetReferenceCounted(false)——
    /// 引用计数语义由活跃 mission 集合承担：集合 0→1 时 Acquire 一次（IsHeld 守卫防重复），
    /// 归零时经 <see cref="ReleaseWakeLock"/> 释放一次。锁对象惰性创建，进程内单例。
    /// 契约：获取失败不崩溃——全程 try/catch + 日志，失败仅放弃保活不阻断前台通知。
    /// </summary>
    private void AcquireWakeLock()
    {
        try
        {
            if (_wakeLock is null)
            {
                var power = (PowerManager?)GetSystemService(PowerService);
                if (power is null)
                {
                    Log.Warn(LogTag, "PowerService unavailable; mission keep-alive proceeds without wakelock");
                    return;
                }

                var wakeLock = power.NewWakeLock(WakeLockFlags.Partial, WakeLockTag);
                if (wakeLock is null)
                {
                    Log.Warn(LogTag, "NewWakeLock returned null; proceeding without wakelock");
                    return;
                }

                // 引用计数由活跃 mission 集合承担（契约哨兵钉死）：锁本体关闭内核侧引用计数，
                // 持/放时机完全由集合的 0↔1 迁移驱动。
                wakeLock.SetReferenceCounted(false);
                _wakeLock = wakeLock;
            }

            if (!_wakeLock.IsHeld)
            {
                // R4 δ-4：带时长上限的持锁——超限自动释放，杜绝 mission 挂死时无限耗电。
                _wakeLock.Acquire(MissionForegroundDecisions.MaxWakeLockHoldMs);
            }
        }
        catch (Exception ex)
        {
            // 契约：wakelock 获取失败不崩溃（try/catch + 日志）。
            Log.Warn(LogTag, $"Wakelock acquire failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放 wakelock（唯一释放入口：最后一个 mission 移除 / 停止路径 / OnDestroy 兜底）。
    /// 未持有则 no-op；失败仅记日志不崩溃。
    /// </summary>
    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is { IsHeld: true })
            {
                _wakeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Wakelock release failed: {ex.Message}");
        }
    }

    private void StartForegroundWithNotification(string missionId)
    {
        var builder = new Notification.Builder(this, MissionForegroundDecisions.ChannelId)
            .SetSmallIcon(Resource.Drawable.icon)
            .SetContentTitle(ContentTitle)
            .SetContentText(string.Format(ContentTextFormat, missionId))
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService);

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // targetSdk 31+ 的 FGS 通知默认可能被延迟展示，而 API 34+ 要求服务启动后
            // 通知 10s 内可见——强制 IMMEDIATE。
            builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        }

        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        if (launch is not null)
        {
            // 点通知回到 App；API 31+ 要求 PendingIntent 必须 IMMUTABLE。
            builder.SetContentIntent(PendingIntent.GetActivity(
                this,
                PendingIntentRequestId,
                launch,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
        }

        try
        {
            StartForeground(MissionForegroundDecisions.NotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            // 系统拒绝前台态（如 Android 12+ 后台启动限制）：记录并自停，绝不崩溃。
            Log.Error(LogTag, $"StartForeground failed: {ex.Message}");
            StopSelf();
        }
    }

    /// <summary>契约停止路径：释放 wakelock → 移除前台态 → 自停。</summary>
    private void StopForegroundAndSelf()
    {
        ReleaseWakeLock();
        _activeMissions.Clear();
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
#pragma warning disable CA1422 // minSdk=26：API 26-32 必须走 bool 重载（33+ 才标过时）；true=移除通知（对齐 Remove 语义）
            StopForeground(true);
#pragma warning restore CA1422
        }

        StopSelf();
    }

    /// <summary>停止单个 mission（missionId 为 null/空白 = 清空全部活跃任务）。
    /// wakelock 的释放不在此逐次进行：由调用方在集合归零时走 StopForegroundAndSelf
    /// （引用计数由集合承担，锁只随 0↔1 迁移持/放一次）。</summary>
    private void RemoveMission(string? missionId)
    {
        if (MissionForegroundDecisions.IsUsableMissionId(missionId))
        {
            _activeMissions.Remove(missionId!);
        }
        else
        {
            _activeMissions.Clear();
        }
    }
}
