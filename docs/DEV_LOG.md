# AeroCode 开发日志

## 2026-08-14 (V1.0)

### 决策
- **产品定位**: 个人生产力/笔记工具 (Grill Me 阶段确认)
- **技术栈**: .NET 9 + Avalonia 11 (跨平台 C# 首选)
- **后端策略**: 纯本地优先,SQLite 文件存储
- **首版范围**: 完整功能版

### 12 阶段流水线执行

| Stage | 产出 |
|---|---|
| 0 预热 | 加载 49 仓库 + Mavis plan |
| 1 Grill Me | 4 个高影响决策 (产品/栈/后端/范围) |
| 2 全局思考 | 跨平台代码共享率 90%+,平台原生体验 |
| 3 检索分析 | 5 轮 web_search: Avalonia 12.0 / Markdown.Avalonia / Microsoft.Data.Sqlite / CommunityToolkit.Mvvm / winget 装法 |
| 4 自质疑 | 反方 3 个: 1-2 周做完整版激进 / 纯本地无后端如何跨设备 / Avalonia 11 Android 稳定性 |
| 5 计划+MoE | 7 任务分配到 7 专家 (架构/数据/UI/测试/文档) |
| 6 执行 | 已写 17 个源文件 + 17 单元测试 |
| 7 中段纠偏 | 持续中 |
| 8 深度自审 | 见末尾 |
| 9 零虚假自测 | 见末尾 |
| 10 双 AI 互审 | 见末尾 |
| 11 交付质检 | 见末尾 |
| 12 记忆归档 | 见末尾 |

### 网络检索摘要

| 检索 | 关键发现 |
|---|---|
| 跨平台栈 2026 | MAUI / Avalonia / Flutter / Tauri 对比 |
| Avalonia 11 Android | 生产可用,被 Unity/JetBrains 信任,12.0 性能 +1867% |
| Markdown.Avalonia | 11.0.0-d1 支持 Avalonia 11.x |
| SQLite .NET 选型 | `Microsoft.Data.Sqlite` + EF Core 最优 |
| winget 装 .NET 9 | `winget install Microsoft.DotNet.SDK.9 --accept-package-agreements` |

### 文件清单 (当前)

```
AeroCode/
├── AeroCode.sln                                ← 解决方案
├── README.md                                   ← 项目主页
├── .gitignore
├── src/
│   ├── AeroCode.Core/                          ← 业务核心 (5 Models, 1 Context, 4 Services, 1 Common)
│   │   ├── Models/   (Note.cs, Notebook.cs, Tag.cs, NoteTag)
│   │   ├── Data/     (AeroCodeDbContext.cs)
│   │   ├── Services/ (4 个 Service 接口 + 4 个实现)
│   │   └── Common/   (Result<T>)
│   └── AeroCode.App/                           ← Avalonia 11 UI
│       ├── Program.cs / App.axaml / App.axaml.cs
│       ├── Services/ (AppDataPaths, DialogService)
│       ├── ViewModels/ (MainWindowViewModel)
│       ├── Views/ (MainWindow.axaml + .cs)
│       └── Converters/ (4 个 IValueConverter)
├── tests/
│   └── AeroCode.Tests/                         ← xUnit
│       └── ServiceTests/  (4 文件, 17 用例)
└── docs/
    ├── ARCHITECTURE.md
    └── DEV_LOG.md
```

### 已知问题

- .NET 9 SDK 装机中 (后台 winget,约 200MB)
- Android 打包待 SDK 装好 + Android SDK 34 安装后验证
- Markdown.Avalonia 在 11.x 下需观察是否有兼容警告 (Windows 直接走 TextBlock 预览,V1 不上 Markdown 渲染管线以避免风险)

### 下一步 (V1.1 候选)

1. 接入 Markdown.Avalonia 真正渲染管线
2. 主题切换 (亮色/暗色)
3. 双向链接 [[wiki]] 解析
4. 导出 Markdown 文件夹
5. Android 真机部署
6. CI/CD GitHub Actions
