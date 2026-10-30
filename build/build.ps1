# PCL MCDownload fixer 构建脚本（单文件，无需 Visual Studio / NuGet 还原）
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
$sources = Get-ChildItem -Path $src -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$refs = @("mscorlib","System","System.Core","System.Web.Extensions","System.Windows.Forms","System.Drawing","System.Xml","System.IO.Compression","PresentationFramework","PresentationCore","WindowsBase") |
        ForEach-Object { "/r:" + (Join-Path $refDir "$_.dll") }
$refs += "/r:" + (Join-Path $lib "Mono.Cecil.dll")
$refs += "/r:" + (Join-Path $lib "0Harmony.dll")

$outExe = Join-Path $OutDir "PCL-MCDownload-fixer.exe"
$cscArgs = @("exec", $csc, "/nologo", "/target:winexe", "/platform:x86", "/langversion:latest",
          "/optimize+", "/nostdlib+", "/debug-", "/deterministic+",
          "/win32manifest:" + (Join-Path $PSScriptRoot "app.manifest"),
          "/out:$outExe",
          "/resource:" + (Join-Path $src "assets\rules.json") + ",PCLauncher.rules.json",
          "/resource:" + (Join-Path $lib "Mono.Cecil.dll") + ",Mono.Cecil.dll",
          "/resource:" + (Join-Path $lib "0Harmony.dll") + ",0Harmony.dll") + $refs + $sources

& $dotnet @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Write-Host "PCL MCDownload fixer 构建完成（单文件）：$outExe" -ForegroundColor Green
