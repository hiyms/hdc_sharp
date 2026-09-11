# NuGet 打包：产出 artifacts/HdcSharp.<版本>.nupkg 并列出包内容（校验 XML 文档与 README 已入包）。
# 用法：pwsh -File scripts/pack.ps1 [-Configuration Release] [-OutputDirectory artifacts]
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }

Push-Location $repoRoot
try {
    & dotnet pack src/HdcSharp -c $Configuration -o $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack 失败（退出码 $LASTEXITCODE）"
    }

    $package = Get-ChildItem -Path $outputPath -Filter 'HdcSharp.*.nupkg' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    Write-Host "包文件：$($package.FullName)"

    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        Write-Host '包内容：'
        foreach ($entry in $archive.Entries) {
            Write-Host ("  {0} ({1} 字节)" -f $entry.FullName, $entry.Length)
        }

        foreach ($required in @('lib/net8.0/HdcSharp.dll', 'lib/net8.0/HdcSharp.xml', 'README.md')) {
            if (-not ($archive.Entries | Where-Object { $_.FullName -eq $required })) {
                throw "包内缺少 $required"
            }
        }

        Write-Host '包内容校验通过：dll + XML 文档 + README 均在包内' -ForegroundColor Green
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    Pop-Location
}
