# ANALYSIS 06 — Google eng-practices (Code Review Guide) 深度拆解

> **来源**: [google/eng-practices](https://github.com/google/eng-practices)  
> **本地路径**: `D:/minimax/代码/AeroCodeV3_externals/eng-practices` (17 files, 0.1 MB)  
> **许可证**: CC-By 3.0  
> **状态**: Google 官方 code review 实践的 **canonical description**  

---

## 1. 定位与差异化

**Google eng-practices** 是 Google 内部 code review 流程的**官方外化文档**。它不是工具, 而是 **"如何做 code review"** 的**圣经级**方法论。

**核心价值**:
- 2 大文档: **The CL Author's Guide** (developer 怎么写) + **The CL Author's Guide** (reviewer 怎么看)
- Google 数十年来累积的工程纪律
- **CC-By 3.0** 协议, 任何人都可自由使用

**CL** = "Changelist" (Google 内部对"一次提交"或"一个 PR"的称呼)

**与 Matt Pocock code-review skill 的关系**:
- Matt Pocock skill: AI agent 自动 code review
- eng-practices: 人类 code review 的方法论
- **AeroCode V3.0**: 用 eng-practices 的方法论, 实现 Matt Pocock 的 code review skill

---

## 2. 文档结构 (完整清单)

```
eng-practices/
├── review/
│   ├── index.md                 # 入口
│   ├── developer/               # CL 作者指南
│   │   ├── cl-descriptions.md   # 写好 CL 描述
│   │   ├── handling-comments.md # 如何处理 reviewer 评论
│   │   ├── index.md
│   │   └── small-cls.md         # 小 CL 原则
│   ├── reviewer/                # Reviewer 指南
│   │   ├── comments.md          # 如何写评论
│   │   ├── index.md
│   │   ├── looking-for.md       # 重点关注什么
│   │   ├── navigate.md          # 快速浏览 PR
│   │   ├── pushback.md          # 如何 push back
│   │   ├── speed.md             # 速度期望
│   │   └── standard.md          # 标准
│   └── emergencies.md
└── LICENSE                      # CC-By 3.0
```

---

## 3. 核心方法论 (8 维度 Code Review)

来自 `review/index.md`:

> "Code reviews should look at:"
> 
> 1. **Design** — 代码是否设计良好且适合你的系统?
> 2. **Functionality** — 代码是否按作者意图运行? 对用户是否友好?
> 3. **Complexity** — 代码能更简单吗? 未来开发者能理解吗?
> 4. **Tests** — 是否有正确且设计良好的自动化测试?
> 5. **Naming** — 变量/类/方法名是否清晰?
> 6. **Comments** — 注释是否清晰有用?
> 7. **Style** — 是否遵循 style guide?
> 8. **Documentation** — 是否更新了相关文档?

**这 8 维度 = AeroCode V3.0 Code Review Skill 的核心检查清单**。

---

## 4. Small CLs 原则 (最重要的移植)

**核心思想** (来自 `developer/small-cls.md`):
- **Small CLs = Better CLs**
- 一个 CL 应该只做 **一件事**
- 大小应该是: **一次 review session 能完成** (经验值: < 200-400 行代码)

**好处**:
- Review 质量更高 (不疲劳)
- Bug 更少 (减少 merge conflict)
- 回滚更容易
- 协作更顺畅

**如何拆分**:
- 不要把 refactor + feature 混在一起
- 不要包含无关 cleanup
- 不要"顺便修个 bug"

**移植到 AeroCode V3.0**:
- AI 生成代码时强制 Small CL 拆分
- 单次 Patch 限制 (e.g. < 50 行单文件)
- 超出时自动建议拆分

---

## 5. CL Descriptions 规范 (来自 `developer/cl-descriptions.md`)

**好 CL 描述** 包含:
1. **第一行**: 简短摘要 (50 字符内)
2. **正文**: 详细说明
   - 这次 CL 做什么
   - 为什么需要
   - 任何已知限制

**示例 (Google 风格)**:
```
Refactor: Extract user validation into UserService

This change moves user validation logic from the AuthController 
to a dedicated UserService class. The new class:
- Improves testability (can be unit tested in isolation)
- Reduces code duplication across 3 controllers
- Sets up for future user-related features (preferences, profile)

Tests cover all existing validation paths. New tests added for 
the UserService class.
```

**移植到 AeroCode V3.0**:
- AI 生成 commit message 时按 Google 规范
- 第一行 50 字符内
- 包含 "做什么 / 为什么 / 已知限制"

---

## 6. Reviewer 核心原则

### 6.1 The Standard (来自 `reviewer/standard.md`)

**核心**: Reviewer 应该确保:
- 代码**长期**有助于 — 不只是今天能跑
- 一致性 — 与 codebase 整体风格一致
- 减少未来的维护负担

### 6.2 Speed (来自 `reviewer/speed.md`)

- **响应时间**: < 1 个工作日
- 慢 review = 阻塞团队

### 6.3 Pushback (来自 `reviewer/pushback.md`)

- Reviewer 应有礼貌但坚定
- 用证据 + 解释
- 永远不要说 "as I said before" — 假设对方忘了

### 6.4 Looking For (来自 `reviewer/looking-for.md`)

扩展 8 维度 + 边界条件 + 资源泄漏 + race condition + ...

### 6.5 Comments (来自 `reviewer/comments.md`)

**好评论**:
- 标 "Blocking" / "Non-blocking" / "Nit"
- 解释 *为什么* 不只是 *什么*
- 给建议而不是命令

---

## 7. Handling Comments (来自 `developer/handling-comments.md`)

**作者收到 review 后**:
1. 全部回复 (即使 "Done")
2. 不同意时礼貌 push back
3. 不要"我不会改, 因为 reviewer 错了" — 给证据

---

## 8. 核心移植清单 (AeroCode V3.0)

### 8.1 Code Review Skill (融合 Matt Pocock + Google)

**移植到 `AeroCode.Skills/Engineering/CodeReview.cs`**:

```csharp
public class CodeReviewSkill : SkillBase
{
    // 8 维度检查 (来自 Google eng-practices)
    public ReviewReport Review(PatchSet patches)
    {
        var report = new ReviewReport();
        
        // 1. Design
        report.Add(CheckDesign(patches));
        
        // 2. Functionality
        report.Add(CheckFunctionality(patches));
        
        // 3. Complexity (代码行数、圈复杂度、嵌套深度)
        report.Add(CheckComplexity(patches));
        
        // 4. Tests
        report.Add(CheckTests(patches));
        
        // 5. Naming
        report.Add(CheckNaming(patches));
        
        // 6. Comments
        report.Add(CheckComments(patches));
        
        // 7. Style (调用 dotnet format / biome)
        report.Add(CheckStyle(patches));
        
        // 8. Documentation
        report.Add(CheckDocumentation(patches));
        
        return report;
    }
    
    // 严重程度分级
    public enum Severity { Blocker, Major, Minor, Nit }
}
```

### 8.2 Small CL Enforcer (Google Small CLs 原则)

**移植到 `AeroCode.Harness/PatchEnforcer.cs`**:

```csharp
public class PatchEnforcer
{
    public const int MaxLinesPerPatch = 200;  // Google 经验值
    public const int MaxFilesPerPatch = 10;
    
    public ValidationResult Validate(Patch patch)
    {
        if (patch.LineCount > MaxLinesPerPatch)
            return ValidationResult.TooBig;  // 建议拆分
        
        if (patch.FileCount > MaxFilesPerPatch)
            return ValidationResult.TooManyFiles;
        
        return ValidationResult.Ok;
    }
}
```

### 8.3 CL Description Generator (Google 规范)

**移植到 `AeroCode.Harness/CommitMessageGenerator.cs`**:

```csharp
public class CommitMessageGenerator
{
    public string Generate(PatchSet patches, string userIntent)
    {
        // 第一行 50 字符内
        var summary = userIntent.Length > 50 
            ? userIntent.Substring(0, 47) + "..." 
            : userIntent;
        
        // 正文: 做什么 / 为什么 / 已知限制
        var body = $"""
        {summary}
        
        {userIntent}
        
        Changes:
        {string.Join("\n", patches.Select(p => $"- {p.File}: {p.Summary}"))}
        """;
        
        return body;
    }
}
```

### 8.4 Complexity Checker (8 维度之一)

**移植到 `AeroCode.Harness/ComplexityChecker.cs`**:

```csharp
public class ComplexityChecker
{
    public ComplexityReport Check(string filePath, string content)
    {
        return new ComplexityReport
        {
            // 行数
            LineCount = content.Split('\n').Length,
            
            // 圈复杂度 (简化版: 计数 if/for/while/case)
            CyclomaticComplexity = CountKeywords(content, new[] { "if", "for", "while", "case", "&&", "||" }),
            
            // 最大嵌套深度
            MaxNestingDepth = ComputeMaxNesting(content),
            
            // 函数平均行数
            AvgFunctionLength = ComputeAvgFunctionLength(content),
        };
    }
}
```

### 8.5 Naming Checker

**移植到 `AeroCode.Harness/NamingChecker.cs`**:

```csharp
public class NamingChecker
{
    // C# 命名规范
    // - 类名: PascalCase
    // - 方法名: PascalCase
    // - 字段: _camelCase (private) or PascalCase (public)
    // - 常量: UPPER_SNAKE
    // - 接口: IPascalCase
    public List<Issue> Check(string content)
    {
        var issues = new List<Issue>();
        // 解析 AST
        // 检查命名规范
        return issues;
    }
}
```

### 8.6 Documentation Checker

**移植到 `AeroCode.Harness/DocumentationChecker.cs`**:

```csharp
public class DocumentationChecker
{
    // 检查:
    // - 新增 public 类/方法/接口是否有 XML doc
    // - README 是否更新
    // - CHANGELOG 是否更新
    // - breaking change 是否标注
    public DocReport Check(PatchSet patches)
    {
        // ...
    }
}
```

---

## 9. Code Review UI (在 MainWindow 集成)

```
MainWindow
├── 笔记 Tab
├── AI 助手 Tab
└── Code Review Tab (新)
    ├── 输入: PR diff / file changes
    ├── 输出: 结构化 Review Report
    │   ├── 8 维度检查结果
    │   ├── 严重程度 (Blocker/Major/Minor/Nit)
    │   ├── 行号定位
    │   └── 建议
    └── Action:
        ├── Apply 修复
        ├── 忽略
        └── 详细解释
```

---

## 10. 给 V3.0 实施的具体建议

**Stage 5: Quality Gates (整合 Matt Pocock + Google + Hermes grill-me)**:

**Step 1**: 8 维度 Code Review Skill
- CodeReview.cs (P0)
- PatchEnforcer.cs (Small CL) (P0)
- CommitMessageGenerator.cs (P0)

**Step 2**: 复杂度检查
- ComplexityChecker.cs (P1)
- NamingChecker.cs (P1)
- DocumentationChecker.cs (P1)

**Step 3**: UI 集成
- Code Review Tab in MainWindow (P0)

---

## 11. 与 Matt Pocock Skills 的整合

```
AeroCode V3.0 写入代码流程:
  生成代码
    ↓
  trigger: tdd skill (生成前先写测试) - Matt Pocock
    ↓
  trigger: code-review skill (审查自己的代码) - Matt Pocock + Google eng-practices
    ↓
  trigger: grill-with-docs skill (用文档验证) - Matt Pocock + Hermes grill-me
    ↓
  enforce: Small CL (限制单次 patch 大小) - Google eng-practices
    ↓
  generate: commit message (Google CL Description 规范) - Google eng-practices
    ↓
  写入文件
    ↓
  trigger: diagnosing-bugs skill (如果失败, 系统化排错) - Matt Pocock
```

---

## 12. 一句话总结

> Google eng-practices 提供 **"code review 方法论圣经"** —— 8 维度检查、Small CLs 原则、CL Description 规范。我们移植其核心方法论 + Matt Pocock 的 code-review skill 自动化，让 AeroCode V3.0 的 AI 写代码时遵循 Google 工程纪律。
