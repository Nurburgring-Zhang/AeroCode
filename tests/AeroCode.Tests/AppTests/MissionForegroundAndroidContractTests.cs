// Copyright (c) AeroCode
// R3-A（契约 A-ANDROID）哨兵：Android head 的 Mission 前台 service 能力以
// 「源码 / 清单静态扫描」守门。tests 工程为 net9.0，无法引用 net9.0-android 头工程程序集，
// 故按 AxamlResourceConsistencyTests 同款 FindRepoRoot 模式直接核对源码文件——
// 守住：清单权限/service 节点、开关默认关、渠道 id、门控先于系统调用的次序。
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AeroCode.Tests.AppTests;

public sealed class MissionForegroundAndroidContractTests
{
    private static readonly string? RepoRoot = FindRepoRoot();

    /// <summary>
    /// Android manifest 的属性带 android: 前缀（xmlns:android 命名空间）——合法 Android
    /// manifest 的强制形态（impl_alpha.md 契约记录：manifest 权限/service 节点均经
    /// android:name 等前缀属性声明，obj 合并产物已核验）。哨兵读取须带同一命名空间，
    /// 否则恒读到 null（R3 缝合 S9 裁决：修读取侧，钉死的值逐项不变）。
    /// </summary>
    private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";

    private static string AndroidPath(params string[] parts) =>
        Path.Combine(new[] { RepoRoot!, "src", "AeroCode.App.Android" }.Concat(parts).ToArray());

    private static string ManifestPath => AndroidPath("Properties", "AndroidManifest.xml");

    [SkippableFact]
    public void Manifest_DeclaresAllRequiredPermissions()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var doc = XDocument.Load(ManifestPath);
        var permissions = doc.Root!.Descendants("uses-permission")
            .Select(e => e.Attribute(AndroidNs + "name")?.Value)
            .ToHashSet();

        Assert.Contains("android.permission.FOREGROUND_SERVICE", permissions);
        Assert.Contains("android.permission.FOREGROUND_SERVICE_DATA_SYNC", permissions); // targetSdk≥34 强制
        Assert.Contains("android.permission.WAKE_LOCK", permissions);
        Assert.Contains("android.permission.POST_NOTIFICATIONS", permissions);
    }

    [SkippableFact]
    public void Manifest_ServiceNode_MatchesContractNameExportAndType()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var doc = XDocument.Load(ManifestPath);
        var service = doc.Root!.Descendants("service").Single();

        Assert.Equal("com.aerocode.app.MissionForegroundService", service.Attribute(AndroidNs + "name")?.Value);
        Assert.Equal("false", service.Attribute(AndroidNs + "exported")?.Value);
        Assert.Equal("dataSync", service.Attribute(AndroidNs + "foregroundServiceType")?.Value);
    }

    [SkippableFact]
    public void CsprojTargetSdk_IsConsistentWithDataSyncServiceType()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var csproj = File.ReadAllText(AndroidPath("AeroCode.App.Android.csproj"));
        var match = Regex.Match(csproj, @"<AndroidTargetSdkVersion>(\d+)</AndroidTargetSdkVersion>");
        Assert.True(match.Success, "csproj 必须显式钉住 AndroidTargetSdkVersion");
        var targetSdk = int.Parse(match.Groups[1].Value);

        // 与 MissionForegroundDecisions.ResolveForegroundServiceType 同语义（跨程序集无法直接调用）。
        var expectedType = targetSdk >= 34 ? "dataSync" : string.Empty;
        var manifest = File.ReadAllText(ManifestPath);
        var actualType = XDocument.Parse(manifest).Root!.Descendants("service").Single()
            .Attribute(AndroidNs + "foregroundServiceType")?.Value ?? string.Empty;

        Assert.Equal(expectedType, actualType);

        // 钉值常量勿漂移：ResolveForegroundServiceType 的分界线写死 34。
        var decisions = File.ReadAllText(AndroidPath("Mission", "MissionForegroundDecisions.cs"));
        Assert.Contains("MinApiForFgsTypeEnforcement = 34", decisions);
    }

    [SkippableFact]
    public void Options_SwitchDefaultsToFalse_ZeroBehaviorWhenOff()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundOptions.cs"));
        // 默认关：属性声明不得带任何初始化器（尤其禁止 = true）。
        Assert.Matches(@"public\s+static\s+bool\s+Enabled\s*\{\s*get;\s*set;\s*\}", source);
        Assert.DoesNotMatch(@"Enabled\s*\{\s*get;\s*set;\s*\}\s*=\s*true", source);
        Assert.DoesNotContain("Enabled => true", source);
    }

    [SkippableFact]
    public void Decisions_ContractConstantsArePinned()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundDecisions.cs"));
        Assert.Contains("ChannelId = \"aerocode-mission\"", source);
        Assert.Contains("ExtraMissionId = \"missionId\"", source);
        Assert.Contains("ActionStart = \"com.aerocode.app.action.MISSION_START\"", source);
        Assert.Contains("ActionStop = \"com.aerocode.app.action.MISSION_STOP\"", source);
        Assert.Contains("MinApiForPostNotifications = 33", source);
    }

    [SkippableFact]
    public void Controller_DecisionGates_PrecedeAllSystemCalls()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundController.cs"));

        // 启动路径：DecideMissionStart（含开关门）必须先于 StartForegroundService。
        Assert.InRange(source.IndexOf("DecideMissionStart(MissionForegroundOptions.Enabled", StringComparison.Ordinal),
            0, source.IndexOf("StartForegroundService", StringComparison.Ordinal));

        // 停止路径：ShouldAttemptStop（含开关门）必须先于 StartService。
        Assert.InRange(source.IndexOf("ShouldAttemptStop(MissionForegroundOptions.Enabled", StringComparison.Ordinal),
            0, source.IndexOf("context.StartService(intent)", StringComparison.Ordinal));

        // 权限路径：ShouldRequestNotificationPermission（含开关门）必须先于 CheckSelfPermission。
        Assert.InRange(source.IndexOf("ShouldRequestNotificationPermission(", StringComparison.Ordinal),
            0, source.IndexOf("CheckSelfPermission", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Service_WakelockLifecycle_ReleasesOnStopAndDestroy()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundService.cs"));

        Assert.Contains("WakeLockFlags.Partial", source);          // 只持 PARTIAL 锁
        Assert.Contains("SetReferenceCounted(false)", source);     // 引用计数由活跃 mission 集合承担
        // OnDestroy 兜底释放 + 契约停止路径（StopForeground → StopSelf）。
        Assert.Matches(@"public\s+override\s+void\s+OnDestroy\(\)\s*\{[^}]*ReleaseWakeLock\(\)", source);
        Assert.Matches(new Regex(
                @"private\s+void\s+StopForegroundAndSelf\(\)\s*\{.*?ReleaseWakeLock\(\);.*?StopForeground\(.*?StopSelf\(\);",
                RegexOptions.Singleline),
            source);
        // 通道 id 来自钉死常量，而非散落字面量。
        Assert.Contains("MissionForegroundDecisions.ChannelId", source);
    }

    [SkippableFact]
    public void Controller_StopIntent_CarriesMissionId_OnlyTargetMissionStopped()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        // R4 δ-4：stop intent 携带 missionId extra → 服务侧只移除该 mission 的保活，
        // 不再全清在保 mission（TryStopMission 方法体内必须出现 PutExtra(ExtraMissionId)）。
        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundController.cs"));
        var stopStart = source.IndexOf("public static bool TryStopMission", StringComparison.Ordinal);
        var stopEnd = source.IndexOf("RequestNotificationPermissionIfNeeded", stopStart + 1, StringComparison.Ordinal);
        Assert.True(stopStart >= 0 && stopEnd > stopStart, "TryStopMission 方法体定位失败");

        var stopBody = source[stopStart..stopEnd];
        Assert.Contains("PutExtra(MissionForegroundDecisions.ExtraMissionId, missionId)", stopBody, StringComparison.Ordinal);
        Assert.Contains("IsUsableMissionId(missionId)", stopBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Service_WakelockAcquire_IsTimeBounded()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        // R4 δ-4：wakelock 必须带时长上限获取；禁止无上限的裸 Acquire()。
        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundService.cs"));
        Assert.Contains("_wakeLock.Acquire(MissionForegroundDecisions.MaxWakeLockHoldMs)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_wakeLock.Acquire()", source, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Decisions_MaxWakeLockHoldMs_IsPinned()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        // R4 δ-4：持锁上限钉死 6h（对齐 API 35 dataSync FGS 配额下界），勿漂移。
        var source = File.ReadAllText(AndroidPath("Mission", "MissionForegroundDecisions.cs"));
        Assert.Contains("MaxWakeLockHoldMs = 6 * 60 * 60 * 1000L", source, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void LifetimeHook_MissionStopped_CarriesMissionId()
    {
        Skip.If(RepoRoot is null, "测试程序集目录向上未找到 AeroCode.sln——无源码树环境跳过");

        // R4 δ-4：MissionStopped 钩子携带 missionId（停止侧据此只解除该 mission 的保活）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }

        var hookPath = Path.Combine(dir!.FullName, "src", "AeroAgent.Autonomy", "Mission", "MissionLifetimeHook.cs");
        var source = File.ReadAllText(hookPath);
        Assert.Matches(@"public\s+static\s+Action<string>\?\s+MissionStopped", source);
    }

    /// <summary>与 AxamlResourceConsistencyTests 同款定位：自测试输出目录向上找解决方案根。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroCode.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
