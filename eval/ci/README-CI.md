# eval/ci — C3 评测 CI 阻断 gate

`run-eval-gate.ps1` 把「构建 → 测试 → 基线 → 对照 → 对比」串成一条可接入 CI 的 gate：
任一步失败即以该步退出码退出（非 0 = 阻断合并/发布）。仓库当前无 `.github/` 目录，
本目录只交付脚本与说明，不含任何平台特定 workflow 文件。

## 接入点

任意 CI/本地流程在仓库根执行：

```powershell
powershell -ExecutionPolicy Bypass -File eval/ci/run-eval-gate.ps1
# 本机显式 SDK 路径：
powershell -ExecutionPolicy Bypass -File eval/ci/run-eval-gate.ps1 `
    -DotNet "C:/Users/<user>/.dotnet/dotnet.exe"
# 只跑评测链（不重复 build/test）：
powershell -ExecutionPolicy Bypass -File eval/ci/run-eval-gate.ps1 -SkipBuild -SkipTests
```

### 参数（路径全部参数化，默认相对仓库根）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-RepoRoot` | 脚本上两级目录 | 仓库根（含 AeroCode.sln） |
| `-DotNet` | `dotnet` | dotnet 可执行文件 |
| `-EvalProject` | `eval/AeroCode.Eval/AeroCode.Eval.csproj` | 评测工程 |
| `-TestProject` | `tests/AeroCode.Tests/AeroCode.Tests.csproj` | 测试工程 |
| `-BeforeReport` | `eval/reports/baseline.md` | 基线报告（只读复用，绝不覆盖） |
| `-AfterReport` | `eval/reports/after.md` | 对照报告（每次 gate 重新生成） |
| `-SkipBuild` / `-SkipTests` | 关 | 跳过对应步骤（快速评测链） |

## 步骤与退出码语义

| 步骤 | 行为 | 失败退出码 |
|---|---|---|
| build | `dotnet build` 评测工程 | 透传 dotnet 退出码（非 0） |
| test | `dotnet test` 测试工程 | 透传 dotnet 退出码（非 0） |
| baseline | `eval/reports/baseline.md` 已存在 → 只读复用并校验三指标段齐备；缺失 → `eval baseline` 生成（首次落基线） | 缺指标段 = 6；生成失败透传 eval 退出码 |
| after | `eval after --out eval/reports/after.md`（复用 baseline 采集管线；无网关 → dry-run PENDING 口径） | 透传 eval 退出码 |
| compare | `eval compare --before <before> --after <after>` | 见下表 |

compare 退出码（CI 阻断语义）：

| 退出码 | 含义 |
|---|---|
| 0 | 通过：无回退（PENDING 口径不参与判定——dry-run 环境两份都 PENDING → 通过） |
| 5 | **阻断**：任一已实测指标 after 比 before 回退（多轮幻觉率↑ / checkpoint 通过率↓ / 单位成本↑，均按指标方向判定） |
| 6 | **阻断（fail-closed）**：报告缺失/指标段缺失/「值」行缺失/值不可解析——解析失败绝不默认通过 |
| 2 | 用法错误（缺 `--before`/`--after` 等） |
| 3 | 输出路径 fail-closed 拒绝（越出 eval/reports/） |
| 4 | fixture 校验失败 |
| 1 | 其他运行错误 |

脚本本身：任一步 `$LASTEXITCODE -ne 0` → 立即 `exit` 该码；全部通过 → `exit 0`。

## 环境变量（凭据只从环境变量读，脚本内不内嵌任何凭据）

| 变量 | 说明 |
|---|---|
| `AEROCODE_EVAL_GATEWAY_KEY` | 网关 API key。未设置 → 评测 runner 进入 dry-run（指标 PENDING，报告结构完整）。值不落盘、不进日志/报告（SensitiveScrubber 统一脱敏） |
| `AEROCODE_EVAL_GATEWAY_URL` | 网关 base URL；未设置且 KEY 存在时回退 `http://127.0.0.1:8910` |

CI 配置凭据示例（以 GitHub Actions 为例，仅示意——本仓库现无 `.github/`）：

```yaml
env:
  AEROCODE_EVAL_GATEWAY_KEY: ${{ secrets.AEROCODE_EVAL_GATEWAY_KEY }}
  AEROCODE_EVAL_GATEWAY_URL: ${{ vars.AEROCODE_EVAL_GATEWAY_URL }}
```

## 基线维护约定

- `eval/reports/baseline.md` 是**只读对照输入**：由首次 gate（或人工跑 `eval baseline`）落一次，
  之后 gate 只读复用；更新基线是显式的人工动作（评审 diff 后重新生成），gate 永不覆盖它。
- `eval/reports/after.md` 每次 gate 重新生成，属一次性产物。
- 无网关（dry-run）环境下两份报告均为 PENDING → compare 通过：gate 校验的是管线健康与
  报告结构，真实指标回归阻断在配置了网关凭据的 CI 环境中生效。
- 所有报告/日志/异常出口统一经 `SensitiveScrubber` 脱敏（sk-* 形态密钥、Bearer 令牌、
  `AEROCODE_EVAL_*` / `MOA_GATEWAY_KEY` 凭据值 → `[REDACTED]`）。
