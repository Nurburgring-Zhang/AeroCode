#Requires -Version 5.1
<#
.SYNOPSIS
    moa-gateway-pro v3.1.1 一键部署/管理脚本（AeroCode MOA 网关集成，PHASE 6 T2）。

.DESCRIPTION
    子命令:
      install  创建独立 venv + pip 安装网关 whl + 安装 config.yaml（幂等）
      start    后台启动网关（uvicorn 单进程），轮询 /health 直到就绪，PID 落盘 gateway.pid
      stop     按 gateway.pid 停止网关（递归杀整棵进程树），并核实端口已释放
      status   查看进程与 /health 实时状态
      info     显示启动参数与环境变量说明

    约定（均经本机真实验证）:
      - venv 位置: <脚本目录>\.venv（不污染系统 Python）
      - 日志:      <脚本目录>\gateway.out.log / gateway.err.log
      - config.yaml: whl 不捆绑该文件，而网关从 <site-packages>\config.yaml 读取
        （ROOT_DIR/config.yaml，vendor 契约）。缺它时 moa.presets 为空，
        POST /v1/moa/execute 返回 500 IndexError（实测）。install 会把官方
        config.yaml（-ConfigPath）复制到位；找不到时生成最小 mock 可用配置。
      - 管理员密码: -AdminPassword 或环境变量 MOA_ADMIN_PASSWORD；
        两者皆无时自动生成并写入 <脚本目录>\.admin_password（start 时读取）。
        注意: admin 用户仅首次启动播种（数据位于 <site-packages>\data\config.db），
        更换密码需先删除该库文件。

.EXAMPLE
    .\setup_gateway.ps1 install
    .\setup_gateway.ps1 start -Port 8910
    .\setup_gateway.ps1 status
    .\setup_gateway.ps1 stop
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install', 'start', 'stop', 'status', 'info')]
    [string]$Command = 'info',

    [int]$Port = 8910,

    # 注意: 不能用 $Host（PowerShell 自动变量），故名 BindHost
    [string]$BindHost = '127.0.0.1',

    # whl 路径默认留空，install 阶段按以下顺序解析：$env:MOA_WHL_PATH > 参数 > 自动发现。
    [string]$WhlPath = $env:MOA_WHL_PATH,

    # 官方 config.yaml（models 端点 + moa.presets）。找不到时生成最小 mock 配置。
    [string]$ConfigPath = $env:MOA_CONFIG_PATH,

    # 基础 Python 解释器。默认留空，install 阶段自动在 PATH 中查找 python/python3/py。
    [string]$BasePython = $env:MOA_BASE_PYTHON,

    [string]$AdminPassword = $env:MOA_ADMIN_PASSWORD
)

$ErrorActionPreference = 'Stop'
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvDir     = Join-Path $ScriptDir '.venv'
$VenvPython  = Join-Path $VenvDir 'Scripts\python.exe'
$PidFile     = Join-Path $ScriptDir 'gateway.pid'
$OutLog      = Join-Path $ScriptDir 'gateway.out.log'
$ErrLog      = Join-Path $ScriptDir 'gateway.err.log'
$PasswordFile = Join-Path $ScriptDir '.admin_password'

function Write-Step([string]$msg) { Write-Host "[setup_gateway] $msg" }

function Test-VenvInstalled {
    if (-not (Test-Path $VenvPython)) { return $false }
    & $VenvPython -c "import moa_gateway" 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Find-ExecutableInPath([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Resolve-BasePython {
    if (-not [string]::IsNullOrWhiteSpace($BasePython)) {
        return $BasePython.Trim()
    }
    foreach ($candidate in @('python', 'python3', 'py')) {
        $found = Find-ExecutableInPath $candidate
        if ($found) {
            Write-Step "自动发现基础 Python: $found"
            return $found
        }
    }
    throw "未找到基础 Python 解释器。请安装 Python 3.10+ 并用 -BasePython 或 `$env:MOA_BASE_PYTHON 指定。"
}

function Resolve-WhlPath {
    if (-not [string]::IsNullOrWhiteSpace($WhlPath)) {
        return $WhlPath.Trim()
    }
    $searchRoots = @(
        $ScriptDir,
        (Join-Path $ScriptDir 'dist'),
        (Join-Path $ScriptDir 'packages'),
        (Join-Path (Split-Path -Parent $ScriptDir) 'dist')
    )
    foreach ($root in $searchRoots) {
        if (-not (Test-Path $root)) { continue }
        $candidates = Get-ChildItem -Path $root -Filter 'moa_gateway_pro-*.whl' -File | Sort-Object LastWriteTime -Descending
        if ($candidates) {
            Write-Step "自动发现 whl: $($candidates[0].FullName)"
            return $candidates[0].FullName
        }
    }
    throw "未找到 moa_gateway_pro-*.whl。请用 -WhlPath 或 `$env:MOA_WHL_PATH 指定。"
}

function Resolve-ConfigPath {
    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        return $ConfigPath.Trim()
    }
    $candidate = Join-Path $ScriptDir 'config.yaml'
    if (Test-Path $candidate) { return $candidate }
    return $null
}

function New-CryptographicPassword {
    # 生成 ≥8 位、含字母+数字的密码（网关 bootstrap 强度校验）。
    # 优先使用加密强随机数生成器；受限语言模式（CLM）下 .NET 类型不可用，
    # 如实回退到 Get-Random 并提示 [DEGRADED]——功能不断、诚实留痕。
    $alphabet = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789'
    try {
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $chars = @()
        $bytes = New-Object byte[] 1
        for ($i = 0; $i -lt 24; $i++) {
            do { $rng.GetBytes($bytes) } while ($bytes[0] -ge (256 - (256 % $alphabet.Length)))
            $chars += $alphabet[$bytes[0] % $alphabet.Length]
        }
        $rng.Dispose()
        return "MoaGw-" + ($chars -join '')
    } catch {
        Write-Step "[DEGRADED] 加密随机数不可用（受限语言模式？），回退到 Get-Random 生成密码: $_"
        $rand = -join (1..24 | ForEach-Object { $alphabet[(Get-Random -Maximum $alphabet.Length)] })
        return "MoaGw-$rand"
    }
}

function Protect-PasswordFile([string]$path) {
    # 将密码文件 ACL 限制为当前用户只读，降低明文落盘风险。
    # 全部 .NET/ACL 调用在受限语言模式下可能失败：如实 [DEGRADED]，不阻断启动。
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $acl = Get-Acl -Path $path
        $acl.SetAccessRuleProtection($true, $false)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity, 'Read', 'Allow')
        $acl.SetAccessRule($rule)
        Set-Acl -Path $path -AclObject $acl
    } catch {
        Write-Step "[DEGRADED] 无法收紧 $path 的访问权限: $_"
    }
}

function Get-GatewayRootDir {
    # vendor 契约: ROOT_DIR = 包目录的父目录（venv 安装即 site-packages）
    $out = & $VenvPython -c "import moa_gateway, pathlib; print(pathlib.Path(moa_gateway.__file__).resolve().parent.parent)"
    if ($LASTEXITCODE -ne 0) { throw "无法定位 moa_gateway 安装目录 (ROOT_DIR)" }
    return $out.Trim()
}

# 最小可运行配置：无任何真实 key 时全部端点由 MockProvider 支撑（mock.mode=explicit，
# 响应带 X-MOA-Mock 头 + body.mock=true）。字段对齐 vendor schema（config.py）。
$MinimalConfigYaml = @'
# AeroCode minimal moa-gateway config (generated by setup_gateway.ps1)
# 无真实模型 key 时所有端点为显式 mock（D6）。补充真实 key 请改用官方 config.yaml。
auth:
  gateway_api_keys: []
  admin_username: admin
  admin_password: ''
models:
- id: qwen3-mock
  provider: openai
  model: qwen3-235b-a22b
  tier: standard
  api_base: https://dashscope.aliyuncs.com/compatible-mode/v1
  api_key_env: QWEN_API_KEY
  enabled: true
- id: glm4-mock
  provider: zhipu
  model: glm-4-flash
  tier: lite
  api_base: https://open.bigmodel.cn/api/paas/v4
  api_key_env: ZHIPU_API_KEY
  enabled: true
- id: deepseek-mock
  provider: deepseek
  model: deepseek-chat
  tier: standard
  api_base: https://api.deepseek.com/v1
  api_key_env: DEEPSEEK_API_KEY
  enabled: true
- id: kimi-mock
  provider: moonshot
  model: kimi-k2-0711-preview
  tier: premium
  api_base: https://api.moonshot.cn/v1
  api_key_env: MOONSHOT_API_KEY
  enabled: true
mock:
  mode: explicit
moa:
  enabled: true
  default_preset: balanced
  presets:
    fast:
      enabled: true
      strategy: single
      reference_count: 1
      critic_rounds: 0
      tier: lite
      description: single lite model, fastest
    balanced:
      enabled: true
      strategy: parallel
      reference_count: 3
      aggregator_tier: premium
      critic_rounds: 1
      description: parallel references + premium aggregator + 1 critic round
    quality:
      enabled: true
      strategy: parallel
      reference_count: 4
      aggregator_tier: flagship
      critic_rounds: 2
      description: parallel references + flagship aggregator + 2 critic rounds
'@

function Install-GatewayConfig {
    $resolvedConfig = Resolve-ConfigPath
    $rootDir = Get-GatewayRootDir
    $target = Join-Path $rootDir 'config.yaml'
    if ($resolvedConfig -and (Test-Path $resolvedConfig)) {
        Copy-Item -Path $resolvedConfig -Destination $target -Force
        Write-Step "config.yaml 已安装（官方）: $target"
    } else {
        Set-Content -Path $target -Value $MinimalConfigYaml -Encoding utf8
        Write-Step "未找到官方 config.yaml (-ConfigPath/`$env:MOA_CONFIG_PATH)，已生成最小 mock 配置: $target"
    }
}

function Invoke-Install {
    $resolvedWhl = Resolve-WhlPath
    $resolvedBase = Resolve-BasePython

    if (-not (Test-Path $resolvedWhl)) {
        throw "whl 不存在: $resolvedWhl（用 -WhlPath 或 `$env:MOA_WHL_PATH 指定 moa_gateway_pro-3.1.1-py3-none-any.whl）"
    }
    if (-not (Test-Path $resolvedBase)) {
        throw "基础 Python 不存在: $resolvedBase（用 -BasePython 或 `$env:MOA_BASE_PYTHON 指定 Python 3.10+ 解释器）"
    }

    if (-not (Test-Path $VenvPython)) {
        Write-Step "创建独立 venv: $VenvDir"
        & $resolvedBase -m venv $VenvDir
        if ($LASTEXITCODE -ne 0) { throw "venv 创建失败 (exit $LASTEXITCODE)" }
    } else {
        Write-Step "venv 已存在: $VenvDir"
    }

    Write-Step "pip 安装网关 whl（含 fastapi/uvicorn 等全部依赖）..."
    & $VenvPython -m pip install --disable-pip-version-check --quiet $resolvedWhl
    if ($LASTEXITCODE -ne 0) { throw "pip install 失败 (exit $LASTEXITCODE)" }

    Write-Step "验证 import moa_gateway ..."
    & $VenvPython -c "import moa_gateway; print('moa_gateway import OK:', moa_gateway.__file__)"
    if ($LASTEXITCODE -ne 0) { throw "import 验证失败——安装不完整" }

    Install-GatewayConfig

    Write-Step "安装完成。启动: .\setup_gateway.ps1 start -Port $Port"
}

function Get-OrInitAdminPassword {
    if (-not [string]::IsNullOrWhiteSpace($AdminPassword)) { return $AdminPassword }
    if (Test-Path $PasswordFile) {
        $saved = (Get-Content $PasswordFile -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($saved)) { return $saved }
    }
    $generated = New-CryptographicPassword
    Set-Content -Path $PasswordFile -Value $generated -Encoding ascii -NoNewline
    Protect-PasswordFile $PasswordFile
    Write-Step "已生成管理员密码并写入 $PasswordFile（登录用户名 admin；文件已限制为当前用户只读）"
    return $generated
}

function Stop-ProcessTree([int]$ParentId) {
    $children = Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $ParentId }
    foreach ($child in $children) { Stop-ProcessTree ([int]$child.ProcessId) }
    Stop-Process -Id $ParentId -Force -ErrorAction SilentlyContinue
}

function Get-HealthJson([string]$url) {
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
        return ($resp.Content | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Invoke-Start {
    if (-not (Test-VenvInstalled)) {
        throw "网关未安装——先运行: .\setup_gateway.ps1 install"
    }

    # config.yaml 缺失时 execute 必 500（实测）——启动前如实拦截
    $rootDir = Get-GatewayRootDir
    $configFile = Join-Path $rootDir 'config.yaml'
    if (-not (Test-Path $configFile)) {
        throw "缺少 $configFile —— 先运行: .\setup_gateway.ps1 install（无 presets 时 execute 会 500）"
    }

    if (Test-Path $PidFile) {
        $oldPid = [int](Get-Content $PidFile -Raw).Trim()
        if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
            throw "网关已在运行 (PID $oldPid)。如需重启: .\setup_gateway.ps1 stop 后再 start"
        }
        Remove-Item $PidFile -Force
    }

    $password = Get-OrInitAdminPassword
    # 子进程继承当前会话环境变量（官方 start.py 约定始终携带 MOA_ADMIN_PASSWORD）
    $env:MOA_ADMIN_PASSWORD = $password

    Write-Step "启动网关: $VenvPython -m uvicorn moa_gateway.server:app --host $BindHost --port $Port"
    $proc = Start-Process -FilePath $VenvPython `
        -ArgumentList @('-m', 'uvicorn', 'moa_gateway.server:app', '--host', $BindHost, '--port', "$Port") `
        -WorkingDirectory $ScriptDir `
        -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog `
        -NoNewWindow -PassThru
    Set-Content -Path $PidFile -Value "$($proc.Id)" -Encoding ascii

    Write-Step "等待 /health 就绪（最长 90s）..."
    $healthUrl = "http://${BindHost}:${Port}/health"
    $deadline = (Get-Date).AddSeconds(90)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $tail = if (Test-Path $ErrLog) { Get-Content $ErrLog -Tail 20 } else { @() }
            throw "网关进程已退出 (exit $($proc.ExitCode))。日志尾部:`n$($tail -join "`n")"
        }
        $health = Get-HealthJson $healthUrl
        if ($null -ne $health -and $health.status -eq 'ok') { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        Stop-ProcessTree $proc.Id
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
        throw "90s 内 /health 未就绪——查看 $ErrLog"
    }

    Write-Step "网关已就绪 (PID $($proc.Id)) @ $healthUrl"
    Write-Step "登录: POST http://${BindHost}:${Port}/api/auth/login {username=admin,password=<MOA_ADMIN_PASSWORD>}"
    Write-Step "执行: POST http://${BindHost}:${Port}/v1/moa/execute  Authorization: Bearer <token 或网关 API Key>"
}

function Invoke-Stop {
    if (-not (Test-Path $PidFile)) {
        Write-Step "gateway.pid 不存在——没有由本脚本启动的网关实例"
        return
    }
    $pidValue = [int](Get-Content $PidFile -Raw).Trim()
    if (Get-Process -Id $pidValue -ErrorAction SilentlyContinue) {
        Write-Step "停止进程树 (root PID $pidValue)..."
        Stop-ProcessTree $pidValue
    } else {
        Write-Step "PID $pidValue 已不存在"
    }
    Remove-Item $PidFile -Force

    # 核实端口确实释放（诚实性：不停干净不算完）
    Start-Sleep -Milliseconds 500
    $still = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($still) {
        throw "端口 $Port 仍被监听——可能有未登记的子进程，需人工排查 (netstat -ano | findstr :$Port)"
    }
    Write-Step "已停止，端口 $Port 已释放"
}

function Invoke-Status {
    if (Test-Path $PidFile) {
        $pidValue = [int](Get-Content $PidFile -Raw).Trim()
        $procAlive = [bool](Get-Process -Id $pidValue -ErrorAction SilentlyContinue)
        Write-Step "PID 文件: $pidValue (进程存活: $procAlive)"
    } else {
        Write-Step "PID 文件: 无（本脚本未启动过网关）"
    }
    $health = Get-HealthJson "http://${BindHost}:${Port}/health"
    if ($null -ne $health) {
        Write-Step "/health: status=$($health.status) version=$($health.version) mock_mode=$($health.mock_mode) mock_endpoints=$($health.mock_endpoints_count) real_endpoints=$($health.real_endpoints_count)"
    } else {
        Write-Step "/health: 不可达 (http://${BindHost}:${Port}/health)"
    }
}

function Invoke-Info {
    @'
================ moa-gateway-pro v3.1.1 启动参数说明（AeroCode 集成） ================
一键流程:
  1) .\setup_gateway.ps1 install            # 建 venv + pip 安装 whl + 安装 config.yaml
  2) .\setup_gateway.ps1 start -Port 8910   # 后台启动并等待 /health 就绪
  3) .\setup_gateway.ps1 status             # 查看进程与健康
  4) .\setup_gateway.ps1 stop               # 停止（递归杀进程树）

启动命令（start 内部实际执行）:
  <venv>\Scripts\python.exe -m uvicorn moa_gateway.server:app --host 127.0.0.1 --port 8910

config.yaml（必读）:
  whl 不捆绑 config.yaml，而网关固定从 <site-packages>\config.yaml 读取。
  缺它时 moa.presets 为空，POST /v1/moa/execute 返回 500（IndexError，实测）。
  install 会自动安装官方配置（-ConfigPath）或生成最小 mock 配置。

必填环境变量:
  MOA_ADMIN_PASSWORD   管理员密码（≥8 位，含字母+数字）。start 未提供时自动生成并
                       写入 .admin_password。注意: admin 用户仅在首次启动播种
                       （数据位于 <site-packages>\data\config.db），
                       更换密码需先删除该库文件。

可选环境变量:
  MOA_GATEWAY_KEY      网关 API Key（v3.1.1 运行期鉴权读 config.yaml 的
                       auth.gateway_api_keys；管理员 JWT 令牌同样可作 Bearer 使用）
  真实模型 Key          QWEN_API_KEY / ZHIPU_API_KEY / DEEPSEEK_API_KEY /
                       MOONSHOT_API_KEY / OPENAI_API_KEY / ANTHROPIC_API_KEY 等
                       （对应 config.yaml 各端点 api_key_env）。一个都不配时
                       全部端点为显式 MockProvider：响应带 X-MOA-Mock: true 头
                       与 body.mock=true，/health 显示 mock_endpoints_count。

关键端点:
  GET  /health            健康（无鉴权；含 mock/real 端点计数）
  GET  /health/ready      就绪探针（未就绪 503）
  POST /api/auth/login    {"username":"admin","password":...} -> {"token": JWT}
  POST /v1/moa/execute    Authorization: Bearer <JWT/APIKey>，OpenAI 式 messages
                          + preset/strategy/reference_count/critic_rounds/...
  GET  /v1/moa/presets    预设清单（需鉴权）

C# 侧对应: src/AeroAgent.Moa/Gateway/（MoaGatewayClient/GatewaySidecar/
GatewayOrchestrationFacade），环境变量 MOA_GATEWAY_URL/MOA_GATEWAY_KEY 与官方 CLI 一致。
======================================================================================
'@
}

switch ($Command) {
    'install' { Invoke-Install }
    'start'   { Invoke-Start }
    'stop'    { Invoke-Stop }
    'status'  { Invoke-Status }
    'info'    { Invoke-Info }
}
