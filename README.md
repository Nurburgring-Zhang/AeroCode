# 📒 AeroCode

> **本地优先的笔记 + AI 助手 + Agent Harness · Windows + Android 一套代码**
> .NET 9 + Avalonia 11 · 2026.08 · V3.0（AeroAgent Takeover 完成）

---

## ✨ 特性

| 能力 | 实现 |
|---|---|
| 📝 Markdown 编辑器 | 双栏（编辑 + 实时预览），自动保存 |
| 📚 多级笔记本 / 🏷️ 标签 / 🔍 全文搜索 / 📌 置顶 / 🗑️ 软删除 | EF Core + SQLite，全部真实持久化 |
| 🤖 AI 助手 | 多 Provider（OpenAI 兼容 / Anthropic Messages）、流式、深度思考档 |
| 💬 统一对话 | 会话历史持久化（SQLite）、token 用量统计 |
| 🧬 MOA 多模型编排 | Single / Decompose / Ensemble 三策略 + 模型画像 + 真实成本核算（未知不估算） |
| 🧰 工具系统 | 笔记工具箱（12 工具）+ Skills + MCP 外部进程工具 |
| 🔐 工具权限 | 允许 / 拒绝 / 每次询问 + 危险模式探测不降级，持久化 permissions.json |
| 🧠 Memory | 长期记忆存取 + 容量上限治理 |
| 🔎 Code Review / Diagnostics | 内置审查与诊断面板 |
| 🔄 跨平台 | Windows（桌面窗口）与 Android（单视图 + Overlay 对话框）共享同一 UI/服务栈 |

## 🏗️ 架构（10 个工程）

```
AeroCode/
├── src/
│   ├── AeroCode.Core/            纯 C# 业务核心（无 UI 依赖）：Note/Notebook/Tag + EF Core
│   ├── AeroCode.AI/              Provider 抽象 / 流式 / OTel(Http+Runtime) 埋点
│   ├── AeroCode.Skills/          Skill 定义 + 内置 Analysis 技能（含敏感信息检测正则）
│   ├── AeroCode.Harness/         Agent 运行时护栏：权限 Broker / 预算 / 任务图
│   ├── AeroCode.Mcp/             MCP stdio 测试服务器 + 客户端网关（aerocode-mcp）
│   ├── AeroAgent.Conversation/   会话领域模型 + 编排门面（SQLite 持久化）
│   ├── AeroAgent.Moa/            MOA 编排：Planner / Synthesizer / CostTracker / 画像目录
│   ├── AeroCode.App/             Avalonia 11 桌面端（WinExe）：MainView + 设置 + 授权 UI
│   └── AeroCode.App.Android/     Android 头项目（net9.0-android + Avalonia.Android）
├── tests/
│   └── AeroCode.Tests/           xUnit 550 用例（535 过 / 15 跳过：需真实网络/LLM/设备）
└── docs/                         架构 / 各阶段计划与交付 / DEV_LOG / ANDROID_BUILD
```

生命周期双平台：桌面走 `IClassicDesktopStyleApplicationLifetime` + 模态 Window；
Android 走 `ISingleViewApplicationLifetime` + `OverlayService` 全屏覆盖层，
同一套视图文件（MainView / SettingsView / PermissionDialogView）两端复用。
数据目录：桌面 `%LOCALAPPDATA%/AeroCode`；Android app 私有内部存储（免存储权限）。

## 🚀 源码构建

```powershell
dotnet restore AeroCode.sln
dotnet build AeroCode.sln -c Debug
dotnet test AeroCode.sln                 # 550 用例
dotnet run --project src/AeroCode.App    # 运行 Windows 桌面
```

Android 头项目构建 / APK 打包 / 签名（需 android workload + JDK 17 + SDK 35）：

```powershell
dotnet build src/AeroCode.App.Android -c Debug -t:SignAndroidPackage
# 产物：src/AeroCode.App.Android/bin/Debug/net9.0-android/com.aerocode.app-Signed.apk
```

完整 Android 构建指南（环境准备 / Release 签名 / aapt2 校验 / adb 安装）见
[docs/ANDROID_BUILD.md](docs/ANDROID_BUILD.md)。

## 📦 发行版安装（GitHub Releases）

| 平台 | 资产 | 说明 |
|---|---|---|
| Windows x64 | `AeroCode-win-x64-*.zip` | 自包含（无需安装 .NET），解压即用，内含自包含 `aerocode-mcp.exe` 演示服务器 |
| Android | `AeroCode-android-*.apk` | 当前为 **debug 签名**内测包（minSdk 26 / targetSdk 35），仅 INTERNET 权限；Release 签名流程见 docs/ANDROID_BUILD.md。**如实标注：未经真机实测**，验收以 `aapt2 dump badging` 元数据为准 |

## 🔐 安全与隐私

- API Key 一律从环境变量读取（设置页只存 `ApiKeyEnvVar` 变量名），仓库与产物中不含任何密钥；
- `.gitignore` 覆盖 `*.keystore / *.jks / *.pem / *.p12 / *.pfx` 与本地数据库；
- MCP 外部工具默认「每次询问」，危险模式探测（如 `rm` / `format`）不受任何降级影响；
- 成本核算只认真实用量，未知价格跳过、绝不估算。

## 🛠️ 技术栈

| 层 | 选型 | 版本 |
|---|---|---|
| Runtime | .NET | 9.0 |
| UI | Avalonia UI（含 Avalonia.Android） | 11.2.2 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| ORM | EF Core + SQLite | 9.0 |
| Markdown | Markdig | 0.37.0 |
| MCP | ModelContextProtocol | 1.0.0 |
| DI / Logging | Microsoft.Extensions.* | 9.0 |
| Test | xUnit + Xunit.SkippableFact | 2.9 |

## 📄 许可证

MIT
