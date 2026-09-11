# README 代码块编译校验：抽取 README.md 中全部 ```csharp 代码块拼成一个控制台程序，
# 在临时工程中引用库工程编译（Nullable + IsAotCompatible + TreatWarningsAsErrors），
# 防止文档示例与公共 API 漂移（示例必须只用库的公共 API）。
# 用法：pwsh -File scripts/check-readme-snippets.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot
$readmePath = Join-Path $repoRoot 'README.md'
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) 'hdcsharp-readme-check'

$readme = Get-Content -LiteralPath $readmePath -Raw
$blocks = [regex]::Matches($readme, '(?ms)^```csharp\r?\n(.*?)^```')
if ($blocks.Count -eq 0) {
    throw "README.md 中未找到 ```csharp 代码块"
}

$program = ($blocks | ForEach-Object { $_.Groups[1].Value }) -join "`n"
if ($program -notmatch 'HdcSharp') {
    throw "README 代码块未引用 HdcSharp 公共 API"
}

if (Test-Path $tempDir) {
    Remove-Item -Recurse -Force $tempDir
}

New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$libraryPath = Join-Path $repoRoot 'src/HdcSharp/HdcSharp.csproj'
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$libraryPath" />
  </ItemGroup>
</Project>
"@

Set-Content -Path (Join-Path $tempDir 'ReadmeSnippets.csproj') -Value $project -Encoding utf8
Set-Content -Path (Join-Path $tempDir 'Program.cs') -Value $program -Encoding utf8

Push-Location $tempDir
try {
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "README 代码块编译失败（退出码 $LASTEXITCODE）"
    }

    Write-Host "README 代码块编译通过（$($blocks.Count) 个 ```csharp 块）" -ForegroundColor Green
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
