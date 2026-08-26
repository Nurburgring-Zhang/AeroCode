# 📒 AeroCode

> **本地优先的 Markdown 笔记 × 多模型 AI 助手 × Agent Harness —— 一套代码，Windows 与 Android 同源双端**
> .NET 9 + Avalonia 11 · MIT · v1.1.0（PHASE 6 收口 · 全量 914 用例 / 0 失败 / 19 网络门控跳过）

<p>
  <img src="https://img.shields.io/github/v/release/Nurburgring-Zhang/AeroCode?label=Release" alt="Release" />
  <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT" />
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Android-68217a.svg" alt="Platforms" />
  <img src="https://img.shields.io/badge/.NET-9-512BD4.svg" alt=".NET 9" />
</p>

**[发行版下载](https://github.com/Nurburgring-Zhang/AeroCode/releases)** · **[开发日志](docs/DEV_LOG.md)** · **[架构](docs/ARCHITECTURE.md)** · **[Android 构建指南](docs/ANDROID_BUILD.md)**

AeroCode 是一款跑在你自己设备上的本地优先工作台：Markdown 双栏笔记、多 Provider 流式 AI 助手、MOA 多模型编排、权限化的 Agent 工具链，数据全部落在本地 SQLite，API Key 只从环境变量读取。一套 C# / .NET 9 + Avalonia 11 代码同时构建 Windows 桌面与 Android 客户端，发行包内附自包含的 `aerocode-mcp` 演示服务器（MCP stdio）。

---

## ✨ 特性

| 能力 | 实现 |
|---|---|
| 📝 Markdown 编辑器 | 双栏（编辑 + 实时预览），自动保存 |
| 📚 多级笔记本 / 🏷️ 标签 / 🔍 全文搜索 / 📌 置顶 / 🗑️ 软删除 | EF Core + SQLite，全部真实持久化 |
| 🤖 AI 助手 | 多 Provider（OpenAI 兼容 / Anthropic Messages）、流式、深度思考档 |
| 💬 统一对话 | 会话历史持久化（SQLite）、token 用量统计 |
| 🧬 MOA 多模型编排 | Single / Router / Decompose / Ensemble / Pipeline 五策略 + 模型画像 + 真实成本核算（未知不估算） |
| 🧰 工具系统 | 笔记工具箱（12 工具）+ Skills + MCP 外部进程工具 |
| 🔐 工具权限 | 允许 / 拒绝 / 每次询问 + 危险模式探测不降级，持久化 permissions.json |
| 🧠 Memory | 长期记忆存取 + 容量上限治理 |
| 🔎 Code Review / Diagnostics | 内置审查与诊断面板 |
| 🧭 Autonomy 内核（PHASE 5） | 任务状态机 + 工程循环 + 真实网络检索；当前由测试矩阵背书，产品入口接线排期 P8（v1.1.0 发行包未含） |
| 🎓 专家簇与经验学习（PHASE 6） | 专家簇 + MOA 网关集成 + 经验沉淀 / RSI；同上，随 P8 进入发行包 |
| 🔄 跨平台 | Windows（桌面窗口）与 Android（单视图 + Overlay 对话框）共享同一 UI/服务栈 |

## 🏗️ 架构（11 个工程：10 src + 1 tests）

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
│   ├── AeroAgent.Autonomy/       自治内核（PHASE 5）：任务状态机 + 工程循环 + 真实网络检索
│   ├── AeroCode.App/             Avalonia 11 桌面端（WinExe）：MainView + 设置 + 授权 UI
│   └── AeroCode.App.Android/     Android 头项目（net9.0-android + Avalonia.Android）
├── tests/
│   └── AeroCode.Tests/           xUnit 914 用例（895 过 / 19 跳过：需真实网络/LLM/设备）
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
dotnet test AeroCode.sln                 # 914 用例
dotnet run --project src/AeroCode.App    # 运行 Windows 桌面
```

Android 头项目构建 / APK 打包 / 签名（需 android workload + JDK 17 + SDK 35）：

```powershell
# EmbedAssembliesIntoApk=true 必带：Debug 默认"快速部署"不嵌入托管程序集，缺了它 APK 装不上真机
dotnet build src/AeroCode.App.Android -c Debug -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
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

## 🔗 相关项目（定位坐标与致敬）

AeroCode 取「本地优先笔记」与「AI Agent 工作台」两条开源主线的交集。以下项目是本仓库 README 与产品定位的重要参考坐标（仅借鉴公开的定位与表达方式，代码无任何复制）：

**本地优先 / 笔记**

- [AppFlowy](https://github.com/AppFlowy-IO/AppFlowy) —— 开源 Notion 替代、AI 协作工作区，主打「数据不失控」
- [SiYuan 思源笔记](https://github.com/siyuan-note/siyuan) —— 隐私优先的自托管知识工作区，「人与 AI Agent 协同」的定位对本项目启发最直接
- [Logseq](https://github.com/logseq/logseq) —— 隐私优先的开源知识管理平台
- [Joplin](https://github.com/laurent22/joplin) —— 全平台隐私笔记 + 同步，多端一致性的范本

**AI Agent / 编码助手**

- [OpenHands](https://github.com/OpenHands/OpenHands) —— AI 驱动的软件工程 Agent
- [opencode](https://github.com/anomalyco/opencode) —— 开源编码 Agent
- [Cline](https://github.com/cline/cline) —— IDE / 终端中的开源编码 Agent
- [Aider](https://github.com/Aider-AI/aider) —— 终端里的 AI 结对编程
- [goose](https://github.com/aaif-goose/goose) —— 可扩展通用 Agent，桌面 + CLI + API 多形态范本
- [Tabby](https://github.com/TabbyML/tabby) —— 自托管 AI 编码助手

**协议**

- [Model Context Protocol](https://github.com/modelcontextprotocol/modelcontextprotocol) —— 开放工具协议；`AeroCode.Mcp` 基于其官方 [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)（ModelContextProtocol 1.0.0）实现 stdio 服务端与客户端网关

## 📄 许可证

MIT
