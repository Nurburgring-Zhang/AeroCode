# Android 构建与打包指南（AeroCode.App.Android）

PHASE 4 产物：`src/AeroCode.App.Android` 头项目（net9.0-android + Avalonia.Android 11.2.2），
与桌面共享同一套 App/MainView/服务栈（单视图生命周期分支），数据落在 app 私有内部存储
（`Context.FilesDir/AeroCode`，免任何存储权限；网络权限仅用于 AI Provider API）。

## 一、环境准备（一次性）

```powershell
# 1. .NET Android workload
dotnet workload install android

# 2. JDK 17（任意发行版，示例为 Adoptium）+ Android SDK（cmdline-tools）
#    设置环境变量：
setx JAVA_HOME "D:\path\to\jdk-17"
setx ANDROID_HOME "D:\path\to\sdk"

# 3. SDK 组件（许可 + platform + build-tools）
"%ANDROID_HOME%\cmdline-tools\latest\bin\sdkmanager.bat" --licenses
"%ANDROID_HOME%\cmdline-tools\latest\bin\sdkmanager.bat" --install platform-tools "platforms;android-35" "build-tools;35.0.0"
```

若 dotnet 找不到 SDK，可在构建时显式传入：
`-p:AndroidSdkDirectory=%ANDROID_HOME% -p:JavaSdkDirectory=%JAVA_HOME%`。

## 二、构建

```powershell
# Debug（快速验证；仅编译，不产 APK）
dotnet build src/AeroCode.App.Android -c Debug

# Debug + 打包（产出调试签名 APK，日常用这条）
# 注意：Debug 默认走"快速部署"，不嵌入托管程序集——那样的 APK 侧载安装将无法启动。
# 必须显式 -p:EmbedAssembliesIntoApk=true 才是可安装的完整包。
dotnet build src/AeroCode.App.Android -c Debug -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true

# Release（未签名包，需第三节的显式签名参数才可安装）
# Release 默认嵌入程序集，但默认启用 AOT（产物 100MB+）；
# 不需要 AOT 时加 -p:RunAOTCompilation=false。
dotnet build src/AeroCode.App.Android -c Release -t:SignAndroidPackage
```

产出位置：`src/AeroCode.App.Android/bin/<配置>/net9.0-android/com.aerocode.app-Signed.apk`
（Debug 自动用 `%LOCALAPPDATA%\.android\debug.keystore` 调试签名；Release 需要下面的显式签名参数）。

构建期可能见到 XA0141 警告（libSkiaSharp.so / libHarfBuzzSharp.so 未按 16KB 页对齐）：
这是上游 NuGet（SkiaSharp/HarfBuzzSharp NativeAssets）对 Android 16 的前瞻提示，
对 minSdk 26 / targetSdk 35 的安装与运行无影响，不阻塞发布。

## 三、APK 打包与签名

### Debug 签名（开发/内测，开箱即用）

```powershell
# EmbedAssembliesIntoApk=true：Debug 默认不嵌入托管程序集（快速部署），缺了它 APK 装不上真机
dotnet build src/AeroCode.App.Android -c Debug -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
```

### Release 签名（对外发布，一次性生成 keystore 后长期复用）

```powershell
# 1) 生成发布 keystore（只做一次；密码与别名请存入密码管理器，仓库内绝不提交）
"%JAVA_HOME%\bin\keytool.exe" -genkeypair -v ^
  -keystore aerocode-release.keystore -alias aerocode ^
  -keyalg RSA -keysize 2048 -validity 10000

# 2) 签名打包
dotnet build src/AeroCode.App.Android -c Release -t:SignAndroidPackage ^
  -p:AndroidPackageFormat=apk ^
  -p:AndroidKeyStore=true ^
  -p:AndroidSigningKeyStore=aerocode-release.keystore ^
  -p:AndroidSigningKeyAlias=aerocode ^
  -p:AndroidSigningKeyPass=<keystore密码> ^
  -p:AndroidSigningStorePass=<store密码>
```

安全纪律：keystore 文件与密码**禁止**进入 git（.gitignore 已覆盖 `*.keystore`）；
密码经命令行/环境变量传入，不落任何配置文件。

### 产物校验

```powershell
"%ANDROID_HOME%\build-tools\35.0.0\aapt2.exe" dump badging <apk路径>
# 关注：package name=com.aerocode.app / versionName / minSdkVersion=26 / targetSdkVersion=35
#       uses-permission: INTERNET（另含 targetSdk 34+ 工具链自动添加的
#       <包名>.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION——自定义签名级，不申请用户资源）
#       launchable-activity: com.aerocode.app.MainActivity

# 托管程序集嵌入校验（badging 看不到的盲区）：
# Debug 快速部署包没有这些条目，侧载安装将无法启动。
python -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1]); print(sum(1 for n in z.namelist() if '/lib_' in n and n.endswith('.dll.so')))" <apk路径>
# 期望输出 > 0（每个 ABI 一份 lib_<程序集>.dll.so）
```

## 四、安装验证

有设备/模拟器时实测：

```powershell
"%ANDROID_HOME%\platform-tools\adb.exe" install -r <apk路径>
"%ANDROID_HOME%\platform-tools\adb.exe" shell am start -n com.aerocode.app/com.aerocode.app.MainActivity
```

无设备环境的验收以 `aapt2 dump badging` 元数据为准，并在发布说明中如实标注"未经真机实测"。

## 五、平台适配说明（与桌面的差异）

| 能力 | 桌面 | Android |
|---|---|---|
| 生命周期 | IClassicDesktopStyleApplicationLifetime + MainWindow | ISingleViewApplicationLifetime + MainView |
| 设置/授权/消息对话框 | 模态 Window | OverlayService 全屏覆盖层（同一视图文件） |
| 数据目录 | %LOCALAPPDATA%/AeroCode | app 私有内部存储（免权限） |
| MCP stdio 子进程 | 可用 | 不可用（子进程在 Android 受限）→ 启动期如实降级 [DEGRADED] |
| AI 助手剪贴板复制 | 可用 | 受限（无主窗口 Clipboard，UI 如实提示） |
| Code Review 文件选择器 | StorageProvider 可用 | 受限（建议粘贴代码，UI 如实提示） |

## 六、构建架构备注（为什么这样配置）

Android 头（net9.0-android，自包含）直接 P2P 引用桌面工程 AeroCode.App（WinExe），
App 再引用 AeroCode.Mcp（Exe）。.NET 9 SDK 有两条默认行为会让这种结构崩掉：

1. **RID 传染**：可执行被引用工程默认接收引用方的 RuntimeIdentifier
   （IsRidAgnostic=false），于是 android-arm64/x64 流入 net9.0 的 exe 工程，
   被迫按 net9.0/android-* 还原运行时包 → NETSDK1047。
   对策：App 与 Mcp 显式 `<IsRidAgnostic>true</IsRidAgnostic>`，
   P2P 构建恒为 portable net9.0；桌面直接构建 / 显式 publish -r 不受影响。
2. **NETSDK1150**：自包含 exe 不得引用非自包含 exe。上面把 Mcp 变 portable 后，
   桌面自包含 publish 与 Android 头都会触发该校验。两处引用方实际都只把
   被引用 exe 当库消费，故按 SDK 对测试项目的同款豁免，在 AeroCode.App 与
   AeroCode.App.Android 设 `ValidateExecutableReferencesMatchSelfContained=false`。

桌面自包含发布目录内的 aerocode-mcp 以自包含 win-x64 单独 publish 后合并
（portable apphost 无法复用同目录运行时，实测 "You must install .NET"，
自包含合并后 aerocode-mcp.exe 独立启动验证通过）。
