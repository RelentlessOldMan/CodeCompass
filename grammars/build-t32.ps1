# Compiles the vendored tree-sitter-t32 (TRACE32 PRACTICE) grammar into a native win-x64 DLL that the
# TreeSitter.DotNet binding can load (exporting `tree_sitter_t32`), and drops it into
# grammars/prebuilt/win-x64/. The C sources under tree-sitter-t32/src are pre-generated and vendored, so
# this only needs a C compiler (MSVC) - no node / tree-sitter CLI required. Re-run this when the vendored
# grammar is updated; the produced DLL is committed so a normal build/release needs no compiler.
#
# Usage:  pwsh ./grammars/build-t32.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$src  = Join-Path $root "tree-sitter-t32\src"
$outDir = Join-Path $root "prebuilt\win-x64"
$out  = Join-Path $outDir "tree-sitter-t32.dll"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found - install Visual Studio (with the C++ workload) to build the grammar." }
$vs = & $vswhere -latest -property installationPath
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vs - install the 'Desktop development with C++' workload." }

Write-Host "Compiling tree-sitter-t32 -> $out ..."
# /O1 (small/fast enough for a parse table), /LD (DLL), explicit export of the grammar entry point.
$cl = "cl /nologo /O1 /LD /I `"$src`" `"$src\parser.c`" `"$src\scanner.c`" /Fe:`"$out`" /link /EXPORT:tree_sitter_t32"
cmd /c "`"$vcvars`" && $cl"
if (-not (Test-Path $out)) { throw "compile failed - $out was not produced" }

# Clean the compile by-products next to the DLL (.lib/.exp/.obj).
Get-ChildItem $outDir -Include *.lib,*.exp -File -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $root -Filter *.obj -File -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host ("Done: {0} ({1:N0} bytes). Commit it so builds/releases need no compiler." -f $out, (Get-Item $out).Length)
