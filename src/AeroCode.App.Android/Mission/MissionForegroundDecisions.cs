// Copyright (c) AeroCode V3.0
// R3-A Mission 后台运行：前台 service 的常量与纯决策函数。
// 全部成员无副作用、不做系统调用（Activity/Context 一概不碰），便于单测与缝合阶段复核。
namespace AeroCode.App.Android.Mission;

/// <summary>启动被拒原因（供调用方/日志诊断，不进 UI）。</summary>
public enum MissionForegroundStartRejection
{
    None = 0,

    /// <summary>总开关关闭（android.foregroundService=false）——契约要求零行为。</summary>
    SwitchDisabled,

    /// <summary>missionId 缺失/空白——无法把 wakelock/通知归属到具体任务。</summary>
    MissionIdMissing,
}

/// <summary>TryStartMission 的决策结果。</summary>
public readonly record struct MissionForegroundStartDecision(
    bool Allowed,
    MissionForegroundStartRejection Rejection,
    string? MissionId);

/// <summary>
/// 前台保活的契约常量与纯决策面。
/// 契约 A-ANDROID：渠道 id=aerocode-mission；missionId 经 intent extra 传递；
/// 开关默认关（MissionForegroundOptions.Enabled=false）时所有入口零行为。
/// </summary>
public static class MissionForegroundDecisions
{
    /// <summary>通知渠道 id（钉死勿改：系统设置里对用户可见，且是契约项）。</summary>
    public const string ChannelId = "aerocode-mission";

    /// <summary>Intent extra：missionId。</summary>
    public const string ExtraMissionId = "missionId";

    /// <summary>Intent action：开始一次 mission 保活。</summary>
    public const string ActionStart = "com.aerocode.app.action.MISSION_START";

    /// <summary>Intent action：结束保活并自停。</summary>
    public const string ActionStop = "com.aerocode.app.action.MISSION_STOP";

    /// <summary>前台通知 id（单通知模型：StartForeground 用）。</summary>
    public const int NotificationId = 0xAE01;

    /// <summary>POST_NOTIFICATIONS 运行时申请的 requestCode（进程内唯一即可）。</summary>
    public const int NotificationPermissionRequestCode = 0xAE02;

    /// <summary>POST_NOTIFICATIONS 运行时权限自 API 33 起存在。</summary>
    public const int MinApiForPostNotifications = 33;

    /// <summary>targetSdk ≥ 34 时 FGS 必须声明具体 type（本工程为 dataSync）。</summary>
    public const int MinApiForFgsTypeEnforcement = 34;

    /// <summary>missionId 可用性（非空白即可；格式校验归 mission 层，此处只兜底）。</summary>
    public static bool IsUsableMissionId(string? missionId) => !string.IsNullOrWhiteSpace(missionId);

    /// <summary>是否允许把 mission 送入前台 service 保活（纯决策）。</summary>
    public static MissionForegroundStartDecision DecideMissionStart(bool enabled, string? missionId)
    {
        if (!enabled)
        {
            return new(false, MissionForegroundStartRejection.SwitchDisabled, missionId);
        }

        if (!IsUsableMissionId(missionId))
        {
            return new(false, MissionForegroundStartRejection.MissionIdMissing, missionId);
        }

        return new(true, MissionForegroundStartRejection.None, missionId);
    }

    /// <summary>是否允许尝试停止前台保活（关闭态零行为：不触碰系统）。</summary>
    public static bool ShouldAttemptStop(bool enabled) => enabled;

    /// <summary>
    /// 是否应在本进程内申请 POST_NOTIFICATIONS。
    /// 语义：开关开启 + API≥33 + 尚未授权 + 本进程尚未请求过（只弹一次）。
    /// </summary>
    public static bool ShouldRequestNotificationPermission(
        bool enabled, int apiLevel, bool permissionGranted, bool alreadyRequested) =>
        enabled
        && apiLevel >= MinApiForPostNotifications
        && !permissionGranted
        && !alreadyRequested;

    /// <summary>
    /// targetSdk 对应的 manifest 声明值 <c>android:foregroundServiceType</c>（纯决策）。
    /// ≥34：dataSync（需配 FOREGROUND_SERVICE_DATA_SYNC 权限）；更早版本无强制 → 空串=不声明。
    /// </summary>
    public static string ResolveForegroundServiceType(int targetSdk) =>
        targetSdk >= MinApiForFgsTypeEnforcement ? "dataSync" : string.Empty;
}
