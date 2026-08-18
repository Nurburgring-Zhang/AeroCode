// Copyright (c) AeroCode V3.0
// Android 头项目入口：AvaloniaMainActivity 承载与桌面共享的 App/MainView。
using Android.App;
using Android.Content.PM;
using AeroCode.App.Services;
using Avalonia;
using Avalonia.Android;

namespace AeroCode.App.Android;

[Activity(
    Label = "AeroCode",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
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
}
