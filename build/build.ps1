# PCL MCDownload fixer 构建脚本（单文件，无需 Visual Studio / NuGet 还原）
# 通过 csc 响应文件传递参数，避免 PowerShell 对 /win32manifest:、/resource: 等参数的拆分问题
param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "PCL-MCDownload-fixer")
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root "src\PCLauncher"
$lib = Join-Path $root "lib"

function Find-Dotnet {
    $p = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path $p) { return $p }
    $c = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    throw "未找到 dotnet.exe（需要 .NET SDK 的 Roslyn 编译器）"
}
$dotnet = Find-Dotnet
$sdkRoot = Join-Path $env:ProgramFiles "dotnet\sdk"
$sdk = Get-ChildItem $sdkRoot -Directory |
       Where-Object { Test-Path (Join-Path $_.FullName "Roslyn\bincore\csc.dll") } |
       Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
       Select-Object -First 1
if (-not $sdk) { throw "未找到带 Roslyn 的 .NET SDK" }
$csc = Join-Path $sdk.FullName "Roslyn\bincore\csc.dll"

$pf86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
$refDir = Join-Path $pf86 "Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if (-not (Test-Path $refDir)) {
    $refDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8"
}
if (-not (Test-Path $refDir)) {
    # CI / 干净机器兜底：从 NuGet 拉参考程序集包并解压
    $pkg = Join-Path $env:TEMP "microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
    $zip = Join-Path $env:TEMP "net48refs"
    Write-Host "未找到 4.8 引用程序集，尝试从 NuGet 下载…"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg" -OutFile $pkg
    if (Test-Path $zip) { Remove-Item $zip -Recurse -Force }
    Expand-Archive -Path $pkg -DestinationPath $zip
    $refDir = Join-Path $zip "build\.NETFramework\v4.8"
}
if (-not (Test-Path $refDir)) { throw "未找到 .NET Framework 4.8 引用程序集" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$outExe = Join-Path $OutDir "PCL-MCDownload-fixer.exe"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("/nologo")
$lines.Add("/target:winexe")
$lines.Add("/platform:anycpu")
$lines.Add("/langversion:latest")
$lines.Add("/optimize+")
$lines.Add("/nostdlib+")
$lines.Add("/debug-")
$lines.Add("/deterministic+")
$lines.Add('/win32manifest:"' + (Join-Path $PSScriptRoot "app.manifest") + '"')
$lines.Add('/out:"' + $outExe + '"')
$lines.Add('/resource:"' + (Join-Path $src "assets\rules.json") + '",PCLauncher.rules.json')
$lines.Add('/resource:"' + (Join-Path $lib "Mono.Cecil.dll") + '",Mono.Cecil.dll')
$lines.Add('/resource:"' + (Join-Path $lib "0Harmony.dll") + '",0Harmony.dll')

foreach ($asm in @("mscorlib","System","System.Core","System.Web.Extensions","System.Windows.Forms","System.Drawing","System.Xml","System.IO.Compression","PresentationFramework","PresentationCore","WindowsBase")) {
    $lines.Add('/r:"' + (Join-Path $refDir "$asm.dll") + '"')
}
$lines.Add('/r:"' + (Join-Path $lib "Mono.Cecil.dll") + '"')
$lines.Add('/r:"' + (Join-Path $lib "0Harmony.dll") + '"')
Get-ChildItem -Path $src -Recurse -Filter *.cs | ForEach-Object {
    $lines.Add('"' + $_.FullName + '"')
}

$rsp = Join-Path $env:TEMP "PCL-MCDownload-fixer-csc.rsp"
Set-Content -Path $rsp -Value $lines -Encoding UTF8
Write-Host ("csc 响应文件：" + $rsp + "（" + $lines.Count + " 行）")

& $dotnet exec $csc "@$rsp"
$code = $LASTEXITCODE
Remove-Item $rsp -Force -ErrorAction SilentlyContinue
if ($code -ne 0) { throw "编译失败（exit $code）" }
Write-Host "PCL MCDownload fixer 构建完成（单文件）：$outExe" -ForegroundColor Green
