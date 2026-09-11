# 生成 API + 概念文档站点（docfx）：工具还原 → 元数据 → 站点构建 → 产物校验。
#   产物：api/（API 元数据 yml，已 gitignore）、artifacts/docs/（HTML 站点，已 gitignore）
#   说明：docfx 为本地工具（dotnet-tools.json）。首次在干净机器上需网络还原 NuGet 包；
#         之后元数据与站点构建均离线完成（模板随包提供）。
# 用法：
#   pwsh -File scripts/build-docs.ps1            # 构建
#   pwsh -File scripts/build-docs.ps1 -Serve     # 构建后本地预览（Ctrl+C 结束）
#   pwsh -File scripts/build-docs.ps1 -Clean     # 先删产物再构建
#   pwsh -File scripts/build-docs.ps1 -Serve -OpenBrowser          # 预览并自动打开浏览器
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Serve,
    [int]$Port = 8080,
    # docfx 2.78 的 modern 与 classic 模板都把顶部导航/左侧栏放在运行时，
    # 由 JS 读取 <meta name="docfx:navrel|tocrel"> 后 fetch toc.json 渲染；
    # 浏览器禁止 file:// 页面发起 fetch，故站点**必须经 HTTP 打开**（见脚本末尾提示）
    [switch]$OpenBrowser
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$repoRoot = Split-Path -Parent $PSScriptRoot
$siteDir = 'artifacts/docs'

Push-Location $repoRoot
try {
    if ($Clean) {
        Write-Host '清理产物：api/ artifacts/docs/' -ForegroundColor Yellow
        Remove-Item -Recurse -Force api, $siteDir -ErrorAction SilentlyContinue
    }

    Write-Host '还原本地工具（docfx）' -ForegroundColor Cyan
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore 失败（退出码 $LASTEXITCODE）" }

    # 单条 docfx 命令同时跑 metadata 与 build：docfx.json 内 metadata 段 + build 段。
    # 不得使用 -SuppressWarnings：警告（失效链接等）应当让门禁失败，避免文档静默腐化。
    # 站点内的 README 副本：由仓库根 README.md 生成，避免两份手写文档漂移。
    # 相对链接需上跳一层（site/ → 仓库根）；若 README 出现新的相对链接形态，
    # docfx 的链接校验会以警告形式暴露，进而让本步骤失败（脚本不静默放过）。
    Write-Host '生成站点内的 README 副本（site/readme-full.md）' -ForegroundColor Cyan
    $readme = Get-Content 'README.md' -Raw
    $readme = $readme -replace '\]\((docs|scripts|samples|tests|src)/', '](../$1/'
    Set-Content 'site/readme-full.md' $readme -NoNewline -Encoding utf8

    Write-Host '生成文档（metadata + build）' -ForegroundColor Cyan
    dotnet docfx docfx.json
    if ($LASTEXITCODE -ne 0) { throw "docfx 生成失败（退出码 $LASTEXITCODE）" }

    # 产物校验：首页/API 页/搜索索引/API 目录树必须存在，且 API 页数量与公共类型数量匹配
    $required = @(
        'index.html',
        'toc.html',
        'index.json',
        'api/toc.html',
        'api/HdcSharp.HdcHost.html',
        'api/HdcSharp.HdcDevice.html'
    )
    $required += 'site/readme-full.html'
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $siteDir $file))) { throw "缺少产物 $siteDir/$file" }
    }

    $apiPages = (Get-ChildItem (Join-Path $siteDir 'api') -Filter '*.html').Count
    $publicTypes = (Get-Content 'scripts/public-api-baseline.txt' | Where-Object { $_ -like 'TYPE *' }).Count
    if ($apiPages -lt $publicTypes) {
        throw "API 页数量 $apiPages 少于公共类型数 $publicTypes，可能有类型未生成文档"
    }

    $pages = (Get-ChildItem $siteDir -Recurse -Filter '*.html').Count
    Write-Host "文档站点已生成：$siteDir（$pages 个 HTML 页面，其中 API 页 $apiPages 个 / 公共类型 $publicTypes 个）" -ForegroundColor Green
    Write-Host "注意：站点必须经 HTTP 访问。顶部导航/左侧栏/搜索都由 JS 在运行时 fetch toc.json、index.json 得到，" -ForegroundColor Yellow
    Write-Host "      而浏览器禁止 file:// 页面发起 fetch（会报 TypeError: Failed to fetch），直接双击 HTML 只会看到正文、没有导航与侧栏。" -ForegroundColor Yellow
    Write-Host "      预览方式：本脚本加 -Serve；或对 $siteDir 起任意静态服务器，例如" -ForegroundColor Yellow
    Write-Host "        python -m http.server $Port --directory $siteDir" -ForegroundColor Yellow
    Write-Host "        npx --yes serve $siteDir -l $Port" -ForegroundColor Yellow

    if ($Serve) {
        Write-Host "本地预览：http://localhost:$Port/（Ctrl+C 结束）" -ForegroundColor Cyan
        if ($OpenBrowser) { Start-Process "http://localhost:$Port/" }
        dotnet docfx serve $siteDir --port $Port
    }
}
finally {
    Pop-Location
}
