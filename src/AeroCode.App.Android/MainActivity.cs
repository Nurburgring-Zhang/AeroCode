// Copyright (c) AeroCode V3.0
// Android 头项目入口：AvaloniaMainActivity 承载与桌面共享的 App/MainView。
using Android.App;
using Android.Content.PM;
using Android.OS;
using AeroAgent.Autonomy.Mission;
using AeroCode.App.Configuration;
using AeroCode.App.Services;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;

namespace AeroCode.App.Android;

[Activity(
    Name = "com.aerocode.app.MainActivity",
    Label = "AeroCode",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    // 全量声明配置变更自行处理：Activity 重建会撕裂 Avalonia 视图树与覆盖层状态，
    // 旋转/分屏/键盘/密度变化时保持同一实例。
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        | ConfigChanges.UiMode
        | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 数据根目录 → Android app 私有内部存储（免任何存储权限）。
        // 必须在 Avalonia App 初始化（BuildServices 构造 AppDataPaths）之前设置。
        var filesDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
        if (!string.IsNullOrEmpty(filesDir))
        {
            AppDataPaths.RootDirectoryOverride = System.IO.Path.Combine(filesDir, "AeroCode");
        }

        return base.CustomizeAppBuilder(builder);
    }

    /// <summary>
    /// R3-A：POST_NOTIFICATIONS 运行时申请的最小钩子。
    /// 契约：请求只发生在「开关开启后首次 TryStartMission」，且必须经 Activity 发出；
    /// 静态入口 MissionForegroundController.TryStartMission 通过 Current 取得当前 Activity。
    /// 默认关（MissionForegroundOptions.Enabled=false）时无人消费该钩子，零行为变化。
    /// </summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;

        // R3 缝合（α S8）：mission 生命周期 → 前台保活。桌面端无此订阅（钩子在
        // AeroAgent.Autonomy，仅 Android 头工程订阅）= 桌面零行为变化。
        // 订阅方必须不抛：全部系统调用 try/catch 兜底，保活失败绝不阻断 mission。
        MissionLifetimeHook.MissionStarted += OnMissionLifetimeStarted;
        MissionLifetimeHook.MissionStopped += OnMissionLifetimeStopped;
    }

    protected override void OnDestroy()
    {
        MissionLifetimeHook.MissionStarted -= OnMissionLifetimeStarted;
        MissionLifetimeHook.MissionStopped -= OnMissionLifetimeStopped;

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
    }

    /// <summary>
    /// R3 缝合（α S8）：mission 启动 → 前台保活。每次启动前先从 SettingsService 读
    /// android.foregroundService 刷新 MissionForegroundOptions.Enabled（设置翻转免重启）；
    /// 开关关时 TryStartMission 内部零系统调用直接返回 false。
    /// 经 UI 线程执行（POST_NOTIFICATIONS 的 RequestPermissions 须在主线程）。
    /// </summary>
    private void OnMissionLifetimeStarted(string missionId)
    {
        try
        {
            RunOnUiThread(() =>
            {
                try
                {
                    try
                    {
                        var settings = App.Services.GetService<SettingsService>();
                        Mission.MissionForegroundOptions.Enabled =
                            settings?.Current.Android.ForegroundService ?? false;
                    }
                    catch
                    {
                        // 服务容器尚未就绪：保持开关现状（默认 false = 零行为）。
                    }

                    Mission.MissionForegroundController.TryStartMission(this, missionId);
                }
                catch
                {
                    // 保活失败不阻断 mission（契约：返回 false = 保活未生效而已）。
                }
            });
        }
        catch
        {
            // RunOnUiThread 本身失败（Activity 正在销毁）：放弃保活，不阻断 mission。
        }
    }

    /// <summary>R3 缝合（α S8）：mission 结束 → 停前台保活（开关关时零系统调用）。</summary>
    private void OnMissionLifetimeStopped()
    {
        try
        {
            RunOnUiThread(() =>
            {
                try
                {
                    Mission.MissionForegroundController.TryStopMission(this);
                }
                catch
                {
                    // 停止失败不阻断（服务侧 OnDestroy 兜底释放 wakelock）。
                }
            });
        }
        catch
        {
            // 同上：放弃停止动作，不抛出。
        }
    }

    /// <summary>
    /// 系统返回键：有覆盖层（设置/授权/消息）时逐层关闭最上层卡片并消费事件，
    /// 无覆盖层时交还基类（正常退出 App）。
    /// Avalonia 11.2.2 尚无 TopLevel.BackRequested（11.3+ API），Activity 层拦截是
    /// 本版本的正确路径；主线程回调满足 OverlayService 的 UI 线程约束。
    /// CA1422 豁免：OnBackPressed 在 API 33+ 标记过时，但本工程未在 manifest 开启
    /// predictive back 回调，框架默认 OnBackInvokedCallback 仍委托到它；
    /// 且 minSdk=26 需要同一实现覆盖 API 26-32。
    /// </summary>
#pragma warning disable CA1422 // 平台兼容性：见上方豁免说明
    public override void OnBackPressed()
    {
        try
        {
            var overlay = App.Services.GetRequiredService<OverlayService>();
            if (overlay.HasOpenOverlays && overlay.TryCloseTop())
            {
                return;
            }
        }
        catch { /* 服务容器尚未构建：走默认返回行为 */ }

        base.OnBackPressed();
    }
#pragma warning restore CA1422
}
