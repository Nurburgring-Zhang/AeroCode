# 📒 AeroCode

> **本地优先的跨平台笔记工具 · Windows + Android · 1 套代码**
> 基于 .NET 9 + Avalonia 11 · 2026.08

---

## ✨ 特性

| 能力 | 实现 |
|---|---|
| 📝 Markdown 编辑器 | 双栏（编辑 + 实时预览） |
| 📚 多级笔记本 | 嵌套树形结构 |
| 🏷️ 标签系统 | 多对多,大小写不敏感 |
| 🔍 全文搜索 | 标题 + 内容,实时返回 |
| 📌 置顶 / 🗑️ 软删除 | 可恢复 |
| 💾 自动保存 | Ctrl+S |
| 🌓 暗色主题 | Fluent 主题默认暗色 |
| 🔄 跨平台 | Windows / Android 同源代码 |

## 🏗️ 架构

```
AeroCode/
├── src/
│   ├── AeroCode.Core/        ← 纯 C# 业务核心 (无 UI 依赖)
│   │   ├── Models/            Note / Notebook / Tag
│   │   ├── Data/              EF Core DbContext (SQLite)
│   │   ├── Services/          Note / Notebook / Tag / Search
│   │   └── Common/            Result<T> 类型
│   └── AeroCode.App/         ← Avalonia 11 UI 层
│       ├── Views/             MainWindow.axaml
│       ├── ViewModels/        MainWindowViewModel (CommunityToolkit.Mvvm)
│       ├── Services/          AppDataPaths / DialogService
│       └── Converters/
├── tests/
│   └── AeroCode.Tests/       ← xUnit 单元测试 (17 用例)
└── docs/
    ├── ARCHITECTURE.md
    ├── USAGE.md
    └── DEV_LOG.md
```

### 5 大设计原则（无虚构）

1. **零虚假**：所有方法必须有真实实现,严禁 `TODO` / `NotImplementedException` / mock
2. **强类型**：`Result<T>` 显式表达成功/失败,异常只用于真正的异常
3. **依赖倒置**：所有 service 走接口,UI 层用 DI 容器注入
4. **无硬编码**：路径/颜色/尺寸走 `AppDataPaths` / 资源 / 配置
5. **可测试**：核心 100% 走 xUnit 覆盖,UI 仅含胶水代码

## 🚀 快速开始

```powershell
# 1. 还原依赖
dotnet restore AeroCode.sln

# 2. 编译
dotnet build AeroCode.sln -c Debug

# 3. 跑 Windows 桌面
dotnet run --project src/AeroCode.App

# 4. 跑测试
dotnet test tests/AeroCode.Tests
```

## 📱 Android 打包

```powershell
dotnet build src/AeroCode.App -c Release -f net9.0-android
# 产物: src/AeroCode.App/bin/Release/net9.0-android/AeroCode.apk
```

## 🛠️ 技术栈

| 层 | 选型 | 版本 |
|---|---|---|
| Runtime | .NET | 9.0 |
| UI | Avalonia UI | 11.2.2 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| ORM | EF Core + Sqlite | 9.0 |
| Markdown | Markdown.Avalonia | 11.0.0-d1 |
| DI | Microsoft.Extensions.DI | 9.0 |
| Test | xUnit | 2.9 |

## 📊 进度

| 模块 | 状态 | 备注 |
|---|---|---|
| 数据模型 (Note/Notebook/Tag) | ✅ 完成 | EF Core 映射 |
| Service 层 (4 个) | ✅ 完成 | 全部带单元测试 |
| DbContext + SQLite | ✅ 完成 | in-memory 测试 + 本地文件 |
| Avalonia UI 主窗口 | ✅ 完成 | 三栏布局 + Markdown |
| DI 容器 | ✅ 完成 | Microsoft.Extensions.DI |
| 单元测试 (17 用例) | ✅ 完成 | xUnit + in-memory SQLite |
| Android 打包 | 🔧 待验证 | 需 Android SDK 34+ |
| 主题切换 | ⏳ V1.1 | |
| 双向链接 [[wiki]] | ⏳ V1.1 | |
| 导出 Markdown/JSON | ⏳ V1.1 | |

## 📄 许可证

MIT
