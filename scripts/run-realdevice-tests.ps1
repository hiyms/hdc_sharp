# 真机集成测试运行脚本：设置 HDC_TEST_TARGET 后只跑 RealDevice 用例，并带挂起保护。
# 用法：pwsh -File scripts/run-realdevice-tests.ps1 -Target 192.168.2.161:44221
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Target,

    [string]$Configuration = 'Debug',

    [string]$HangTimeout = '10m'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $env:HDC_TEST_TARGET = $Target
    Write-Host "真机端点：$env:HDC_TEST_TARGET"

    dotnet test tests/HdcSharp.Tests `
        -c $Configuration `
        --filter 'RealDevice=true' `
        --blame-hang `
        --blame-hang-timeout $HangTimeout `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw "真机集成测试失败（退出码 $LASTEXITCODE）"
    }
}
finally {
    Remove-Item Env:HDC_TEST_TARGET -ErrorAction SilentlyContinue
    Pop-Location
}
