# Copyright (c) AeroCode
# run-eval-gate.ps1 - C3 eval CI gate: build -> test -> eval baseline (or validate existing) -> eval after -> compare.
# Any step failure exits with that step's exit code (non-zero = block). Paths are parameterized.
# No credentials are embedded: gateway credentials are only read by the eval runner from AEROCODE_EVAL_*
# environment variables (inherited by child processes); this script never sets or prints them.
# Usage (from repo root in CI):
#   powershell -ExecutionPolicy Bypass -File eval/ci/run-eval-gate.ps1
# Keep this file ASCII-only: Windows PowerShell 5.1 parses BOM-less scripts using the legacy ANSI codepage.
# ConstrainedLanguage-safe: cmdlets and operators only (no .NET type literals, no .NET method calls),
# so the gate also runs on hardened CI hosts that lock PowerShell down to ConstrainedLanguage mode.
[CmdletBinding()]
param(
    # Repo root (default = two levels up from this script, i.e. the grandparent of eval/ci/).
    [string]$RepoRoot = "",
    # dotnet executable (CI images have it on PATH; pass e.g. C:/Users/<user>/.dotnet/dotnet.exe locally).
    [string]$DotNet = "dotnet",
    # Eval and test projects (relative to RepoRoot; absolute paths are also accepted).
    [string]$EvalProject = "eval/AeroCode.Eval/AeroCode.Eval.csproj",
    [string]$TestProject = "tests/AeroCode.Tests/AeroCode.Tests.csproj",
    # Report paths (relative to RepoRoot; absolute paths are also accepted). baseline.md is read-only:
    # reused when present, never overwritten here.
    [string]$BeforeReport = "eval/reports/baseline.md",
    [string]$AfterReport = "eval/reports/after.md",
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($RepoRoot -ne "") {
    $repo = $RepoRoot
}

# Join a (possibly relative) parameter path with the repo root; absolute input passes through.
# Split-Path -IsAbsolute avoids [System.IO.Path]::IsPathRooted, which ConstrainedLanguage blocks.
function Join-RepoPath {
    param([string]$Path)
    if (Split-Path -Path $Path -IsAbsolute) { return $Path }
    return (Join-Path $repo $Path)
}

# Fail the gate when the last native command returned non-zero.
function Assert-GateStep {
    param([string]$Name)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[gate] FAIL: $Name (exit=$LASTEXITCODE) - CI gate blocked"
        exit $LASTEXITCODE
    }
    Write-Host "[gate] OK: $Name"
}

# Eval runner DLL: prefer an existing build, Debug first, then Release (R2 repair LOW-C: the
# hardcoded bin/Debug path broke the gate on Release-only CI images). ASCII-only, cmdlets-only
# (Split-Path/Join-Path/Test-Path), so this stays ConstrainedLanguage-safe.
$evalDll = ""
foreach ($config in @("Debug", "Release")) {
    $candidate = Join-RepoPath ("eval/AeroCode.Eval/bin/" + $config + "/net9.0/AeroCode.Eval.dll")
    if (Test-Path -LiteralPath $candidate) {
        $evalDll = $candidate
        break
    }
}
if ($evalDll -eq "") {
    # No prebuilt output found: fall back to the conventional Debug path (the build step below
    # produces it; the runner invocation will surface a clear failure if neither config exists).
    $evalDll = Join-RepoPath "eval/AeroCode.Eval/bin/Debug/net9.0/AeroCode.Eval.dll"
}
$before = Join-RepoPath $BeforeReport
$after = Join-RepoPath $AfterReport

Push-Location $repo
try {
    if (-not $SkipBuild) {
        Write-Host "[gate] ==> build eval project"
        & $DotNet build (Join-RepoPath $EvalProject) -p:NodeReuse=false --nologo -v q
        Assert-GateStep "build eval project"
    }

    if (-not $SkipTests) {
        Write-Host "[gate] ==> test suite"
        & $DotNet test (Join-RepoPath $TestProject) -p:NodeReuse=false --nologo -v q
        Assert-GateStep "test suite"
    }

    # Eval baseline: when the report exists, reuse it read-only and check all three metric sections;
    # otherwise generate it once (first baseline landing). Substring test uses -notlike instead of
    # string.Contains, which ConstrainedLanguage blocks.
    if (Test-Path -LiteralPath $before) {
        Write-Host "[gate] ==> validate existing baseline"
        $text = Get-Content -Raw -LiteralPath $before
        $missing = @()
        foreach ($key in @("multi_turn_hallucination_rate", "checkpoint_pass_rate", "unit_cost_completion")) {
            if ($text -notlike ("*(" + $key + ")*")) { $missing += $key }
        }
        if ($missing.Count -gt 0) {
            Write-Host ("[gate] baseline.md missing metric sections: " + ($missing -join ", ") + " - fail-closed")
            $global:LASTEXITCODE = 6
        } else {
            $global:LASTEXITCODE = 0
        }
        Assert-GateStep "validate existing baseline"
    } else {
        Write-Host "[gate] ==> eval baseline (first run)"
        & $DotNet $evalDll baseline
        Assert-GateStep "eval baseline (first run)"
    }

    Write-Host "[gate] ==> eval after report"
    & $DotNet $evalDll after --out $after
    Assert-GateStep "eval after report"

    # Compare: 0 = pass; 5 = measured metric regressed; 6 = parse fail-closed; other = upstream error.
    Write-Host "[gate] ==> eval compare (CI gate)"
    & $DotNet $evalDll compare --before $before --after $after
    Assert-GateStep "eval compare (CI gate)"

    Write-Host "[gate] PASS: eval gate completed (compare exit 0)"
    exit 0
} finally {
    Pop-Location
}
