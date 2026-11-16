param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipLayoutCheck
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NativeProj = Join-Path $RepoRoot "ftdc2c_ctp\ftdc2c_ctp.vcxproj"
$ManagedProj = Join-Path $RepoRoot "FtdcNet.CTP\FtdcNet.CTP.csproj"
$SolutionDir = $RepoRoot + "\"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found" }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "Visual Studio C++ toolchain not found" }
$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

Write-Host "== Build native bridge x64"
& $msbuild $NativeProj /p:Configuration=$Configuration /p:Platform=x64 `
    /p:SolutionDir=$SolutionDir /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "native x64 build failed" }

Write-Host "== Build native bridge x86"
& $msbuild $NativeProj /p:Configuration=$Configuration /p:Platform=Win32 `
    /p:SolutionDir=$SolutionDir /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "native x86 build failed" }

$collectorSrc = Join-Path $RepoRoot "tools\vendor\win-x64\WinDataCollect.dll"
$collectorDst = Join-Path $RepoRoot "$Configuration\runtimes\win-x64\native\WinDataCollect.dll"
if (Test-Path $collectorSrc) {
    Write-Host "== Stage WinDataCollect.dll (x64)"
    Copy-Item -Force $collectorSrc $collectorDst
} else {
    Write-Warning "WinDataCollect.dll not found at $collectorSrc; CTP_GetSystemInfo(x64) will not be packaged."
}

Write-Host "== Verify ABI"
python (Join-Path $PSScriptRoot "ctpgen\verify_abi.py")
if ($LASTEXITCODE -ne 0) { throw "ABI verification failed" }

if (-not $SkipLayoutCheck) {
    Write-Host "== Verify native/managed struct layouts"
    & (Join-Path $PSScriptRoot "layout-check\run_layout_check.ps1")
    if ($LASTEXITCODE -ne 0) { throw "layout check failed" }
}

Write-Host "== Build managed wrapper"
dotnet build $ManagedProj -c $Configuration -v m
if ($LASTEXITCODE -ne 0) { throw "managed build failed" }

$nupkg = Get-ChildItem (Join-Path $RepoRoot "FtdcNet.CTP\bin\$Configuration") -Filter "FtdcNet.CTP.*.nupkg" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($nupkg) {
    Write-Host "== Package: $($nupkg.FullName)"
} else {
    throw "NuGet package not found"
}
