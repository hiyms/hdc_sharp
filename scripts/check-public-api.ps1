# 公共 API 冻结基线回归：用 tools/PublicApiDump 反射导出库的公共 API 面，
# 与 scripts/public-api-baseline.txt（spec §5 冻结基线的落地快照）逐行比对。
# 基线更新须是"有意的公共 API 变更"：pwsh -File scripts/check-public-api.ps1 -UpdateBaseline
[CmdletBinding()]
param(
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot
$baselinePath = Join-Path $PSScriptRoot 'public-api-baseline.txt'

Push-Location $repoRoot
try {
    $actual = @(& dotnet run --project tools/PublicApiDump -c Release)
    if ($LASTEXITCODE -ne 0) {
        throw "公共 API 导出失败（退出码 $LASTEXITCODE）"
    }

    if ($UpdateBaseline) {
        Set-Content -Path $baselinePath -Value $actual -Encoding utf8
        Write-Host "已更新基线：$baselinePath（$($actual.Count) 行）"
        return
    }

    $baseline = @(Get-Content -Path $baselinePath | Where-Object { $_ -notmatch '^\s*#' })
    $diff = @(Compare-Object -ReferenceObject $baseline -DifferenceObject $actual -CaseSensitive)
    if ($diff.Count -gt 0) {
        Write-Host "公共 API 与冻结基线不一致（基线 $($baseline.Count) 行 / 实际 $($actual.Count) 行）：" -ForegroundColor Red
        foreach ($item in $diff) {
            $marker = if ($item.SideIndicator -eq '<=') { '基线有、实现无（缺失）' } else { '实现有、基线无（多余）' }
            Write-Host "  [$marker] $($item.InputObject)"
        }

        exit 1
    }

    Write-Host "公共 API 与冻结基线一致（$($actual.Count) 行成员）" -ForegroundColor Green
}
finally {
    Pop-Location
}
