$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$BuildDir = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

Write-Host "== Generate C++ layout dumper"
python (Join-Path $PSScriptRoot "gen_layout_cpp.py")

Write-Host "== Build C# layout dumper"
dotnet build (Join-Path $PSScriptRoot "LayoutCheck\LayoutCheck.csproj") -v q
if ($LASTEXITCODE -ne 0) { throw "LayoutCheck build failed" }

Write-Host "== Run C# layout dumper"
dotnet run --project (Join-Path $PSScriptRoot "LayoutCheck\LayoutCheck.csproj") --no-build |
    Out-File -Encoding ascii (Join-Path $BuildDir "csharp_layout.txt")
if ($LASTEXITCODE -ne 0) { throw "C# layout dump failed" }

Write-Host "== Locate Visual Studio C++ toolchain"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found" }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "Visual Studio C++ toolchain not found" }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"

Write-Host "== Compile C++ layout dumper"
$cpp = Join-Path $BuildDir "layout_dump.cpp"
$exe = Join-Path $BuildDir "layout_dump.exe"
$obj = Join-Path $BuildDir "layout_dump.obj"
$apiDir = Join-Path $RepoRoot "ftdc2c_ctp\api"
$clCmd = "call `"$vcvars`" >nul && cl /nologo /std:c++17 /EHsc /I `"$apiDir`" `"$cpp`" /Fe:`"$exe`" /Fo:`"$obj`""
cmd /c $clCmd
if ($LASTEXITCODE -ne 0) { throw "C++ layout dump compile failed" }

Write-Host "== Run C++ layout dumper"
& $exe | Out-File -Encoding ascii (Join-Path $BuildDir "cpp_layout.txt")

Write-Host "== Compare layouts"
python (Join-Path $PSScriptRoot "compare_layout.py") `
    (Join-Path $BuildDir "cpp_layout.txt") `
    (Join-Path $BuildDir "csharp_layout.txt")
if ($LASTEXITCODE -ne 0) { throw "Layout check failed" }
