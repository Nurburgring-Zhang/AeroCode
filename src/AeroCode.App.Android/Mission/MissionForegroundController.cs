// Copyright (c) AeroCode V3.0
// R3-A Mission 后台运行：静态启动入口（缝合阶段把它接到 mission 启停路径）。
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace AeroCode.App.Android.Mission;

/// <summary>
/// Mission 前台保活的唯一启动/停止入口（静态、无实例状态）。
/// 契约 A-ANDROID：默认关（MissionForegroundOptions.Enabled=false）时全部方法零行为、
/// 零系统调用（连权限自检都不发生）；开启后启动走前台服务启动 API
///（intent 携带 missionId extra）。
///
/// 缝合需求（本方法体之外的一切接线都归缝合阶段）：
///   1) mission 启动路径调用 <see cref="TryStartMission"/>；
///   2) mission 终止路径调用 <see cref="TryStopMission"/>；
///   3) 设置 android.foregroundService（bool，默认 false，δ builder 落 SettingsService.cs）
///      的值同步进 MissionForegroundOptions.Enabled（建议每次 mission 启动前刷新）。
/// </summary>
public static class MissionForegroundController
{
    private const string LogTag = "AeroCodeMission";

    /// <summary>POST_NOTIFICATIONS 进程级一次性标记：每个进程至多弹一次系统对话框。</summary>
    private static bool _notificationPermissionRequested;

    /// <summary>
    /// 尝试把 mission 送入前台 service 保活（契约静态入口）。
    /// 先查开关：关闭直接返回 false（零系统调用）。
    /// 其余返回 false（不阻断 mission 自身前台运行）的情形：missionId 空白 /
    /// 系统拒绝启动（如 Android 12+ 后台启动 FGS 限制）。
    /// 启动成功后经 <see cref="MainActivity"/> 走 POST_NOTIFICATIONS 一次性申请（API 33+）。
    /// </summary>
    public static bool TryStartMission(Context context, string missionId)
    {
        var decision = MissionForegroundDecisions.DecideMissionStart(MissionForegroundOptions.Enabled, missionId);
        if (!decision.Allowed)
        {
            return false;
        }

        var intent = new Intent(context, typeof(MissionForegroundService))
            .SetAction(MissionForegroundDecisions.ActionStart)
            .PutExtra(MissionForegroundDecisions.ExtraMissionId, decision.MissionId);

        try
        {
            // minSdk=26：StartForegroundService 即可用；服务侧保证 OnStartCommand 内
            // 立即 StartForeground（targetSdk 34+ 的 10s 时限）。
            context.StartForegroundService(intent);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"StartForegroundService rejected: {ex.Message}");
            return false;
        }

        // POST_NOTIFICATIONS 只在本入口（开关开启后首次）经 MainActivity 请求。
        RequestNotificationPermissionIfNeeded(MainActivity.Current);
        return true;
    }

    /// <summary>
    /// 尝试停止前台保活（契约静态入口）。先查开关：关闭直接返回 false（零系统调用）。
    /// 先发 ACTION_STOP 让服务走「释放 wakelock + StopForeground + StopSelf」的优雅路径；
    /// 系统拒绝（后台 StartService 限制）时回退 StopService——OnDestroy 兜底释放
    /// wakelock、系统随服务销毁移除通知。
    /// R4 δ-4：intent 携带 missionId extra——服务侧只移除该 mission 的保活，
    /// 活跃集合归零才 StopForeground + StopSelf；其他在保 mission 不受牵连。
    /// missionId 空白 = 兜底全清语义（无法归属时的保守停止）。
    /// </summary>
    public static bool TryStopMission(Context context, string? missionId = null)
    {
        if (!MissionForegroundDecisions.ShouldAttemptStop(MissionForegroundOptions.Enabled))
        {
            return false; // 关闭态零行为：不触碰系统
        }

        var intent = new Intent(context, typeof(MissionForegroundService))
            .SetAction(MissionForegroundDecisions.ActionStop);
        if (MissionForegroundDecisions.IsUsableMissionId(missionId))
        {
            intent.PutExtra(MissionForegroundDecisions.ExtraMissionId, missionId);
        }

        try
        {
            context.StartService(intent);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Stop intent delivery failed, falling back to StopService: {ex.Message}");
            try
            {
                return context.StopService(new Intent(context, typeof(MissionForegroundService)));
            }
            catch (Exception stopEx)
            {
                Log.Warn(LogTag, $"StopService failed: {stopEx.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// POST_NOTIFICATIONS 运行时申请（API 33+，仅开关开启 + 未授权 + 本进程未请求过时触发）。
    /// 唯一触发路径是 <see cref="TryStartMission"/>（经 MainActivity 最小钩子取得 Activity）；
    /// 进程级一次性标记保证整个进程至多弹一次系统对话框。
    /// </summary>
    public static void RequestNotificationPermissionIfNeeded(Activity? activity)
    {
        // 廉价预过滤（不触碰任何系统 API）：开关关闭 / API<33 / 本进程已请求过 → 直接返回。
        // 关闭态的「零行为变化」由此保证：连权限自检调用都不会发生。
        if (!MissionForegroundDecisions.ShouldRequestNotificationPermission(
                MissionForegroundOptions.Enabled,
                (int)Build.VERSION.SdkInt,
                permissionGranted: false,
                alreadyRequested: _notificationPermissionRequested))
        {
            return;
        }

        if (activity is null)
        {
            return; // 无 Activity 无法 RequestPermissions：本次跳过，下次 TryStartMission 再试
        }

        // CA1416 门（静态分析要求的字面量守卫，语义等同 MinApiForPostNotifications=33）：
        // API<33 不存在该运行时权限，直接放行（无需申请），且不触碰 33+ 的权限常量。
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        var granted = activity.CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted;
        if (!MissionForegroundDecisions.ShouldRequestNotificationPermission(
                MissionForegroundOptions.Enabled,
                (int)Build.VERSION.SdkInt,
                granted,
                _notificationPermissionRequested))
        {
            return;
        }

        _notificationPermissionRequested = true;
        activity.RequestPermissions(
            new[] { Manifest.Permission.PostNotifications },
            MissionForegroundDecisions.NotificationPermissionRequestCode);
    }
}
