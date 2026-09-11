# 注释三分法粗筛（spec §2.1）：扫描 src/ 下全部 .cs（排除 obj/bin），
#   1) 硬门禁：块注释 /* */、TODO/FIXME/HACK/XXX、疑似注释掉的代码 → 直接失败
#   2) 人工确认清单：全部非 /// 行注释（按"平台注释只解释为什么"的标准逐条确认）
#   3) 预算门禁：非 /// 注释行数不得超过 -MaxComments（新增注释须有意确认并显式抬升预算）
#      预算沿革：84 → 85（2026-09-11 应用安装成功判定 inline 依据，见验证记录 §11）
# 用法：pwsh -File scripts/check-comments.ps1 [-MaxComments 84]
[CmdletBinding()]
param(
    [int]$MaxComments = 85
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot

$files = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

$review = [System.Collections.Generic.List[string]]::new()
$violations = [System.Collections.Generic.List[string]]::new()
$commentedOutCode = '^\s*//\s*(using |var |if\b|else\b|for\b|foreach\b|while\b|return\b|await\b|throw\b|public\b|private\b|internal\b|protected\b|\{|\}|\[)'

foreach ($file in $files) {
    $relative = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++
        $index = $line.IndexOf('//')
        if ($index -lt 0) {
            if ($line.Contains('/*')) {
                $violations.Add("${relative}:${lineNumber}: 块注释（三分法禁止）：$($line.Trim())")
            }

            continue
        }

        if ($line.TrimStart().StartsWith('///')) {
            continue
        }

        # 字符串里的 URL / 协议串（如 "http://"）不是注释
        if ($index -gt 0 -and $line[$index - 1] -eq ':') {
            continue
        }

        $text = $line.Trim()
        $review.Add("${relative}:${lineNumber}: $text")
        if ($text -match 'TODO|FIXME|HACK|XXX') {
            $violations.Add("${relative}:${lineNumber}: 散注（三分法禁止）：$text")
        }

        if ($text -match $commentedOutCode) {
            $violations.Add("${relative}:${lineNumber}: 疑似注释掉的代码：$text")
        }
    }
}

Write-Host "非 /// 注释（平台注释）清单，共 $($review.Count) 行，逐条确认是否只解释"为什么"：" -ForegroundColor Cyan
$review | ForEach-Object { Write-Host "  $_" }

if ($violations.Count -gt 0) {
    Write-Host "注释三分法违规 $($violations.Count) 条：" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}

if ($review.Count -gt $MaxComments) {
    Write-Host "平台注释数 $($review.Count) 超出预算 $MaxComments：请逐条确认必要性后显式抬升 -MaxComments" -ForegroundColor Red
    exit 1
}

Write-Host "注释三分法检查通过：无 TODO/块注释/注释掉的代码，平台注释 $($review.Count)/$MaxComments 在预算内" -ForegroundColor Green
