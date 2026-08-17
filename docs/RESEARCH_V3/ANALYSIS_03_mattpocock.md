# ANALYSIS 03 — mattpocock/skills 深度拆解

> **来源**: [mattpocock/skills](https://github.com/mattpocock/skills) (GitHub) + `npx skills@latest add mattpocock/skills`  
> **本地路径**: `D:/minimax/代码/mattpocock-skills` (151 files, 0.6 MB)  
> **许可证**: MIT  
> **作者**: Matt Pocock (TypeScript engineering 布道师, Total TypeScript)  

---

## 1. 定位与差异化

**mattpocock/skills** 是 **"Skills For Real Engineers"** —— 18 个工程实践技能集合，专门为 **TypeScript 工程师 + AI 编程助手** 设计。

**核心目标**: **Anti-vibe-coding**（反"凭感觉编程"），让 AI 写代码时遵循：
- 类型安全 (TypeScript strict mode)
- 测试驱动 (TDD)
- 设计文档 (Design Docs)
- 代码审查 (Code Review)
- 领域建模 (Domain Modeling)
- 改进架构 (Improve Architecture)

**与 Hermes skills 的区别**:
- **Hermes skills**: 通用工作流 (研究、MLOps、社交、媒体)
- **Matt Pocock skills**: **工程纪律** (TDD、code review、design doc、bug diagnosis)

---

## 2. 完整 Skill 列表 (18 个 + 1 个 init)

| # | Skill | 目的 | 移植优先级 |
|---|---|---|---|
| 1 | `ask-matt` | 交互式教学 — 边学边问 | P2 |
| 2 | `code-review` | **代码审查** — 自动 review 自己的 PR | **P0** |
| 3 | `codebase-design` | **代码库设计** — 从空白开始设计 | P1 |
| 4 | `diagnosing-bugs` | **Bug 诊断** — 系统化排错 | **P0** |
| 5 | `domain-modeling` | **领域建模** — 业务对象建模 | P1 |
| 6 | `grill-with-docs` | **文档验证** — 用官方文档挑战实现 | **P0** |
| 7 | `implement` | 实施 — 按 TDD 实现 | P1 |
| 8 | `improve-codebase-architecture` | 改进代码库架构 | P1 |
| 9 | `prototype` | 原型设计 — 快速验证 | P2 |
| 10 | `research` | 研究 — 信息收集 | P2 |
| 11 | `resolving-merge-conflicts` | 解决合并冲突 | P2 |
| 12 | `setup-mattpocock-skills` | **初始化** — 必须先跑 | **P0** |
| 13 | `tdd` | **TDD** — 测试驱动开发 | **P0** |
| 14 | `to-spec` | **写 spec** — 写技术规范 | P1 |
| 15 | `to-tickets` | 转 tickets — 任务分解 | P1 |
| 16 | `triage` | 分类 — 优先级排序 | P2 |
| 17 | `wayfinder` | 寻路 — 在大型代码库定位 | P2 |
| 18 | `wizard` | 向导 — 复杂工作流分步 | P2 |

---

## 3. 核心移植清单 (AeroCode V3.0)

### 3.1 code-review skill → `AeroCode.Skills/Engineering/CodeReview.cs` (P0)

**核心逻辑**：
```markdown
# code-review
1. 读取 PR diff (`git diff`)
2. 分类变更 (新增 / 修改 / 删除)
3. 检查清单：
   - 类型安全 (TypeScript strict, C# nullable)
   - 测试覆盖 (新增代码是否有测试)
   - 文档更新 (是否需要更新 README/CHANGELOG)
   - 性能影响 (是否有 N+1 查询、内存泄漏)
   - 安全问题 (XSS, SQL 注入, 硬编码密钥)
   - 可访问性 (UI 是否符合 a11y)
4. 输出结构化报告
5. 严重程度分级 (Blocker / Major / Minor / Nit)
```

**移植到 AeroCode**：
- Code Review Bot (MCP tool)
- 输入: PR diff / 文件变更
- 输出: 结构化审查报告 (JSON)
- UI: 在 MainWindow 集成 review 报告标签

### 3.2 diagnosing-bugs skill → `AeroCode.Skills/Engineering/DiagnoseBugs.cs` (P0)

**核心逻辑**：
```markdown
# diagnosing-bugs
1. 收集证据：错误信息、堆栈、日志、复现步骤
2. 假设生成：列出可能的根因 (至少 3 个)
3. 二分法验证：通过排除法定位
4. 最小化复现：构建最小测试用例
5. 根因分析：5 Why 分析
6. 修复：实施 + 写回归测试
7. 验证：跑测试 + 复现步骤
```

**移植**：
- 集成到 AI Assistant Tab
- 错误日志 → 自动触发 bug 诊断
- 输出: 结构化诊断报告

### 3.3 grill-with-docs skill → `AeroCode.Skills/Engineering/GrillWithDocs.cs` (P0)

**核心逻辑**：**用官方文档挑战自己的实现**
```markdown
# grill-with-docs
1. 提取实现中的关键 API 调用
2. 自动抓取官方文档
3. 比对实现 vs 文档：
   - API 用法是否正确？
   - 参数顺序是否正确？
   - 是否有 deprecated API？
   - 是否有更新版本？
4. 输出: "我建议你检查以下 API 用法..."
```

**移植**：
- 集成到 AI 助手
- 写代码后自动 grill
- 类似 Hermes 的"grill-me"

### 3.4 tdd skill → `AeroCode.Skills/Engineering/Tdd.cs` (P0)

**核心逻辑**：
```markdown
# tdd
1. Red: 写失败的测试
2. Green: 写最小实现让测试通过
3. Refactor: 重构 (保持测试通过)
4. Repeat: 下一个功能
```

**移植**：
- Code Generation 时强制 TDD 流程
- 输出: 测试 + 实现 + 重构 三段

### 3.5 setup-mattpocock-skills → `AeroCode.Skills/Engineering/SetupSkills.cs` (P0)

**核心逻辑**：初始化 + 注入工程纪律
```markdown
# setup-mattpocock-skills
1. 检测项目类型 (Node/TS/C#/...)
2. 检查现有规范 (eslint, biome, dotnet format)
3. 创建 .agents/ 目录
4. 注入工程纪律到 system prompt:
   - 严格类型
   - 测试覆盖
   - 文档更新
   - 提交前自审
5. 注册所有 skills 到本项目
```

**移植**：
- AeroCode 启动时检测用户工程纪律
- 提供"工程纪律模板"选择

### 3.6 domain-modeling / to-spec / codebase-design (P1)

移植这三个, 用于"任务规划 / 长期记忆"。

---

## 4. Matt Pocock Skills 的"工程哲学"

**核心理念** (从 README + 18 个 SKILL.md 提炼)：

1. **Type safety is non-negotiable** — 不能容忍 `any`、不能容忍未处理的 null
2. **Test first, code second** — TDD 不是可选项
3. **Design before implement** — 写代码前先写 design doc / spec
4. **Verify with documentation** — 写完代码 grill 文档
5. **Self-review before commit** — 提 PR 前自己审查
6. **Diagnose systematically** — Bug 排查要有方法论
7. **Domain language matters** — 业务对象命名要反映业务语言
8. **Architecture is iterative** — 架构持续改进

**这些哲学 = AeroCode V3.0 的"质量门控"核心**。

---

## 5. SKILL.md 格式 (Matt Pocock 版)

```yaml
---
name: skill-name
description: Brief description. (≤ 60 chars)
when_to_use: |
  Trigger conditions. When should the agent use this?
prerequisites: |
  What needs to be in place? (deps, env vars, etc.)
---

# Skill Title
2-3 句 intro

## When to Use
## Prerequisites
## Process
  1. Step one
  2. Step two
  3. Step three
## Checklist
- [ ] Item 1
- [ ] Item 2
## Examples
## Anti-patterns
```

**注意**: Matt Pocock 用 `when_to_use` 字段而不是 `platforms` 字段 — 这是与 Hermes 的差异。
**AeroCode V3.0 应支持两种 frontmatter schema**（兼容 Hermes 和 Matt Pocock）。

---

## 6. 核心移植到 AeroCode V3.0

### 6.1 Engineering Skills Bundle (P0)

**必移植 5 个**:
- `code-review` → Code Review MCP tool
- `diagnosing-bugs` → Bug 诊断能力
- `grill-with-docs` → 文档验证 (类似 Hermes grill-me)
- `tdd` → TDD 流程强制
- `setup-mattpocock-skills` → AeroCode 启动初始化

**选移植 5 个** (P1):
- `codebase-design` / `domain-modeling` / `to-spec` / `to-tickets` / `implement` / `improve-codebase-architecture`

### 6.2 质量门控集成 (P0)

```
AeroCode V3.0 写入代码流程:
  生成代码
    ↓
  trigger: tdd skill (生成前先写测试)
    ↓
  生成代码
    ↓
  trigger: code-review skill (审查自己的代码)
    ↓
  trigger: grill-with-docs skill (用文档验证)
    ↓
  写入文件
    ↓
  trigger: diagnosing-bugs skill (如果失败, 系统化排错)
```

---

## 7. 一句话总结

> Matt Pocock skills 提供 **"工程纪律"** — TDD、code review、design doc、bug diagnosis。我们移植其 5 个核心 skill + 工程哲学，让 AeroCode V3.0 的 AI 助手从"能写代码"升级为"工程化的代码"。
