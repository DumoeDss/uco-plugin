<#
.SYNOPSIS
    Build the three internal framework projects (ReflectorNet, Uco.Framework.Common,
    Uco.Framework) from source and deploy the netstandard2.1 DLLs as static plugin
    assets.

.DESCRIPTION
    The Unity plugin consumes three internal framework DLLs at compile time via
    asmdef precompiledReferences: ReflectorNet.dll, Uco.Framework.Common.dll,
    Uco.Framework.dll.  These are now built from the local source repos and committed
    as static assets rather than fetched from NuGet at runtime by the
    DependencyResolver.

    This script:
      1. Builds ReflectorNet.csproj            -> ReflectorNet.dll
      2. Builds Uco.Framework.Common.csproj    -> Uco.Framework.Common.dll
      3. Builds Uco.Framework.csproj           -> Uco.Framework.dll
         (all for netstandard2.1 / Release)
      4. Copies the three DLLs to
         uco-unity-project/Assets/Plugins/NuGet/
         preserving existing .meta files (Unity GUIDs / import settings).
      5. Reports the deployed file size of each DLL.

    External NuGet dependencies (SignalR.Client, Microsoft.Extensions.*, R3, etc.)
    remain resolver-managed and are NOT touched by this script.

.PARAMETER Configuration
    Build configuration. Default 'Release'.

.PARAMETER SkipBuild
    Reuse existing build output instead of running 'dotnet build'.
    Fails if the expected output DLLs are missing.

.EXAMPLE
    .\commands\build-framework-dlls.ps1

.EXAMPLE
    .\commands\build-framework-dlls.ps1 -Configuration Debug
#>

#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# --- Path resolution ---------------------------------------------------------
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ucoPluginDir = Split-Path -Parent $scriptDir                              # ...\uco-plugin
$workspace   = Split-Path -Parent $ucoPluginDir                            # ...\unity-copilot

$reflectorNetDir     = Join-Path $ucoPluginDir 'ReflectorNet'
$ucoFrameworkDir     = Join-Path $ucoPluginDir 'uco-framework'

# Source project files
$srcProj = [ordered]@{
    ReflectorNet      = Join-Path $reflectorNetDir 'ReflectorNet\ReflectorNet.csproj'
    UcoFrameworkCommon   = Join-Path $ucoFrameworkDir   'Uco.Framework.Common\Uco.Framework.Common.csproj'
    UcoFramework         = Join-Path $ucoFrameworkDir   'Uco.Framework\Uco.Framework.csproj'
}

$framework = 'netstandard2.1'

# Build output -> deployed DLL name mapping
$srcDll = [ordered]@{
    'ReflectorNet.dll'        = Join-Path $reflectorNetDir    "ReflectorNet\bin\$Configuration\$framework\ReflectorNet.dll"
    'Uco.Framework.Common.dll' = Join-Path $ucoFrameworkDir    "Uco.Framework.Common\bin\$Configuration\$framework\Uco.Framework.Common.dll"
    'Uco.Framework.dll'        = Join-Path $ucoFrameworkDir    "Uco.Framework\bin\$Configuration\$framework\Uco.Framework.dll"
}

$dstDir = Join-Path $ucoPluginDir 'uco-unity-project\Assets\Plugins\NuGet'

# --- Validate source paths ---------------------------------------------------
if (-not (Test-Path $reflectorNetDir)) {
    throw "ReflectorNet source not found at: $reflectorNetDir`nExpected as a subdirectory of uco-plugin."
}
if (-not (Test-Path $ucoFrameworkDir)) {
    throw "uco-framework source not found at: $ucoFrameworkDir`nExpected as a subdirectory of uco-plugin."
}
if (-not (Test-Path $dstDir)) {
    throw "Destination not found: $dstDir`nIs uco-unity-project checked out?"
}

# --- Locate dotnet -----------------------------------------------------------
function Resolve-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $fallbacks = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    )
    foreach ($p in $fallbacks) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    throw "dotnet CLI not found on PATH or at common install locations."
}

# --- Build -------------------------------------------------------------------
if (-not $SkipBuild) {
    $dotnet = Resolve-Dotnet
    Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray
    Write-Host "Configuration: $Configuration / $framework" -ForegroundColor DarkGray
    Write-Host ""

    # Build in dependency order: ReflectorNet -> Uco.Framework.Common -> Uco.Framework.
    # Uco.Framework has a ProjectReference to Uco.Framework.Common, so building it
    # will also rebuild Common; building Common first ensures the output exists
    # even if the Uco.Framework build is skipped partway.
    foreach ($pair in $srcProj.GetEnumerator()) {
        $label = $pair.Key
        $proj  = $pair.Value

        if (-not (Test-Path $proj)) {
            throw "Project file missing: $proj"
        }

        Write-Host "==> Building $label" -ForegroundColor Cyan
        Write-Host "    $proj"
        & $dotnet build $proj `
            -c $Configuration -f $framework `
            -p:GeneratePackageOnBuild=false `
            -p:TargetFrameworks=$framework `
            -v minimal
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build FAILED for $label (exit code $LASTEXITCODE).`nNo DLLs were copied to the destination."
        }
        Write-Host "    OK" -ForegroundColor Green
        Write-Host ""
    }
}

# --- Verify build output exists ----------------------------------------------
$missing = @()
foreach ($pair in $srcDll.GetEnumerator()) {
    if (-not (Test-Path $pair.Value)) {
        $missing += $pair.Value
    }
}
if ($missing.Count -gt 0) {
    throw @"
Expected build output missing:
$($missing -join "`n")
Run without -SkipBuild to compile the projects first.
"@
}

# --- Copy DLLs (preserve .meta files) ----------------------------------------
Write-Host "==> Deploying DLLs to $dstDir" -ForegroundColor Cyan
foreach ($pair in $srcDll.GetEnumerator()) {
    $dllName = $pair.Key
    $src     = $pair.Value
    $dst     = Join-Path $dstDir $dllName

    Copy-Item -Path $src -Destination $dst -Force
    $size = (Get-Item $dst).Length
    Write-Host ("    {0,-25} {1,10:N0} bytes" -f $dllName, $size)

    # Warn if .meta is missing — Unity requires it.
    $metaPath = "$dst.meta"
    if (-not (Test-Path $metaPath)) {
        Write-Host "    WARNING: .meta file missing for $dllName" -ForegroundColor Yellow
        Write-Host "             Unity will generate one on next import (GUID will change)." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Reopen Unity Editor (or trigger Reimport) to load the refreshed plugin DLLs."
