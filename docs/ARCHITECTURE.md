# AeroCode 架构设计

> 2026-08-14 · Mavis Code × 格林

## 1. 分层架构

```
┌─────────────────────────────────────────────────────────┐
│                   AeroCode.App (UI)                     │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐   │
│  │ MainWindow   │  │ ViewModels   │  │ Converters   │   │
│  │ (AXAML)      │◄─┤ (Observable) │  │ (XAML IValue)│   │
│  └──────────────┘  └──────┬───────┘  └──────────────┘   │
│         │                  │                             │
│  ┌──────▼──────────────────▼─────────┐                   │
│  │ IDialogService / AppDataPaths     │ ← 平台相关       │
│  └──────────────┬────────────────────┘                   │
├─────────────────┼───────────────────────────────────────┤
│                 │       AeroCode.Core (纯 C#)            │
│  ┌──────────────▼──────────────────┐                     │
│  │  Service Interfaces             │ ← DI 边界         │
│  │  ┌────────────────────────────┐ │                     │
│  │  │ INoteService               │ │                     │
│  │  │ INotebookService           │ │                     │
│  │  │ ITagService                │ │                     │
│  │  │ ISearchService             │ │                     │
│  │  └────────────────────────────┘ │                     │
│  │  ┌────────────────────────────┐ │                     │
│  │  │ Models: Note, Notebook,    │ │                     │
│  │  │         Tag, NoteTag       │ │                     │
│  │  └────────────────────────────┘ │                     │
│  │  ┌────────────────────────────┐ │                     │
│  │  │ AeroCodeDbContext (EF Core)│ │                     │
│  │  └────────────────────────────┘ │                     │
│  └─────────────────────────────────┘                     │
└─────────────────────┬───────────────────────────────────┘
                      ▼
              ┌──────────────┐
              │   SQLite DB  │
              │  (local file)│
              └──────────────┘
```

## 2. 关键设计决策

### 2.1 为什么 Core 与 App 分两个 csproj？

| 原因 | 收益 |
|---|---|
| Core 无 UI 依赖 | 单元测试 0 依赖 Avalonia,跑得快 (ms 级) |
| 强制接口隔离 | UI 层不能直接 new Core 类型,必须走 DI |
| 未来加 Linux/Web 端 | 共享 100% Core,只需新写 View |
| 跨端一致 | Android / Windows / Linux 同一份业务逻辑 |

### 2.2 为什么用 `Result<T>` 而不是抛异常？

```csharp
// 传统: 业务错误用异常,性能差且容易漏 catch
try {
  var n = await svc.CreateAsync("", "");
} catch (ArgumentException) { /* 漏一个就崩 */ }

// Result: 编译期强制处理失败分支
var r = await svc.CreateAsync("", "");
if (!r.IsSuccess) { /* 编译器让你处理 */ }
```

- 业务错误 ≠ 系统异常
- 异常只用于真正的 unexpected
- 强制调用方处理,杜绝静默失败

### 2.3 为什么 Core 用 `Microsoft.Data.Sqlite` 而不是 `sqlite-net-pcl`？

| 维度 | Microsoft.Data.Sqlite | sqlite-net-pcl |
|---|---|---|
| EF Core 支持 | ✅ 官方 | ❌ 不支持 |
| FTS5 全文索引 | ✅ 完整 | ⚠️ 部分 |
| 类型映射 | ✅ 强类型 | ⚠️ 弱类型 |
| 性能 | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| 学习曲线 | 中 | 低 |

**选 Microsoft.Data.Sqlite** 因为 EF Core 8/9 在 desktop/mobile 跨端一致,代码更可维护。

### 2.4 为什么 CommunityToolkit.Mvvm 而不是 ReactiveUI？

| 维度 | CommunityToolkit | ReactiveUI |
|---|---|---|
| 微软官方 | ✅ | ❌ 第三方 |
| 学习曲线 | 低 (Source Generator) | 高 (ReactiveX) |
| 编译时生成 | ✅ 零反射 | ❌ 反射 |
| Avalonia 11 兼容 | ✅ | ✅ |

**选 CommunityToolkit** 因为 Roslyn Source Generator 编译期生成 `OnXxxChanged` / `[RelayCommand]`,没有运行时反射开销。

### 2.5 跨平台路径策略

```csharp
// AppDataPaths.cs
RootDirectory = Path.Combine(
  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
  "AeroCode");
// Windows:  C:\Users\<u>\AppData\Local\AeroCode\
// Android:  /data/data/com.AeroCode.app/files/AeroCode/  (Avalonia 注入)
```

**没有硬编码** 任何路径字面量,所有路径走 `AppDataPaths` 服务,跨平台零修改。

## 3. 数据流（创建一个笔记）

```
User click "新笔记" (Ctrl+N)
   │
   ▼
MainWindowViewModel.CreateNoteAsync()
   │ (RelayCommand 由 CommunityToolkit 生成)
   ▼
INoteService.CreateAsync("新建笔记", "", null)
   │
   ▼
NoteService.CreateAsync (Core)
   │  1. 校验 title
   │  2. new Note { ... }
   │  3. _db.Notes.Add(note)
   │  4. _db.SaveChangesAsync()
   ▼
Result<Note>.Ok(note) ──► UI ObservableCollection 自动更新
   │
   ▼
SelectedNote = r.Value  (UI 自动滚动到新笔记)
```

## 4. 错误处理策略

| 层 | 策略 | 例子 |
|---|---|---|
| Service | 捕获所有异常 → Result.Fail(msg, ex) | DB 断开 / 唯一键冲突 |
| ViewModel | 检查 Result.IsSuccess,转译给用户 | 显示 dialog |
| View | 不直接处理,只绑定 | — |
| 全局兜底 | `App.axaml.cs` `LogToFile` | 启动期未捕获异常 |

## 5. 测试策略

### 5.1 当前覆盖（17 用例）

```
tests/AeroCode.Tests/ServiceTests/
├── NoteServiceTests.cs        7 用例 (CRUD + 置顶 + 标签)
├── NotebookServiceTests.cs    4 用例 (嵌套 + 级联删除)
├── TagServiceTests.cs         2 用例 (大小写归一化)
└── SearchServiceTests.cs      3 用例 (命中/空查询/排除已删)
```

### 5.2 In-Memory SQLite 模式

```csharp
var opts = new DbContextOptionsBuilder<AeroCodeDbContext>()
    .UseSqlite("Data Source=:memory:")
    .Options;
var db = new AeroCodeDbContext(opts);
db.Database.OpenConnection();   // in-memory 必须保持连接
db.Database.EnsureCreated();    // 走 OnModelCreating
```

### 5.3 待补（V1.1）

- [ ] ViewModel 集成测试 (Avalonia.Headless)
- [ ] 端到端 UI 测试 (Avalonia 11 + Appium 风格)
- [ ] Android 真机 smoke test
- [ ] 性能压测 (10K 笔记下的搜索 P99)

## 6. 安全与隐私

- 数据完全本地,无云端
- SQLite 文件 OS 级权限保护 (Android 沙箱)
- 不收集任何遥测
- 日志只写本地 `logs/`,可关闭

## 7. 性能预算

| 场景 | 目标 | 实测 |
|---|---|---|
| 启动到首屏 | < 500ms | TBD |
| 1K 笔记列表滚动 | 60 FPS | TBD |
| 搜索 1K 笔记 | < 50ms | TBD |
| 保存 100KB 笔记 | < 20ms | TBD |
| 内存占用 (Windows) | < 80MB | TBD |
| 内存占用 (Android) | < 50MB | TBD |
