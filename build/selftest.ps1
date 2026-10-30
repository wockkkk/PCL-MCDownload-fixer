# PCL 跨版本拦截点自测脚本
# 用法：
#   .\build\selftest.ps1 -PclExe "D:\PCL\Plain Craft Launcher 2.exe"
#   .\build\selftest.ps1 -PclExe "路径1","路径2"   # 批量对比多个版本（无默认扫描目录）
param(
    [string[]]$PclExe = @(),
    [string]$Launcher = (Join-Path (Split-Path $PSScriptRoot -Parent) "PCL-MCDownload-fixer\PCL-MCDownload-fixer.exe"),
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "runtime\reports")
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Launcher)) { throw "未找到 $Launcher，请先运行 build\build.ps1" }
if ($PclExe.Count -eq 0) {
    throw "请用 -PclExe 指定至少一个 Plain Craft Launcher 2.exe 路径"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$rows = @()
foreach ($exe in $PclExe) {
    Write-Host "扫描 $exe"
    & $Launcher --scan --pcl $exe --out $OutDir | Out-Null
    $txt = Get-ChildItem $OutDir -Filter "PCL-scan-*.txt" | Where-Object { $_.Name -notlike "*.sha256.txt" } |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $content = Get-Content $txt.FullName -Raw
    $asmLine = [regex]::Match($content, "程序集\s*: (.*)").Groups[1].Value.Trim()
    $ver = ($asmLine -split " ")[-1]
    $sites = ([regex]::Matches($content, "\[(.+?)\] (\S+::\S+)")).Count
    $kinds = [regex]::Matches($content, "\[(.+?)\] ") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $rows += [pscustomobject]@{
        Version = $ver
        Exe = $exe
        Sites = $sites
        Kinds = ($kinds -join ", ")
        Report = $txt.Name
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
$cmp = Join-Path $OutDir "version-comparison.md"
"# PCL 版本兼容性对比" | Set-Content $cmp -Encoding UTF8
"" | Add-Content $cmp -Encoding UTF8
"| 版本 | 拦截点 | 形态 | 报告 |" | Add-Content $cmp -Encoding UTF8
"| --- | --- | --- | --- |" | Add-Content $cmp -Encoding UTF8
foreach ($r in $rows) { "| $($r.Version) | $($r.Sites) | $($r.Kinds) | $($r.Report) |" | Add-Content $cmp -Encoding UTF8 }
Write-Host "对比报告：$cmp" -ForegroundColor Green
