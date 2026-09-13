# 📒 AeroCode

> **本地优先的 Markdown 笔记 × 多模型 AI 助手 × 本地大模型 × Agent Harness —— 一套代码，Windows 与 Android 同源双端**
> .NET 9 + Avalonia 11 · MIT · 开发主线 r5.3 + 多轮独立评审收口 · 全量 2045 用例 / 0 失败 / 26 门控跳过

<p>
  <img src="https://img.shields.io/github/v/release/Nurburgring-Zhang/AeroCode?label=Release" alt="Release" />
  <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT" />
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Android-68217a.svg" alt="Platforms" />
  <img src="https://img.shields.io/badge/.NET-9-512BD4.svg" alt=".NET 9" />
  <img src="https://img.shields.io/badge/tests-2045%20passed-brightgreen.svg" alt="Tests" />
</p>

**[发行版下载](https://github.com/Nurburgring-Zhang/AeroCode/releases)** · **[开发日志](docs/DEV_LOG.md)** · **[架构](docs/ARCHITECTURE.md)** · **[本地模型规格](docs/LOCAL_LLM_SPEC.md)** · **[附件驱逐规格](docs/ATTACHMENT_EVICTION_SPEC.md)** · **[Android 构建指南](docs/ANDROID_BUILD.md)**

AeroCode 是一款跑在你自己设备上的本地优先工作台：Markdown 双栏笔记、多 Provider 流式 AI 助手、**本地 GGUF 大模型（Ollama，纯 CPU 可跑）**、MOA 多模型编排、权限化的 Agent 工具链，数据全部落在本地 SQLite，API Key 只从环境变量读取。一套 C# / .NET 9 + Avalonia 11 代码同时构建 Windows 桌面与 Android 客户端，发行包内附自包含的 `aerocode-mcp` 演示服务器（MCP stdio）。

> **版本说明（如实）**：GitHub 最新 Release 标签为 `v1.1.0`；当前 `main` 已演进到 r5.3 并完成多轮 builder≠verifier 独立评审收口（本地大模型、消息操作、指令队列、多附件、SOUL、TTS、vision、附件历史驱逐等），见 [docs/DEV_LOG.md](docs/DEV_LOG.md)。

---

## ✨ 特性

### 📝 笔记与知识

| 能力 | 实现 |
|---|---|
| Markdown 编辑器 | 双栏（编辑 + 实时预览），自动保存，Markdig 渲染 |
| 笔记本 / 标签 / 全文搜索 / 置顶 / 软删除 | EF Core + SQLite，全部真实持久化 |
| 笔记导出 | Markdown / JSON 一键导出 |
| 笔记内 AI | 问答 / 分析 / 整理 / 摘要 / 应用到笔记，流式写回 |

### 🤖 AI 对话与本地大模型

| 能力 | 实现 |
|---|---|
| AI 助手 | 多 Provider（OpenAI 兼容 / Anthropic Messages / MiniMax / **Ollama 本地**）、流式、深度思考档、100 办公生产场景库、改写/扩写/续写/大纲/待办/表格化 |
| 统一对话 | 会话历史持久化（SQLite）、token 用量统计、消息**复制 / 编辑 / 重跑 / 分叉运行**（悬停提示，五面板） |
| 指令队列 | 多输入框自动执行 + 排序 + 插队 + 编辑 + 删除 + 折叠（对话 / AI 助手 / 笔记 AI 三处共用 `CommandQueueEngine`） |
| 多附件 | 任意类型、最多 100 个 / 合计 10GB；文本类抽正文按 120K 字符预算**分块注入**，超预算如实标「已引用未注入」；支持 **vision 图像上送**（OpenAI 兼容 content-parts） |
| 附件历史驱逐 | 旧附件注入正文按保留窗口降级为「元信息存根 + 原文」，模型上下文钉死在 ≤2×120K 硬上界，长会话不膨胀（见 [规格](docs/ATTACHMENT_EVICTION_SPEC.md)） |
| **本地大模型** | Ollama GGUF 全链路：运行时检测 / 已装模型列表 / 拉取 / **导入本地 .gguf** / **HuggingFace 拉取** / 参数调节（num_ctx/thread/temperature…热重载）/ 一键设为聊天默认 / 精选模型目录；纯 CPU 推理，免 API Key（见 [规格](docs/LOCAL_LLM_SPEC.md)） |
| SOUL 长系统提示词 | InstructionLoader 全文装载（全局 + 项目级，不截断）→ 真实 system 消息注入；内置 394KB 参考级预设（嵌入资源，一键安装/移除） |
| 语音 / 多模态 | MiniMax TTS（t2a_v2）、生图（image-01）、生视频（video-01 异步任务） |
| 多 AI 对抗代码评审 | 批评者 → 辩护者 → 裁判三轮真实 LLM；单 provider 时如实标注「同模型多角色」 |

### 🧬 MOA 多模型编排

| 能力 | 实现 |
|---|---|
| 六策略 | Single / Router / Decompose / Ensemble / Pipeline / Experts |
| 模型画像 + 成本核算 | 只认真实用量，未知价格跳过、绝不估算 |
| 网关实时徽标 | 专家团页真实探活 MOA 网关 `/health`，绿点=在线 / 琥珀=不可达，绝不伪造连通；X-MOA-Mock 模式如实标注 |
| 高级开关 | Budget / LoopGuard / Curation / Deprecation 四开关入设置 UI（默认关，改动落盘需重启；真热重载暂缓，见 DEV_LOG） |

### 🛡️ Agent Harness（工具链与护栏）

| 能力 | 实现 |
|---|---|
| 工具系统 | 笔记工具箱 + Skills + MCP 外部进程工具（aerocode-mcp） |
| 工具权限 | 允许 / 拒绝 / 每次询问 + 危险模式探测任何档位不降级，持久化 permissions.json |
| 工作区八工具 | read / write / edit / delete / list / search / grep / run_shell + 文件检查点 + Plan 模式（PLAN.md 状态机）+ Git 工作流 |
| 四档权限 | Default / AcceptEdits / Plan / Bypass —— 显式 Deny 恒胜 Ask |
| 子代理与守卫链 | 独立会话 + 权限显式继承 + 并行上限；工作区边界 / 命令分级 / doom-loop / 敏感文件 / 急停哨兵 |
| 扩展生态 | 智能审批 Advisor（不可用零行为差异）+ Hook 引擎（hooks.json）+ 调度器（jobs.json）+ 会话 fork / Steer 插话 / Todo 持久化 / 上下文溢出压缩 |
| Memory | 长期记忆存取（已去除人为字符上限） |
| Mission 自治内核 | 任务状态机 + 工程循环 + 真实网络检索，产品内 Mission 面板（复制轨迹 / 编辑目标推进重跑） |

### 🔎 诊断与评测

| 能力 | 实现 |
|---|---|
| Code Review / Diagnostics | 内置审查与诊断面板 |
| **AeroCode.Eval** | 独立评测工程（只读复用 src，禁改源码）：Baseline/Compare Runner + 三项指标（CheckpointPassRate / MultiTurnHallucination / UnitCostCompletion），gateway 模式经 `MoaGatewayClient` 真实采集，报告见 `eval/reports/` |
| 跨平台 | Windows（桌面窗口）与 Android（单视图 + Overlay 对话框）共享同一 UI/服务栈 |

## 🏗️ 架构（12 个工程：10 src + 1 eval + 1 tests）

```
AeroCode/
├── src/
│   ├── AeroCode.Core/            纯 C# 业务核心（无 UI 依赖）：Note/Notebook/Tag + EF Core
│   ├── AeroCode.AI/              Provider 抽象 / 流式 / OTel 埋点 / LocalModels（Ollama 原生+OpenAI 客户端）
│   ├── AeroCode.Skills/          Skill 定义 + 内置技能（含敏感信息检测正则）
│   ├── AeroCode.Harness/         Agent 运行时护栏：权限 Broker / 预算 / 任务图 / 守卫链
│   ├── AeroCode.Mcp/             MCP stdio 测试服务器 + 客户端网关（aerocode-mcp）
│   ├── AeroAgent.Conversation/   会话领域模型 + 编排门面 + HistoryMapper（附件驱逐）+ SQLite 持久化
│   ├── AeroAgent.Moa/            MOA 编排：六策略 / Planner / Synthesizer / CostTracker / 画像目录 / 网关客户端
│   ├── AeroAgent.Autonomy/       自治内核：任务状态机 + 工程循环 + 真实网络检索
│   ├── AeroCode.App/             Avalonia 11 桌面端（WinExe）：MainView + 设置 + 本地模型面板 + 授权 UI
│   └── AeroCode.App.Android/     Android 头项目（net9.0-android + Avalonia.Android）
├── eval/
│   └── AeroCode.Eval/            评测工程（Exe，只读复用 AeroAgent.Moa）：Baseline/Compare + 三指标 Runner
├── tests/
│   └── AeroCode.Tests/           xUnit 2045 用例（0 失败 / 26 门控跳过：需真实网络/LLM/Ollama/设备）
└── docs/                         架构 / 各阶段计划与交付 / DEV_LOG / LOCAL_LLM_SPEC / ATTACHMENT_EVICTION_SPEC / ANDROID_BUILD
```

生命周期双平台：桌面走 `IClassicDesktopStyleApplicationLifetime` + 模态 Window；
Android 走 `ISingleViewApplicationLifetime` + `OverlayService` 全屏覆盖层，
同一套视图文件（MainView / SettingsView / PermissionDialogView）两端复用。
数据目录：桌面 `%LOCALAPPDATA%/AeroCode`；Android app 私有内部存储（免存储权限）。
对话库含幂等启动迁移（`EnsureSchemaAsync`，PRAGMA 检测缺列自动 `ALTER TABLE` 补齐）。

## 🚀 源码构建

```powershell
dotnet restore AeroCode.sln
dotnet build AeroCode.sln -c Debug
dotnet test AeroCode.sln                 # 2045 用例（0 失败 / 26 门控跳过）
dotnet run --project src/AeroCode.App    # 运行 Windows 桌面
```

发布自包含桌面包（免装 .NET，剔 pdb）：

```powershell
dotnet publish src/AeroCode.App -c Release -r win-x64 --self-contained true -o deliverables/r5/win-x64
```

Android 头项目构建 / APK 打包 / 签名（需 android workload + JDK 17 + SDK 35 平台）：

```powershell
# EmbedAssembliesIntoApk=true 必带：Debug 默认"快速部署"不嵌入托管程序集，缺了它 APK 装不上真机
dotnet build src/AeroCode.App.Android -c Debug -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
# 产物：src/AeroCode.App.Android/bin/Debug/net9.0-android/com.aerocode.app-Signed.apk
```

> **如实标注**：本机构建环境当前缺 Android SDK 平台目录（报 XA5300），Android 头需先装 SDK 平台；无设备/模拟器冒烟。完整 Android 构建指南见 [docs/ANDROID_BUILD.md](docs/ANDROID_BUILD.md)。

## 📦 发行版安装（GitHub Releases）

| 平台 | 资产 | 说明 |
|---|---|---|
| Windows x64 | `AeroCode-*-win-x64*.zip` | 自包含（无需安装 .NET），解压即用，内含自包含 `aerocode-mcp.exe` 演示服务器与 `DELIVERY_MANIFEST.txt` 交付清单；已剔 pdb |
| Android | `AeroCode-android-*.apk` | 当前为 **debug 签名**内测包（minSdk 26 / targetSdk 35），仅 INTERNET 权限；Release 签名流程见 docs/ANDROID_BUILD.md。**如实标注：未经真机实测**，验收以 `aapt2 dump badging` 元数据为准 |

> 注：Releases 最新标签为 `v1.1.0`；r5.3 交付包（含本地大模型与评审收口）的打包描述见仓库 `deliverables/` 与 DEV_LOG，`main` 源码已推送至 GitHub。

## 🧠 本地大模型快速上手

1. 安装并启动 [Ollama](https://ollama.com)（默认 `127.0.0.1:11434`）。
2. AeroCode 设置 → 「本地模型」Tab：点「检测」列出运行时与已装模型。
3. 拉取模型（精选目录一键拉取，或手动 `拉取` 输入模型名；支持 `hf.co/<repo>:<quant>`），或「导入本地 .gguf」。
4. 「设为聊天默认」把选中模型写入 ollama provider，对话即走本地推理。
5. 「参数调节」可改 num_ctx / num_thread / temperature / top_p / top_k / repeat_penalty / num_predict，应用后热重载免重启。

> 本机无 NVIDIA GPU 时走纯 CPU 推理（Intel 集显等）；CUDA / FlashAttention / vLLM 等 GPU 加速路径如实标注为环境受限，见 [docs/LOCAL_LLM_SPEC.md](docs/LOCAL_LLM_SPEC.md)。

## 🔐 安全与隐私

- API Key 一律从环境变量读取（设置页只存 `ApiKeyEnvVar` 变量名），仓库与产物中不含任何密钥；
- 本地大模型（Ollama）免 API Key，数据不出本机；
- `.gitignore` 覆盖 `*.keystore / *.jks / *.pem / *.p12 / *.pfx` 与本地数据库；
- MCP 外部工具默认「每次询问」，危险模式探测（如 `rm` / `format` / `git push --mirror`）不受任何降级影响；
- 附件注入 / 历史驱逐只认真实数据，超预算如实标注「已引用未注入」，绝不伪造已读全文；
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
| 本地推理 | Ollama（原生 /api + OpenAI 兼容 /v1） | 0.34+ |
| DI / Logging | Microsoft.Extensions.* | 9.0 |
| Test | xUnit + Xunit.SkippableFact | 2.9.2 / 1.4.13 |

## 🧪 质量与验证（如实）

- 全量测试套件：**2045 通过 / 0 失败 / 26 门控跳过**（跳过 = 需真实网络 / LLM / Ollama / 设备的诚实跳过，非掩盖）。
- 本地模型全链路 E2E：`facade → OllamaProvider → 真实本地模型推理 → 助手回复落库`（Ollama 可达时真实跑，否则诚实跳过）。
- 多轮 **builder≠verifier 独立评审**闭环（命令队列 / 附件分块 / 本地模型 / 网关徽标 / 四开关 / 附件驱逐），逐项修复 HIGH/MED/LOW 并复验。
- 生产部署冒烟：设置全部 Tab + 本地模型面板原子粒度全检，窗口级 UIA 实证。
- 设计级规格：[附件历史驱逐](docs/ATTACHMENT_EVICTION_SPEC.md)、[本地大模型](docs/LOCAL_LLM_SPEC.md)。

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
- [Ollama](https://github.com/ollama/ollama) —— 本地大模型运行时；AeroCode 本地模型子系统基于其原生 /api 与 OpenAI 兼容端点

**协议**

- [Model Context Protocol](https://github.com/modelcontextprotocol/modelcontextprotocol) —— 开放工具协议；`AeroCode.Mcp` 基于其官方 [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)（ModelContextProtocol 1.0.0）实现 stdio 服务端与客户端网关

## 📄 许可证

MIT
