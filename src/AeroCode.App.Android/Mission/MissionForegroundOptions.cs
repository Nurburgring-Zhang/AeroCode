// Copyright (c) AeroCode V3.0
// R3-A Mission 后台运行：工程内静态开关（缝合点）。
namespace AeroCode.App.Android.Mission;

/// <summary>
/// Mission 前台 service 总开关（进程级静态配置，默认关）。
///
/// 契约 A-ANDROID：默认 false = 现行为——不启动服务、不建通知渠道、不申请任何权限、
/// 不持有 wakelock。真正的设置接线由缝合阶段完成：
/// δ builder 在 SettingsService.cs 落字段 <c>android.foregroundService</c>（bool，默认 false），
/// 组合根 / mission 启动路径把该设置值同步到这里（建议每次 mission 启动前刷新，
/// 翻转设置无需重启 App 即可生效；关闭时本工程所有入口立即回到零行为）。
/// </summary>
public static class MissionForegroundOptions
{
    /// <summary>默认 false：翻转只能来自显式设置（android.foregroundService=true）。</summary>
    public static bool Enabled { get; set; }
}
