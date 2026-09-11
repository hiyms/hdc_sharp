# Task 20 验收：一条命令复跑全部收尾门禁，逐项 PASS/FAIL 汇总。
#   1) dotnet build HdcSharp.sln -c Release           → 0 警告 0 错误
#   2) dotnet test tests/HdcSharp.Tests -c Release    → 通过 210、跳过 25（真机用例）
#   3) dotnet publish samples/AotConsumer -r win-x64  → AOT 门禁 + 产物 --help 冒烟
#   4) dotnet pack src/HdcSharp -c Release            → NuGet 包（含 XML 文档与 README）
#   5) scripts/check-public-api.ps1                   → 公共 API 与 spec §5 冻结基线一致
#   6) scripts/check-comments.ps1                     → 注释三分法粗筛
#   7) scripts/check-readme-snippets.ps1              → README 代码块编译校验（防文档漂移）
# 用法：pwsh -File scripts/verify-all.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

Push-Location $repoRoot
try {
    function Invoke-Step {
        param([string]$Name, [scriptblock]$Body)

        Write-Host ''
        Write-Host "=== $Name ===" -ForegroundColor Cyan
        $ok = $false
        try {
            & $Body
            if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
                throw "外部命令退出码 $LASTEXITCODE"
            }

            $ok = $true
        }
        catch {
            Write-Host $_.Exception.Message -ForegroundColor Red
        }

        $results.Add([pscustomobject]@{ 步骤 = $Name; 结果 = if ($ok) { 'PASS' } else { 'FAIL' } })
    }

    Invoke-Step '1/7 构建 HdcSharp.sln（Release）' {
        dotnet build HdcSharp.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '2/7 测试 tests/HdcSharp.Tests（Release）' {
        dotnet test tests/HdcSharp.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw "测试失败（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '3/7 AOT 门禁：publish samples/AotConsumer（win-x64）' {
        dotnet publish samples/AotConsumer -r win-x64 -c Release
        if ($LASTEXITCODE -ne 0) { throw "AOT 发布失败（退出码 $LASTEXITCODE）" }

        $exe = 'samples/AotConsumer/bin/Release/net8.0/win-x64/publish/AotConsumer.exe'
        if (-not (Test-Path $exe)) { throw "缺少 AOT 产物 $exe" }

        & $exe --help
        if ($LASTEXITCODE -ne 0) { throw "AOT 产物 --help 冒烟失败（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '4/7 打包 src/HdcSharp（Release）' {
        dotnet pack src/HdcSharp -c Release
        if ($LASTEXITCODE -ne 0) { throw "打包失败（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '5/7 公共 API 冻结基线比对' {
        pwsh -NoProfile -File scripts/check-public-api.ps1
        if ($LASTEXITCODE -ne 0) { throw "公共 API 与冻结基线不一致（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '6/7 注释三分法粗筛' {
        pwsh -NoProfile -File scripts/check-comments.ps1 | Select-Object -Last 1 | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "注释三分法检查失败（退出码 $LASTEXITCODE）" }
    }

    Invoke-Step '7/7 README 代码块编译校验' {
        pwsh -NoProfile -File scripts/check-readme-snippets.ps1 | Select-Object -Last 1 | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "README 代码块编译失败（退出码 $LASTEXITCODE）" }
    }

    Write-Host ''
    Write-Host '=== 汇总 ===' -ForegroundColor Cyan
    $results | Format-Table -AutoSize | Out-String | Write-Host

    if ($results | Where-Object { $_.结果 -ne 'PASS' }) {
        Write-Host '存在失败项' -ForegroundColor Red
        exit 1
    }

    Write-Host '全部门禁通过' -ForegroundColor Green
}
finally {
    Pop-Location
}
