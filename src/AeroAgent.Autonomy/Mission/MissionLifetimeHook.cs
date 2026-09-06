// Copyright (c) AeroCode V3.0
// R3 缝合（α S8）：mission 生命周期静态钩子——跨工程单向依赖的缝合点。
//
// 背景：App 工程（MissionController 的宿主引用方）不能引用 Android 头工程，
// AeroAgent.Autonomy（MissionController 所在工程）也不能反向引用 AeroCode.App，
// 依赖链是 Android → App → Autonomy 单向。因此「mission 启停 → 前台保活」
// 的通知面只能落在两者都能看到的最低层（本工程），由平台头工程（Android）订阅。
//
// 用途：MissionController.RunAsync 入口触发 MissionStarted(missionId)、
// finally 触发 MissionStopped。Android 侧 MainActivity 订阅后把设置
// android.foregroundService 同步进 MissionForegroundOptions.Enabled 并
// 启停前台 service。
//
// 约定：
//   - 触发方（MissionController）对订阅方异常一律 try/catch 吞掉，绝不阻断 mission；
//   - 订阅方必须不抛（平台侧自行 try/catch 兜底）；
//   - 无订阅者（桌面端）= 零行为变化、零开销（null 检查短路）。
namespace AeroAgent.Autonomy.Mission;

/// <summary>mission 生命周期静态钩子（订阅方必须不抛；触发方吞异常绝不阻断 mission）。</summary>
public static class MissionLifetimeHook
{
    /// <summary>mission 进入运行（参数 = missionId）；订阅方必须不抛。</summary>
    public static Action<string>? MissionStarted;

    /// <summary>mission 结束（成功/失败/取消统一）；订阅方必须不抛。</summary>
    public static Action? MissionStopped;
}
