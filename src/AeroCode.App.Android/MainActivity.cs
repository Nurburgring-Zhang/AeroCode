// Copyright (c) AeroCode V3.0
// Android 头项目入口：AvaloniaMainActivity 承载与桌面共享的 App/MainView。
using Android.App;
using Android.Content.PM;
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
